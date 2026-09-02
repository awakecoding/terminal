#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
dotnet_root="$(cd -- "$script_dir/.." && pwd)"
project="$dotnet_root/src/WindowsTerminal/WindowsTerminal.csproj"
rid="${1:-linux-x64}"
version="${2:-0.1.0}"
output_dir="${3:-$dotnet_root/artifacts/packages}"

case "$rid" in
    linux-x64)
        expected_machine="x86-64"
        ;;
    linux-arm64)
        expected_machine="ARM aarch64"
        ;;
    *)
        echo "Unsupported Linux RID: $rid" >&2
        exit 64
        ;;
esac

staging="$(mktemp -d)"
trap 'rm -rf -- "$staging"' EXIT

publish_args=(
    "$project"
    -c Release
    -r "$rid"
    --self-contained true
    -o "$staging"
    -p:DebugSymbols=false
    -p:DebugType=None
    -p:NativeDebugSymbols=false
    --verbosity minimal
)
if [[ "$rid" == "linux-arm64" && "$(uname -m)" != "aarch64" ]]; then
    command -v aarch64-linux-gnu-objcopy >/dev/null ||
        { echo "aarch64-linux-gnu-objcopy is required for ARM64 cross-publish." >&2; exit 69; }
    publish_args+=("-p:ObjCopyName=aarch64-linux-gnu-objcopy")
fi

dotnet publish "${publish_args[@]}"
find "$staging" -type f \( -name '*.dbg' -o -name '*.pdb' \) -delete
find "$staging" -type d -exec chmod 755 {} +
find "$staging" -type f -exec chmod 644 {} +
chmod 755 "$staging/WindowsTerminal" "$staging/wt" "$staging/wt-pty-host"

for artifact in WindowsTerminal wt wt-pty-host libghostty-vt.so; do
    test -f "$staging/$artifact" ||
        { echo "Publish output is missing $artifact." >&2; exit 70; }
done
file "$staging/WindowsTerminal" | grep -F "$expected_machine" >/dev/null ||
    { echo "WindowsTerminal has the wrong architecture for $rid." >&2; exit 70; }
file "$staging/wt-pty-host" | grep -F "$expected_machine" >/dev/null ||
    { echo "wt-pty-host has the wrong architecture for $rid." >&2; exit 70; }

mkdir -p "$output_dir"
archive="$output_dir/windows-terminal-dotnet-${version}-${rid}.tar.gz"
source_date_epoch="${SOURCE_DATE_EPOCH:-}"
if [[ -z "$source_date_epoch" ]]; then
    source_date_epoch="$(git -C "$dotnet_root" log -1 --format=%ct 2>/dev/null ||
        stat -c %Y "$project")"
fi
tar \
    --sort=name \
    --owner=0 \
    --group=0 \
    --numeric-owner \
    --mtime="@$source_date_epoch" \
    -C "$staging" \
    -czf "$archive" \
    .

echo "$archive"
