#!/usr/bin/env bash
set -euo pipefail
export LC_ALL=C

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
dotnet_root="$(cd -- "$script_dir/.." && pwd)"
metadata="$dotnet_root/linux/package.env"

if (($# < 2)); then
    echo "Usage: $0 <linux-x64|linux-arm64> <package> [package ...]" >&2
    exit 64
fi
rid="$1"
shift

# shellcheck source=../linux/package.env
source "$metadata"
case "$rid" in
    linux-x64) expected_machine="Advanced Micro Devices X86-64"; package_arch_deb=amd64; package_arch_rpm=x86_64 ;;
    linux-arm64) expected_machine="AArch64"; package_arch_deb=arm64; package_arch_rpm=aarch64 ;;
    *) echo "Unsupported Linux RID: $rid" >&2; exit 64 ;;
esac

for command in ar file readelf sha256sum strings tar python3; do
    command -v "$command" >/dev/null 2>&1 ||
        { echo "$command is required for Linux package validation." >&2; exit 69; }
done

work="$dotnet_root/artifacts/linux-package-validation/$rid-$$"
rm -rf -- "$work"
mkdir -p "$work"
trap 'rm -rf -- "$work"' EXIT

validate_tar_modes() {
    python3 - "$1" <<'PY'
import sys
import tarfile

executables = {
    "Devolutions.Terminal",
    "dt",
    "dt-pty-host",
    "Install-LinuxDesktopIntegration.sh",
    "devolutions-terminal-x-terminal-emulator",
    "AppRun",
    "postinst",
    "postrm",
}
with tarfile.open(sys.argv[1], "r:gz") as archive:
    for item in archive:
        parts = item.name.removeprefix("./").split("/")
        assert not item.name.startswith("/") and ".." not in parts, item.name
        if item.isdir():
            expected = 0o755
        elif item.issym():
            expected = 0o777
        elif item.name.rsplit("/", 1)[-1] in executables:
            expected = 0o755
        else:
            expected = 0o644
        assert item.mode == expected, (item.name, oct(item.mode), oct(expected))
        assert item.uid == 0 and item.gid == 0, item.name
PY
}

validate_control_metadata() {
    local package="$1"
    local control="$work/control"
    rm -rf "$control"
    mkdir -p "$control"
    ar t "$package" | grep -Fx debian-binary >/dev/null
    ar t "$package" | grep -Fx control.tar.gz >/dev/null
    ar t "$package" | grep -Fx data.tar.gz >/dev/null
    ar p "$package" control.tar.gz | tar -xzf - -C "$control"
    ar p "$package" control.tar.gz > "$work/control.tar.gz"
    ar p "$package" data.tar.gz > "$work/data.tar.gz"
    validate_tar_modes "$work/control.tar.gz"
    validate_tar_modes "$work/data.tar.gz"
    grep -Fx "Package: $PACKAGE_NAME" "$control/control" >/dev/null
    grep -Fx "Architecture: $package_arch_deb" "$control/control" >/dev/null
    grep -F "Depends: $RUNTIME_DEPENDENCIES_DEB" "$control/control" >/dev/null
    test -x "$control/postinst"
    test -x "$control/postrm"
}

validate_rpm_metadata() {
    local package="$1"
    command -v rpm >/dev/null 2>&1 && command -v rpm2cpio >/dev/null 2>&1 ||
        { echo "rpm and rpm2cpio are required to validate RPM output (Ubuntu: sudo apt-get install rpm)." >&2; exit 69; }
    test "$(rpm -qp --qf '%{NAME}' "$package")" = "$PACKAGE_NAME"
    test "$(rpm -qp --qf '%{ARCH}' "$package")" = "$package_arch_rpm"
    test "$(rpm -qp --qf '%{LICENSE}' "$package")" = "$LICENSE_ID"
    local requirements
    requirements="$(rpm -qp --requires "$package")"
    IFS=',' read -r -a expected_requirements <<<"$RUNTIME_DEPENDENCIES_RPM"
    for requirement in "${expected_requirements[@]}"; do
        requirement="${requirement# }"
        grep -Fx "$requirement" <<<"$requirements" >/dev/null
    done
    local listing
    listing="$(rpm -qplv "$package")"
    for executable in Devolutions.Terminal dt dt-pty-host; do
        grep -E -- "^-rwxr-xr-x .* /opt/devolutions-terminal/$executable$" \
            <<<"$listing" >/dev/null
    done
    for library in libSkiaSharp.so libHarfBuzzSharp.so libghostty-vt.so; do
        grep -E -- "^-rw-r--r-- .* /opt/devolutions-terminal/$library$" \
            <<<"$listing" >/dev/null
    done
}

extract_appimage() {
    local package="$1"
    local root="$2"
    readelf -h "$package" | grep -F "Machine:" | grep -F "$expected_machine" >/dev/null ||
        { echo "AppImage runtime architecture does not match $rid." >&2; exit 1; }
    if readelf -S "$package" | grep -E '\.debug(_|$)' >/dev/null; then
        echo "AppImage runtime contains debug sections." >&2
        exit 1
    fi
    if readelf -d "$package" 2>/dev/null | sed -n \
        's/.*Shared library: \[\([^]]*\)\].*/\1/p' | grep / >/dev/null; then
        echo "AppImage runtime has an absolute NEEDED dependency." >&2
        exit 1
    fi
    python3 - "$package" "$work/appimage-offset" <<'PY'
import pathlib
import sys
data = pathlib.Path(sys.argv[1]).read_bytes()
offset = data.rfind(b"hsqs")
if offset < 0:
    raise SystemExit("AppImage has no embedded SquashFS filesystem")
pathlib.Path(sys.argv[2]).write_text(str(offset), encoding="ascii")
PY
    command -v unsquashfs >/dev/null 2>&1 ||
        { echo "unsquashfs is required for architecture-independent AppImage validation (Ubuntu: sudo apt-get install squashfs-tools)." >&2; exit 69; }
    local listing="$work/appimage-listing"
    unsquashfs -lln -o "$(cat "$work/appimage-offset")" "$package" > "$listing" 2>/dev/null
    grep -E -- '^-rwxr-xr-x .* squashfs-root/opt/devolutions-terminal/(Devolutions.Terminal|dt|dt-pty-host)$' "$listing" >/dev/null
    grep -E -- '^-rw-r--r-- .* squashfs-root/opt/devolutions-terminal/lib(SkiaSharp|HarfBuzzSharp|ghostty-vt)\.so$' "$listing" >/dev/null
    unsquashfs -no-progress -o "$(cat "$work/appimage-offset")" -d "$root" "$package" >/dev/null
    local runtime_size
    runtime_size="$(cat "$work/appimage-offset")"
    head -c "$runtime_size" "$package" > "$work/appimage-runtime"
    cmp "$work/appimage-runtime" \
        "$root/usr/share/doc/$PACKAGE_NAME/appimage-runtime"
    grep -F 'LicenseRef-AppImage-Runtime' \
        "$root/usr/share/doc/$PACKAGE_NAME/sbom.spdx.json" >/dev/null
    test -s "$root/usr/share/doc/$PACKAGE_NAME/APPIMAGE-RUNTIME-LICENSE.txt"
    test -x "$root/AppRun"
    test -f "$root/$APP_ID.desktop"
    test -L "$root/.DirIcon"
    local desktop="$root/$APP_ID.desktop"
    grep -Fx 'Exec=AppRun %u' "$desktop" >/dev/null
    grep -Fx 'TryExec=AppRun' "$desktop" >/dev/null
    grep -Fx 'Exec=AppRun -w new' "$desktop" >/dev/null
    grep -Fx 'Exec=AppRun -w use-any' "$desktop" >/dev/null
    ! grep -F '/opt/' "$desktop" >/dev/null
    if command -v desktop-file-validate >/dev/null 2>&1; then
        desktop-file-validate "$desktop"
    fi
}

extract_package() {
    local package="$1"
    local root="$2"
    case "$package" in
        *.tar.gz)
            validate_tar_modes "$package"
            tar -xzf "$package" -C "$root"
            ;;
        *.deb)
            validate_control_metadata "$package"
            ar p "$package" data.tar.gz | tar -xzf - -C "$root"
            ;;
        *.rpm)
            validate_rpm_metadata "$package"
            (cd "$root" && rpm2cpio "$package" | cpio -idm --quiet --no-absolute-filenames)
            ;;
        *.AppImage)
            extract_appimage "$package" "$root"
            ;;
        *)
            echo "Unsupported package extension: $package" >&2
            exit 64
            ;;
    esac
}

validate_inventory() {
    local root="$1"
    local package="$2"
    local artifact_kind
    case "$package" in
        *.tar.gz) artifact_kind=tar ;;
        *.deb) artifact_kind=deb ;;
        *.rpm) artifact_kind=rpm ;;
        *.AppImage) artifact_kind=appimage ;;
    esac
    PACKAGE_ROOT="$root" PACKAGE_NAME="$PACKAGE_NAME" ARTIFACT_KIND="$artifact_kind" python3 - <<'PY'
import hashlib
import json
import os
from pathlib import Path

root = Path(os.environ["PACKAGE_ROOT"])
doc = root / "usr/share/doc" / os.environ["PACKAGE_NAME"]
inventory = json.loads((doc / "inventory.json").read_text(encoding="utf-8"))
sbom = json.loads((doc / "sbom.spdx.json").read_text(encoding="utf-8"))
assert inventory["schemaVersion"] == 1
assert inventory["artifactKind"] == os.environ["ARTIFACT_KIND"]
assert sbom["spdxVersion"] == "SPDX-2.3"
assert sbom["name"].endswith("-" + os.environ["ARTIFACT_KIND"])
assert sbom["creationInfo"]["created"].endswith("Z")
assert sbom["documentDescribes"] == ["SPDXRef-Package"]
assert sbom["packages"][0]["versionInfo"] == inventory["version"]
package = sbom["packages"][0]
assert package["filesAnalyzed"] is True
expected_license = (
    "(MIT AND OFL-1.1) AND LicenseRef-AppImage-Runtime"
    if os.environ["ARTIFACT_KIND"] == "appimage"
    else "MIT AND OFL-1.1"
)
assert package["licenseConcluded"] == expected_license
assert package["licenseDeclared"] == expected_license
assert package["packageVerificationCode"]["packageVerificationCodeExcludedFiles"] == [
    f"./usr/share/doc/{os.environ['PACKAGE_NAME']}/sbom.spdx.json"
]
spdx = {
    item["fileName"]: next(
        checksum["checksumValue"]
        for checksum in item["checksums"]
        if checksum["algorithm"] == "SHA256"
    )
    for item in sbom["files"]
}
assert all(path.startswith("./") and not path.startswith("/") for path in spdx)
paths = [item["path"] for item in inventory["files"]]
assert paths == sorted(paths) and len(paths) == len(set(paths))
for item in inventory["files"]:
    path = root / item["path"].lstrip("/")
    assert path.exists() or path.is_symlink(), item["path"]
    if item["type"] == "symlink":
        expected_mode = "0777"
    elif path.name in {
        "Devolutions.Terminal", "dt", "dt-pty-host",
        "Install-LinuxDesktopIntegration.sh",
        "devolutions-terminal-x-terminal-emulator",
        "AppRun",
    }:
        expected_mode = "0755"
    else:
        expected_mode = "0644"
    assert item["mode"] == expected_mode, item["path"]
    if item["type"] == "symlink":
        data = os.readlink(path).encode()
    else:
        data = path.read_bytes()
        assert spdx["." + item["path"]] == item["sha256"], item["path"]
    assert hashlib.sha256(data).hexdigest() == item["sha256"], item["path"]
PY
    python3 "$script_dir/Validate-Spdx.py" \
        "$root/usr/share/doc/$PACKAGE_NAME/sbom.spdx.json" "$root"
}

validate_root() {
    local root="$1"
    local package="$2"
    local app="$root$INSTALL_DIR"
    local desktop="$root/usr/share/applications/$APP_ID.desktop"
    local metainfo="$root/usr/share/metainfo/$APP_ID.metainfo.xml"
    local helper="$app/linux/Install-LinuxDesktopIntegration.sh"

    for path in \
        "$app/Devolutions.Terminal" "$app/dt" "$app/dt-pty-host" \
        "$app/libghostty-vt.so" "$app/libSkiaSharp.so" "$app/libHarfBuzzSharp.so" \
        "$app/THIRD-PARTY-NOTICES-GHOSTTY.txt" \
        "$root/usr/share/doc/$PACKAGE_NAME/LICENSE" \
        "$root/usr/share/doc/$PACKAGE_NAME/inventory.json" \
        "$root/usr/share/doc/$PACKAGE_NAME/sbom.spdx.json" \
        "$desktop" "$metainfo" "$helper"; do
        test -e "$path" || { echo "$(basename "$package") is missing ${path#"$root"}." >&2; exit 1; }
    done
    test -L "$root/usr/bin/Devolutions.Terminal"
    test "$(readlink "$root/usr/bin/Devolutions.Terminal")" = "../../${INSTALL_DIR#/}/Devolutions.Terminal"
    test -L "$root/usr/bin/dt"
    test "$(readlink "$root/usr/bin/dt")" = "../../${INSTALL_DIR#/}/dt"
    for size in 16 32 48 64 96 256; do
        test -f "$root/usr/share/icons/hicolor/${size}x${size}/apps/$APP_ID.png"
    done

    grep -Fx "Exec=\"$INSTALL_DIR/Devolutions.Terminal\" %u" "$desktop" >/dev/null
    grep -Fx "TryExec=$INSTALL_DIR/Devolutions.Terminal" "$desktop" >/dev/null
    grep -Fx "MimeType=$DESKTOP_SCHEME;" "$desktop" >/dev/null
    grep -Fx 'X-TerminalArgExec=--' "$desktop" >/dev/null
    grep -F "<id>$APP_ID</id>" "$metainfo" >/dev/null
    python3 - "$metainfo" <<'PY'
import sys
import xml.etree.ElementTree as ET
ET.parse(sys.argv[1])
PY
    if command -v desktop-file-validate >/dev/null 2>&1; then
        desktop-file-validate "$desktop"
    fi
    if command -v appstreamcli >/dev/null 2>&1; then
        appstreamcli validate --no-net "$metainfo"
    fi

    while IFS= read -r -d '' path; do
        if file -b "$path" | grep -q '^ELF '; then
            readelf -h "$path" | grep -F "Machine:" | grep -F "$expected_machine" >/dev/null ||
                { echo "${path#"$root"} has the wrong architecture for $rid." >&2; exit 1; }
            if readelf -d "$path" 2>/dev/null | grep -E '\((RPATH|RUNPATH)\)' |
                grep -E '\[/(home|mnt|tmp|var/tmp)/' >/dev/null; then
                echo "${path#"$root"} contains a build-host RPATH." >&2
                exit 1
            fi
            if readelf -S "$path" | grep -E '\.debug(_|$)' >/dev/null; then
                echo "${path#"$root"} contains debug sections." >&2
                exit 1
            fi
            if strings "$path" | grep -E \
                '(/home/[^ /]+|/mnt/[a-z]/|/var/tmp/|linux-package-staging)' >/dev/null; then
                echo "${path#"$root"} contains an unexpected build-host absolute path." >&2
                exit 1
            fi
            while read -r dependency; do
                [[ "$dependency" != */* ]] ||
                    { echo "${path#"$root"} has an absolute NEEDED dependency: $dependency" >&2; exit 1; }
            done < <(readelf -d "$path" 2>/dev/null |
                sed -n 's/.*Shared library: \[\([^]]*\)\].*/\1/p')
        elif [[ "$(basename "$path")" == "Install-LinuxDesktopIntegration.sh" ||
                "$(basename "$path")" == "devolutions-terminal-x-terminal-emulator" ||
                "$(basename "$path")" == "AppRun" ]]; then
            head -c 2 "$path" | grep -F '#!' >/dev/null ||
                { echo "${path#"$root"} is executable but is neither ELF nor a script." >&2; exit 1; }
        fi
    done < <(find "$root" -type f -print0)

    if find "$root" -type f \( -name '*.pdb' -o -name '*.dbg' -o -name '*.dSYM' \
        -o -name '*.key' -o -name '*.pfx' -o -name '*.pem' \) | grep -q .; then
        echo "$(basename "$package") contains debug or private-key files." >&2
        exit 1
    fi
    if grep -RIlE --include='*.sh' --include='*.json' --include='*.xml' \
        --include='*.desktop' --include='*.env' \
        '(BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY|AKIA[0-9A-Z]{16}|github_pat_[A-Za-z0-9_]{20,})' \
        "$root" | grep -q .; then
        echo "$(basename "$package") contains a secret-like value." >&2
        exit 1
    fi
    if grep -RIlE --include='*.sh' --include='*.json' --include='*.xml' \
        --include='*.desktop' --include='*.env' \
        '(/home/[^ /]+|/mnt/[a-z]/|linux-package-staging)' "$root" | grep -q .; then
        echo "$(basename "$package") contains an unexpected build-host absolute path." >&2
        exit 1
    fi

    validate_inventory "$root" "$package"

    local lifecycle="$work/lifecycle"
    rm -rf "$lifecycle"
    cp -a "$root" "$lifecycle"
    "$lifecycle$INSTALL_DIR/linux/Install-LinuxDesktopIntegration.sh" \
        install --destdir "$lifecycle" --prefix /usr --app-dir "$INSTALL_DIR"
    "$lifecycle$INSTALL_DIR/linux/Install-LinuxDesktopIntegration.sh" \
        install --destdir "$lifecycle" --prefix /usr --app-dir "$INSTALL_DIR"
    "$lifecycle$INSTALL_DIR/linux/Install-LinuxDesktopIntegration.sh" \
        uninstall --destdir "$lifecycle" --prefix /usr --app-dir "$INSTALL_DIR"
    test ! -e "$lifecycle/usr/share/applications/$APP_ID.desktop"
    test ! -e "$lifecycle/usr/share/metainfo/$APP_ID.metainfo.xml"
    test -x "$lifecycle$INSTALL_DIR/Devolutions.Terminal"
}

for package in "$@"; do
    package="$(cd -- "$(dirname -- "$package")" && pwd)/$(basename "$package")"
    test -f "$package" || { echo "Package not found: $package" >&2; exit 66; }
    root="$work/root-$(basename "$package" | tr -c 'A-Za-z0-9' '_')"
    mkdir -p "$root"
    extract_package "$package" "$root"
    validate_root "$root" "$package"
    echo "Validated $(basename "$package")"
done

for checksum in "$(dirname -- "$1")"/*-"$rid".sha256; do
    [[ -f "$checksum" ]] || continue
    (cd "$(dirname "$checksum")" && sha256sum -c "$(basename "$checksum")")
done

echo "Linux $rid package validation passed without launching the UI."
