#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd -- "$script_dir/.." && pwd)"
dotnet_root="$repo_root"
metadata="$repo_root/linux/package.env"

if (($# != 4)); then
    echo "Usage: $0 <publish-directory> <package-root> <version> <linux-x64|linux-arm64>" >&2
    exit 64
fi

publish_dir="$(cd -- "$1" && pwd)"
package_root="$2"
version="$3"
rid="$4"

# shellcheck source=../linux/package.env
source "$metadata"

[[ "$version" =~ ^[0-9][0-9A-Za-z.+~_-]*$ ]] ||
    { echo "Invalid Linux package version: $version" >&2; exit 64; }
case "$rid" in
    linux-x64) strip_tool="${STRIP:-strip}" ;;
    linux-arm64)
        if [[ "$(uname -m)" == "aarch64" ]]; then
            strip_tool="${STRIP:-strip}"
        else
            strip_tool="${STRIP:-aarch64-linux-gnu-strip}"
        fi
        ;;
    *) echo "Unsupported Linux RID: $rid" >&2; exit 64 ;;
esac
command -v python3 >/dev/null 2>&1 ||
    { echo "python3 is required to generate deterministic package inventories and SPDX SBOMs." >&2; exit 69; }
command -v "$strip_tool" >/dev/null 2>&1 ||
    { echo "$strip_tool is required to remove debug data from $rid package payloads." >&2; exit 69; }

required=(
    Devolutions.Terminal
    dt
    dt-pty-host
    libghostty-vt.so
    libSkiaSharp.so
    libHarfBuzzSharp.so
    THIRD-PARTY-NOTICES-GHOSTTY.txt
    THIRD-PARTY-NOTICES-NOTO-EMOJI.txt
)
for path in "${required[@]}"; do
    [[ -f "$publish_dir/$path" ]] ||
        { echo "NativeAOT publish output is missing $path for $rid." >&2; exit 70; }
done

rm -rf -- "$package_root"
install -d "$package_root$INSTALL_DIR"
cp -a "$publish_dir/." "$package_root$INSTALL_DIR/"
rm -f "$package_root$INSTALL_DIR"/*.dbg "$package_root$INSTALL_DIR"/*.pdb
while IFS= read -r -d '' path; do
    if file -b "$path" | grep -q '^ELF '; then
        "$strip_tool" --strip-unneeded "$path"
    fi
done < <(find "$package_root$INSTALL_DIR" -maxdepth 1 -type f -print0)

install -d "$package_root$INSTALL_DIR/linux"
cp -a \
    "$dotnet_root/linux/Install-LinuxDesktopIntegration.sh" \
    "$dotnet_root/linux/package.env" \
    "$dotnet_root/linux/com.devolutions.Terminal.desktop" \
    "$dotnet_root/linux/com.devolutions.Terminal.metainfo.xml" \
    "$dotnet_root/linux/icons" \
    "$package_root$INSTALL_DIR/linux/"

DESTDIR="$package_root" bash "$dotnet_root/linux/Install-LinuxDesktopIntegration.sh" \
    install --prefix /usr --app-dir "$INSTALL_DIR"

install -d "$package_root/usr/bin" "$package_root/usr/share/doc/$PACKAGE_NAME"
ln -s "../../${INSTALL_DIR#/}/Devolutions.Terminal" "$package_root/usr/bin/Devolutions.Terminal"
ln -s "../../${INSTALL_DIR#/}/dt" "$package_root/usr/bin/dt"
install -m 0644 "$repo_root/LICENSE" "$package_root/usr/share/doc/$PACKAGE_NAME/LICENSE"
install -m 0644 "$publish_dir/THIRD-PARTY-NOTICES-GHOSTTY.txt" \
    "$package_root/usr/share/doc/$PACKAGE_NAME/THIRD-PARTY-NOTICES-GHOSTTY.txt"

find "$package_root" -type d -exec chmod 0755 {} +
find "$package_root" -type f -exec chmod 0644 {} +
chmod 0755 \
    "$package_root$INSTALL_DIR/Devolutions.Terminal" \
    "$package_root$INSTALL_DIR/dt" \
    "$package_root$INSTALL_DIR/dt-pty-host" \
    "$package_root$INSTALL_DIR/linux/Install-LinuxDesktopIntegration.sh" \
    "$package_root/usr/bin/devolutions-terminal-x-terminal-emulator"

source_date_epoch="${SOURCE_DATE_EPOCH:-}"
if [[ -z "$source_date_epoch" ]]; then
    source_date_epoch="$(git -C "$repo_root" log -1 --format=%ct 2>/dev/null ||
        stat -c %Y "$dotnet_root/Directory.Build.props")"
fi
[[ "$source_date_epoch" =~ ^[0-9]+$ ]] ||
    { echo "SOURCE_DATE_EPOCH must be a non-negative integer." >&2; exit 64; }
export SOURCE_DATE_EPOCH="$source_date_epoch"
find "$package_root" -depth -exec touch --no-dereference --date="@$source_date_epoch" {} +

python3 "$script_dir/Generate-LinuxPackageMetadata.py" \
    "$package_root" "$PACKAGE_NAME" "$version" "$rid" "$SBOM_LICENSE_ID" \
    staged "$source_date_epoch"
python3 "$script_dir/Validate-Spdx.py" \
    "$package_root/usr/share/doc/$PACKAGE_NAME/sbom.spdx.json" "$package_root"
