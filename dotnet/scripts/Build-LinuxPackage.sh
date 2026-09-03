#!/usr/bin/env bash
set -euo pipefail
export LC_ALL=C

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd -- "$script_dir/.." && pwd)"
dotnet_root="$repo_root"
project="$repo_root/src/Devolutions.Terminal/Devolutions.Terminal.csproj"
metadata="$repo_root/linux/package.env"

rid="${1:-linux-x64}"
version="${2:-0.1.0}"
output_dir="${3:-$dotnet_root/artifacts/packages}"
formats="${4:-tar}"

# shellcheck source=../linux/package.env
source "$metadata"
case "$rid" in
    linux-x64)
        expected_machine="Advanced Micro Devices X86-64"
        deb_arch="amd64"
        rpm_arch="x86_64"
        appimage_arch="x86_64"
        ;;
    linux-arm64)
        expected_machine="AArch64"
        deb_arch="arm64"
        rpm_arch="aarch64"
        appimage_arch="aarch64"
        ;;
    *)
        echo "Unsupported Linux RID: $rid" >&2
        exit 64
        ;;
esac
[[ "$version" =~ ^[0-9][0-9A-Za-z.+~_-]*$ ]] ||
    { echo "Invalid Linux package version: $version" >&2; exit 64; }
for command in file readelf sha256sum python3; do
    command -v "$command" >/dev/null 2>&1 ||
        { echo "$command is required to build Linux packages; install it and retry." >&2; exit 69; }
done

if [[ "$formats" == "all" ]]; then
    formats="tar,deb,rpm,appimage"
fi
IFS=',' read -r -a requested_formats <<<"$formats"
for format in "${requested_formats[@]}"; do
    case "$format" in
        tar) ;;
        deb)
            command -v ar >/dev/null 2>&1 ||
                { echo "ar is required for DEB output (Ubuntu: sudo apt-get install binutils)." >&2; exit 69; }
            ;;
        rpm)
            command -v rpmbuild >/dev/null 2>&1 ||
                { echo "rpmbuild is required for RPM output. Install the rpm build tools (Ubuntu: sudo apt-get install rpm)." >&2; exit 69; }
            ;;
        appimage)
            command -v mksquashfs >/dev/null 2>&1 ||
                { echo "mksquashfs is required for AppImage output (Ubuntu: sudo apt-get install squashfs-tools)." >&2; exit 69; }
            [[ -f "${APPIMAGE_RUNTIME_FILE:-}" ]] ||
                { echo "APPIMAGE_RUNTIME_FILE must name a pinned, local $appimage_arch type-2 AppImage runtime; the builder never downloads one." >&2; exit 69; }
            readelf -h "$APPIMAGE_RUNTIME_FILE" | grep -F "Machine:" | grep -F "$expected_machine" >/dev/null ||
                { echo "APPIMAGE_RUNTIME_FILE has the wrong architecture for $rid." >&2; exit 70; }
            ;;
        *)
            echo "Unknown format '$format'; choose tar, deb, rpm, appimage, or all." >&2
            exit 64
            ;;
    esac
done

source_date_epoch="${SOURCE_DATE_EPOCH:-}"
if [[ -z "$source_date_epoch" ]]; then
    source_date_epoch="$(git -C "$repo_root" log -1 --format=%ct 2>/dev/null ||
        stat -c %Y "$project")"
fi
[[ "$source_date_epoch" =~ ^[0-9]+$ ]] ||
    { echo "SOURCE_DATE_EPOCH must be a non-negative integer." >&2; exit 64; }
export SOURCE_DATE_EPOCH="$source_date_epoch"

work="$dotnet_root/artifacts/linux-package-staging/$rid-$$"
publish_dir="$work/publish"
package_root="$work/root"
rm -rf -- "$work"
mkdir -p "$publish_dir"
trap 'rm -rf -- "$work"' EXIT

if [[ -n "${LINUX_PUBLISH_DIR:-}" ]]; then
    [[ -d "$LINUX_PUBLISH_DIR" ]] ||
        { echo "LINUX_PUBLISH_DIR does not exist: $LINUX_PUBLISH_DIR" >&2; exit 66; }
    cp -a "$LINUX_PUBLISH_DIR/." "$publish_dir/"
else
    command -v dotnet >/dev/null 2>&1 ||
        { echo "dotnet is required to publish the application; install the SDK selected by dotnet/global.json or set LINUX_PUBLISH_DIR." >&2; exit 69; }
    publish_args=(
        "$project"
        -c Release
        -r "$rid"
        --self-contained true
        -o "$publish_dir"
        -p:DebugSymbols=false
        -p:DebugType=None
        -p:NativeDebugSymbols=false
        --verbosity minimal
    )
    if [[ "$rid" == "linux-arm64" && "$(uname -m)" != "aarch64" ]]; then
        command -v aarch64-linux-gnu-objcopy >/dev/null 2>&1 ||
            { echo "aarch64-linux-gnu-objcopy is required for ARM64 cross-publish (Ubuntu: sudo apt-get install binutils-aarch64-linux-gnu)." >&2; exit 69; }
        publish_args+=("-p:ObjCopyName=aarch64-linux-gnu-objcopy")
    fi
    dotnet publish "${publish_args[@]}"
fi

find "$publish_dir" -type f \( -name '*.dbg' -o -name '*.pdb' \) -delete
for artifact in Devolutions.Terminal dt dt-pty-host libghostty-vt.so libSkiaSharp.so libHarfBuzzSharp.so; do
    [[ -f "$publish_dir/$artifact" ]] ||
        { echo "Publish output is missing $artifact." >&2; exit 70; }
    if [[ "$artifact" == *.so || "$artifact" != "lib"* ]]; then
        readelf -h "$publish_dir/$artifact" | grep -F "Machine:" | grep -F "$expected_machine" >/dev/null ||
            { echo "$artifact has the wrong architecture for $rid." >&2; exit 70; }
    fi
done

bash "$script_dir/Stage-LinuxPackage.sh" "$publish_dir" "$package_root" "$version" "$rid"
mkdir -p "$output_dir"
output_dir="$(cd -- "$output_dir" && pwd)"
base="$PACKAGE_NAME-$version-$rid"
built=()

prepare_artifact_metadata() {
    local root="$1"
    local artifact_kind="$2"
    local license_id="${3:-$SBOM_LICENSE_ID}"
    python3 "$script_dir/Generate-LinuxPackageMetadata.py" \
        "$root" "$PACKAGE_NAME" "$version" "$rid" "$license_id" \
        "$artifact_kind" "$source_date_epoch"
    python3 "$script_dir/Validate-Spdx.py" \
        "$root/usr/share/doc/$PACKAGE_NAME/sbom.spdx.json" "$root"
}

create_tar() {
    python3 "$script_dir/Create-DeterministicTar.py" "$1" "$2" "$source_date_epoch"
}

build_tar() {
    local archive="$output_dir/$base.tar.gz"
    prepare_artifact_metadata "$package_root" tar
    create_tar "$package_root" "$archive"
    built+=("$archive")
}

build_deb() {
    prepare_artifact_metadata "$package_root" deb
    local deb_work="$work/deb"
    local control="$deb_work/control"
    mkdir -p "$control"
    local installed_size
    installed_size="$(du -sk "$package_root" | awk '{print $1}')"
    cat > "$control/control" <<EOF
Package: $PACKAGE_NAME
Version: $version
Architecture: $deb_arch
Maintainer: Devolutions Inc. <support@devolutions.net>
Installed-Size: $installed_size
Depends: $RUNTIME_DEPENDENCIES_DEB
Section: utils
Priority: optional
Homepage: $HOMEPAGE
Description: $SUMMARY
 Cross-platform Avalonia terminal emulator published with .NET NativeAOT.
EOF
    (
        cd "$package_root"
        find . -type f -print0 |
            sort -z | xargs -0 sha256sum
    ) > "$control/sha256sums"
    cat > "$control/postinst" <<'EOF'
#!/bin/sh
set -e
command -v update-desktop-database >/dev/null 2>&1 &&
    update-desktop-database /usr/share/applications || true
command -v gtk-update-icon-cache >/dev/null 2>&1 &&
    gtk-update-icon-cache --force --ignore-theme-index /usr/share/icons/hicolor || true
exit 0
EOF
    cat > "$control/postrm" <<'EOF'
#!/bin/sh
set -e
if [ "$1" = remove ] || [ "$1" = purge ]; then
    command -v update-desktop-database >/dev/null 2>&1 &&
        update-desktop-database /usr/share/applications || true
    command -v gtk-update-icon-cache >/dev/null 2>&1 &&
        gtk-update-icon-cache --force --ignore-theme-index /usr/share/icons/hicolor || true
fi
exit 0
EOF
    chmod 0755 "$control/postinst" "$control/postrm"
    find "$deb_work" -depth -exec touch --date="@$source_date_epoch" {} +
    create_tar "$control" "$deb_work/control.tar.gz"
    create_tar "$package_root" "$deb_work/data.tar.gz"
    printf '2.0\n' > "$deb_work/debian-binary"
    touch --date="@$source_date_epoch" "$deb_work/debian-binary"
    local archive="$output_dir/$base.deb"
    rm -f "$archive"
    (
        cd "$deb_work"
        ar rcsD "$archive" debian-binary control.tar.gz data.tar.gz
    )
    built+=("$archive")
}

build_rpm() {
    prepare_artifact_metadata "$package_root" rpm
    local rpm_work="$work/rpm"
    local top="$rpm_work/top"
    mkdir -p "$top"/{BUILD,BUILDROOT,RPMS,SOURCES,SPECS,SRPMS}
    local escaped_root="${package_root//\\/\\\\}"
    escaped_root="${escaped_root//%/%%}"
    cat > "$top/SPECS/$PACKAGE_NAME.spec" <<EOF
Name: $PACKAGE_NAME
Version: $version
Release: 1
Summary: $SUMMARY
License: $LICENSE_ID
URL: $HOMEPAGE
Requires: $RUNTIME_DEPENDENCIES_RPM
AutoReqProv: no

%description
Cross-platform Avalonia terminal emulator published with .NET NativeAOT.

%prep

%build

%install
rm -rf %{buildroot}
mkdir -p %{buildroot}
cp -a "$escaped_root"/. %{buildroot}/

%files
%defattr(0644,root,root,0755)
EOF
    while IFS= read -r path; do
        relative="${path#"$package_root"}"
        case "$relative" in
            /opt|/usr|/usr/bin|/usr/share|/usr/share/applications|/usr/share/metainfo|/usr/share/icons|/usr/share/icons/hicolor|/usr/share/doc)
                continue
                ;;
        esac
        if [[ -d "$path" ]]; then
            printf '%%dir %%attr(0755,root,root) %s\n' "$relative"
        elif [[ -L "$path" ]]; then
            printf '%s\n' "$relative"
        else
            case "$(basename "$path")" in
                Devolutions.Terminal|dt|dt-pty-host|Install-LinuxDesktopIntegration.sh|devolutions-terminal-x-terminal-emulator)
                    printf '%%attr(0755,root,root) %s\n' "$relative" ;;
                *) printf '%%attr(0644,root,root) %s\n' "$relative" ;;
            esac
        fi
    done < <(find "$package_root" -mindepth 1 -print | sort) \
        >> "$top/SPECS/$PACKAGE_NAME.spec"
    rpmbuild --target "${rpm_arch}-linux" -bb "$top/SPECS/$PACKAGE_NAME.spec" \
        --define "_topdir $top" \
        --define "_buildhost reproducible.invalid" \
        --define "_build_id_links none" \
        --define "__os_install_post %{nil}" \
        --define "clamp_mtime_to_source_date_epoch Y" \
        --define "source_date_epoch_from_changelog 0" \
        --define "use_source_date_epoch_as_buildtime Y" >/dev/null
    local generated
    generated="$(find "$top/RPMS" -type f -name '*.rpm' -print -quit)"
    [[ -n "$generated" ]] || { echo "rpmbuild produced no RPM." >&2; exit 70; }
    local archive="$output_dir/$base.rpm"
    cp "$generated" "$archive"
    built+=("$archive")
}

build_appimage() {
    local appdir="$work/AppDir"
    cp -a "$package_root" "$appdir"
    sed \
        -e "s|\"$INSTALL_DIR/Devolutions.Terminal\"|AppRun|g" \
        -e "s|$INSTALL_DIR/Devolutions.Terminal|AppRun|g" \
        "$package_root/usr/share/applications/$APP_ID.desktop" \
        > "$appdir/$APP_ID.desktop"
    cp "$package_root/usr/share/icons/hicolor/256x256/apps/$APP_ID.png" \
        "$appdir/$APP_ID.png"
    ln -s "$APP_ID.png" "$appdir/.DirIcon"
    cat > "$appdir/AppRun" <<EOF
#!/bin/sh
set -eu
HERE=\$(CDPATH= cd -- "\$(dirname -- "\$0")" && pwd)
export WT_DOTNET_INSTALL_DIR="\$HERE$INSTALL_DIR"
exec "\$HERE$INSTALL_DIR/Devolutions.Terminal" "\$@"
EOF
    chmod 0755 "$appdir/AppRun"
    local appimage_doc="$appdir/usr/share/doc/$PACKAGE_NAME"
    cp "$APPIMAGE_RUNTIME_FILE" "$appimage_doc/appimage-runtime"
    cp "$dotnet_root/linux/APPIMAGE-RUNTIME-LICENSE.txt" \
        "$appimage_doc/APPIMAGE-RUNTIME-LICENSE.txt"
    chmod 0644 "$appimage_doc/appimage-runtime" \
        "$appimage_doc/APPIMAGE-RUNTIME-LICENSE.txt"
    prepare_artifact_metadata \
        "$appdir" appimage \
        "($SBOM_LICENSE_ID) AND LicenseRef-AppImage-Runtime"
    find "$appdir" -depth -exec touch --no-dereference --date="@$source_date_epoch" {} +
    local archive="$output_dir/$base.AppImage"
    local squashfs="$work/$base.squashfs"
    local pseudo="$work/appimage.pseudo"
    local sort_file="$work/appimage.sort"
    rm -f "$archive"
    (
        cd "$appdir"
        priority=32767
        while IFS= read -r path; do
            [[ -n "$path" ]] || continue
            printf '%s %d\n' "$path" "$priority"
            priority=$((priority - 1))
        done < <(find . -mindepth 1 -printf '%P\n' | sort)
    ) > "$sort_file"
    (
        cd "$appdir"
        while IFS= read -r path; do
            [[ -n "$path" ]] || continue
            if [[ -d "$path" ]]; then
                mode=0755
            elif [[ -L "$path" ]]; then
                mode=0777
            else
                case "$(basename "$path")" in
                    Devolutions.Terminal|dt|dt-pty-host|Install-LinuxDesktopIntegration.sh|devolutions-terminal-x-terminal-emulator|AppRun)
                        mode=0755 ;;
                    *) mode=0644 ;;
                esac
            fi
            printf '%s m %s 0 0\n' "$path" "$mode"
        done < <(find . -mindepth 1 -printf '%P\n' | sort)
    ) > "$pseudo"
    env -u SOURCE_DATE_EPOCH mksquashfs "$appdir" "$squashfs" -noappend -all-root -no-xattrs \
        -mkfs-time "$source_date_epoch" -all-time "$source_date_epoch" \
        -root-mode 0755 -comp gzip -Xcompression-level 9 -processors 1 \
        -sort "$sort_file" -pf "$pseudo" -quiet
    cat "$APPIMAGE_RUNTIME_FILE" "$squashfs" > "$archive"
    chmod 0755 "$archive"
    touch --date="@$source_date_epoch" "$archive"
    built+=("$archive")
}

for format in "${requested_formats[@]}"; do
    case "$format" in
        tar) build_tar ;;
        deb) build_deb ;;
        rpm) build_rpm ;;
        appimage) build_appimage ;;
    esac
done

checksums="$output_dir/$base.sha256"
(
    cd "$output_dir"
    for artifact in "${built[@]}"; do
        sha256sum "$(basename "$artifact")"
    done | sort -k2
) > "$checksums"
touch --date="@$source_date_epoch" "$checksums"

printf '%s\n' "${built[@]}" "$checksums"
