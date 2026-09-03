#!/usr/bin/env bash
set -euo pipefail
export LC_ALL=C

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
dotnet_root="$(cd -- "$script_dir/.." && pwd)"
metadata="$dotnet_root/linux/package.env"
package_dir="${1:-$dotnet_root/artifacts/packages}"

case "$(uname -m)" in
    aarch64|arm64) ;;
    *)
        echo "Native Linux ARM64 runtime validation requires an aarch64/arm64 host; uname -m returned '$(uname -m)'. QEMU and cross-execution are not accepted." >&2
        exit 78
        ;;
esac
[[ "$(uname -s)" == "Linux" ]] ||
    { echo "Native Linux ARM64 runtime validation requires Linux." >&2; exit 78; }
[[ -d "$package_dir" ]] ||
    { echo "ARM64 package directory not found: $package_dir" >&2; exit 66; }
package_dir="$(cd -- "$package_dir" && pwd)"

# shellcheck source=../linux/package.env
source "$metadata"
for command in ar awk cpio dotnet file find grep install python3 readelf rpm \
    rpm2cpio sha256sum strip tar unsquashfs; do
    command -v "$command" >/dev/null 2>&1 ||
        { echo "$command is required for native Linux ARM64 runtime validation." >&2; exit 69; }
done

work="${WT_ARM64_TEST_ROOT:-$dotnet_root/artifacts/linux-arm64-runtime-$$}"
rm -rf -- "$work"
mkdir -p "$work/home" "$work/config" "$work/state"
trap 'rm -rf -- "$work"' EXIT
export HOME="$work/home"
export XDG_CONFIG_HOME="$work/config"
export XDG_STATE_HOME="$work/state"
export XDG_CACHE_HOME="$work/cache"

one_package() {
    local suffix="$1"
    local matches=()
    mapfile -t matches < <(find "$package_dir" -maxdepth 1 -type f \
        -name "*-linux-arm64.$suffix" -print | sort)
    if ((${#matches[@]} != 1)); then
        echo "Expected exactly one *-linux-arm64.$suffix package in $package_dir; found ${#matches[@]}." >&2
        exit 66
    fi
    printf '%s\n' "${matches[0]}"
}

tar_package="$(one_package tar.gz)"
deb_package="$(one_package deb)"
rpm_package="$(one_package rpm)"
appimage_package="$(one_package AppImage)"
packages=("$tar_package" "$deb_package" "$rpm_package" "$appimage_package")

bash "$script_dir/Test-LinuxPackage.sh" linux-arm64 "${packages[@]}"

extract_appimage() {
    local package="$1"
    local root="$2"
    local offset
    offset="$(python3 - "$package" <<'PY'
import pathlib
import sys

data = pathlib.Path(sys.argv[1]).read_bytes()
offset = data.rfind(b"hsqs")
if offset < 0:
    raise SystemExit("AppImage has no embedded SquashFS filesystem")
print(offset)
PY
)"
    unsquashfs -no-progress -o "$offset" -d "$root" "$package" >/dev/null
}

extract_package() {
    local package="$1"
    local root="$2"
    mkdir -p "$root"
    case "$package" in
        *.tar.gz) tar -xzf "$package" -C "$root" ;;
        *.deb) ar p "$package" data.tar.gz | tar -xzf - -C "$root" ;;
        *.rpm) (cd "$root" && rpm2cpio "$package" | cpio -idm --quiet --no-absolute-filenames) ;;
        *.AppImage) extract_appimage "$package" "$root" ;;
        *) echo "Unsupported package: $package" >&2; exit 64 ;;
    esac
}

smoke_wt() {
    local root="$1"
    local label="$2"
    local app="$root$INSTALL_DIR"
    readelf -h "$app/dt" | grep -F "Machine:" | grep -F "AArch64" >/dev/null
    file "$app/dt" | grep -E 'ARM aarch64|aarch64' >/dev/null
    "$app/dt" --help | grep -F "dt - Devolutions Terminal" >/dev/null

    local error="$work/parser-error"
    if "$app/dt" --arm64-invalid-option >"$error" 2>&1; then
        echo "$label dt accepted an invalid parser option." >&2
        exit 1
    else
        local status=$?
        [[ "$status" == 2 ]] ||
            { echo "$label dt returned $status instead of 2 for invalid input." >&2; exit 1; }
    fi
    grep -F "Unknown command '--arm64-invalid-option'." "$error" >/dev/null

    local helper_error="$work/pty-helper-error"
    if "$app/dt-pty-host" >"$helper_error" 2>&1; then
        echo "$label dt-pty-host accepted missing launch arguments." >&2
        exit 1
    else
        local helper_status=$?
        [[ "$helper_status" == 64 ]] ||
            { echo "$label dt-pty-host returned $helper_status instead of 64 for missing arguments." >&2; exit 1; }
    fi
    grep -F "usage: dt-pty-host" "$helper_error" >/dev/null
    echo "NativeAOT dt startup and parser passed for $label."
}

for package in "${packages[@]}"; do
    root="$work/extracted-$(basename "$package" | tr -c 'A-Za-z0-9' '_')"
    extract_package "$package" "$root"
    smoke_wt "$root" "$(basename "$package")"
done

# Exercise package staging and lifecycle entirely below a disposable root.
artifact_root="$work/artifact-root"
staged="$work/staged-initial"
upgraded="$work/staged-upgrade"
installed="$work/installed"
extract_package "$tar_package" "$artifact_root"
SOURCE_DATE_EPOCH=1704067200 bash "$script_dir/Stage-LinuxPackage.sh" \
    "$artifact_root$INSTALL_DIR" "$staged" "0.0.0-arm64-runtime" linux-arm64
SOURCE_DATE_EPOCH=1704067200 bash "$script_dir/Stage-LinuxPackage.sh" \
    "$artifact_root$INSTALL_DIR" "$upgraded" "0.0.1-arm64-runtime" linux-arm64
mkdir -p "$installed"
cp -a "$staged/." "$installed/"
helper="$installed$INSTALL_DIR/linux/Install-LinuxDesktopIntegration.sh"
"$helper" install --destdir "$installed" --prefix /usr --app-dir "$INSTALL_DIR"
smoke_wt "$installed" "temporary-root install"

expected_dt="$(sha256sum "$staged$INSTALL_DIR/dt" | awk '{print $1}')"
printf 'damaged before upgrade\n' >"$installed$INSTALL_DIR/dt"
cp -a "$upgraded/." "$installed/"
"$helper" install --destdir "$installed" --prefix /usr --app-dir "$INSTALL_DIR"
test "$(sha256sum "$installed$INSTALL_DIR/dt" | awk '{print $1}')" = "$expected_dt"
python3 - "$installed/usr/share/doc/$PACKAGE_NAME/inventory.json" <<'PY'
import json
import pathlib
import sys

inventory = json.loads(pathlib.Path(sys.argv[1]).read_text(encoding="utf-8"))
assert inventory["version"] == "0.0.1-arm64-runtime"
PY
smoke_wt "$installed" "temporary-root upgrade"

"$helper" uninstall --destdir "$installed" --prefix /usr --app-dir "$INSTALL_DIR"
test ! -e "$installed/usr/share/applications/$APP_ID.desktop"
rm -rf -- "$installed$INSTALL_DIR" "$installed/usr/bin" \
    "$installed/usr/share/doc/$PACKAGE_NAME"
test ! -e "$installed$INSTALL_DIR/dt"
echo "Temporary-root package stage/install/upgrade/uninstall passed."

run_tests() {
    local project="$1"
    local filter="$2"
    dotnet test "$dotnet_root/tests/$project/$project.csproj" \
        -c Release --nologo --verbosity minimal --filter "$filter"
}

run_packaged_native_tests() {
    local project="$1"
    local native_name="$2"
    local packaged_native="$3"
    local filter="$4"
    local project_file="$dotnet_root/tests/$project/$project.csproj"
    dotnet build "$project_file" -c Release --nologo --verbosity minimal
    local assembly
    assembly="$(find "$dotnet_root/tests/$project/bin/Release" -type f \
        -name "$project.dll" -print -quit)"
    [[ -n "$assembly" ]] ||
        { echo "Could not locate the $project test output." >&2; exit 70; }
    if [[ -x "$packaged_native" ]]; then
        install -m 0755 "$packaged_native" "$(dirname "$assembly")/$native_name"
    else
        install -m 0644 "$packaged_native" "$(dirname "$assembly")/$native_name"
    fi
    dotnet test "$project_file" -c Release --no-build --no-restore \
        --nologo --verbosity minimal --filter "$filter"
}

run_tests Devolutions.Terminal.Cli.Tests \
    'FullyQualifiedName~Devolutions.Terminal.Cli.Tests.CliParserTests'
run_tests Devolutions.Terminal.Core.Tests \
    'FullyQualifiedName~Devolutions.Terminal.Core.Tests.VtParserTests'
run_packaged_native_tests Devolutions.Terminal.Ghostty.Tests libghostty-vt.so \
    "$artifact_root$INSTALL_DIR/libghostty-vt.so" \
    'FullyQualifiedName~Devolutions.Terminal.Ghostty.Tests.GhosttyTerminalEngineTests'
run_packaged_native_tests Devolutions.Terminal.Connection.Tests dt-pty-host \
    "$artifact_root$INSTALL_DIR/dt-pty-host" \
    'FullyQualifiedName~Devolutions.Terminal.Connection.Tests.LinuxPtyConnectionTests'
run_tests Devolutions.Terminal.Broker.Tests \
    'FullyQualifiedName~Devolutions.Terminal.Broker.Tests.BrokerTests.ConcurrentClientsAreServedBySinglePrimary'
run_tests Devolutions.Terminal.Settings.Tests \
    'FullyQualifiedName~Devolutions.Terminal.Settings.Tests.DynamicProfileGeneratorTests.LinuxShellsUseShellAndPathExecutables|FullyQualifiedName~Devolutions.Terminal.Settings.Tests.LinuxRuntimeEnvironmentTests'

echo "Native Linux ARM64 non-UI runtime validation passed on $(uname -m)."
