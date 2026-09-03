#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
dotnet_root="$(cd -- "$script_dir/.." && pwd)"
installer="$dotnet_root/linux/Install-LinuxDesktopIntegration.sh"
work="${1:-$dotnet_root/artifacts/linux-desktop-test}"
stage="$work/stage"

rm -rf -- "$work"
mkdir -p "$stage"
bash -n "$installer"
run_installer() {
    bash "$installer" "$@"
}
run_installer install --destdir "$stage" --prefix /usr

desktop="$stage/usr/share/applications/com.awakecoding.WindowsTerminalDotNet.desktop"
metainfo="$stage/usr/share/metainfo/com.awakecoding.WindowsTerminalDotNet.metainfo.xml"
wrapper="$stage/usr/bin/windows-terminal-dotnet-x-terminal-emulator"
test -f "$desktop"
test -f "$metainfo"
test -x "$wrapper"
if command -v desktop-file-validate >/dev/null 2>&1; then
    desktop-file-validate "$desktop"
fi
if command -v appstreamcli >/dev/null 2>&1; then
    appstreamcli validate --no-net "$metainfo"
fi
grep -Fx 'Exec="/opt/windows-terminal-dotnet/WindowsTerminal" %u' "$desktop" >/dev/null
grep -Fx 'TryExec=/opt/windows-terminal-dotnet/WindowsTerminal' "$desktop" >/dev/null
grep -Fx 'MimeType=x-scheme-handler/wt-dotnet;' "$desktop" >/dev/null
grep -Fx 'X-TerminalArgExec=--' "$desktop" >/dev/null
grep -F '<id>com.awakecoding.WindowsTerminalDotNet</id>' "$metainfo" >/dev/null
for size in 16 32 48 64 96 256; do
    test -f "$stage/usr/share/icons/hicolor/${size}x${size}/apps/com.awakecoding.WindowsTerminalDotNet.png"
done

run_installer uninstall --destdir "$stage" --prefix /usr
test ! -e "$desktop"
test ! -e "$metainfo"
test ! -e "$wrapper"
if find "$stage/usr/share/icons" -type f -name 'com.awakecoding.WindowsTerminalDotNet.png' | grep -q .; then
    echo "uninstall left application icons behind" >&2
    exit 1
fi

fake_bin="$work/fake-bin"
home="$work/home"
live_prefix="$work/prefix"
state="$work/state"
config="$work/config"
mkdir -p "$fake_bin" "$home" "$state" "$config"
cat > "$fake_bin/xdg-terminal-exec" <<'EOF'
#!/bin/sh
exit 0
EOF
cat > "$fake_bin/xdg-mime" <<'EOF'
#!/bin/sh
case "$1" in
    query) cat "$FAKE_MIME_STATE" ;;
    default) printf '%s\n' "$2" > "$FAKE_MIME_STATE" ;;
    *) exit 64 ;;
esac
EOF
chmod 0755 "$fake_bin/xdg-terminal-exec" "$fake_bin/xdg-mime"

PATH="$fake_bin:$PATH" HOME="$home" XDG_STATE_HOME="$state" XDG_CONFIG_HOME="$config" \
    run_installer install --prefix "$live_prefix" --app-dir /opt/custom-terminal
grep -Fx 'Exec="/opt/custom-terminal/WindowsTerminal" %u' \
    "$live_prefix/share/applications/com.awakecoding.WindowsTerminalDotNet.desktop" >/dev/null

printf 'org.example.Previous.desktop\n' > "$config/xdg-terminals.list"
PATH="$fake_bin:$PATH" HOME="$home" XDG_STATE_HOME="$state" XDG_CONFIG_HOME="$config" XDG_CURRENT_DESKTOP= \
    run_installer set-default-terminal --prefix "$live_prefix"
test "$(sed -n '1p' "$config/xdg-terminals.list")" = \
    'com.awakecoding.WindowsTerminalDotNet.desktop'
grep -Fx 'org.example.Previous.desktop' "$config/xdg-terminals.list" >/dev/null
PATH="$fake_bin:$PATH" HOME="$home" XDG_STATE_HOME="$state" XDG_CONFIG_HOME="$config" XDG_CURRENT_DESKTOP= \
    run_installer unset-default-terminal --prefix "$live_prefix"
test "$(cat "$config/xdg-terminals.list")" = 'org.example.Previous.desktop'

mime_state="$work/mime-state"
printf 'org.example.Previous.desktop\n' > "$mime_state"
PATH="$fake_bin:$PATH" HOME="$home" XDG_STATE_HOME="$state" XDG_CONFIG_HOME="$config" \
FAKE_MIME_STATE="$mime_state" \
    run_installer register-protocol --prefix "$live_prefix"
test "$(cat "$mime_state")" = 'com.awakecoding.WindowsTerminalDotNet.desktop'
PATH="$fake_bin:$PATH" HOME="$home" XDG_STATE_HOME="$state" XDG_CONFIG_HOME="$config" \
FAKE_MIME_STATE="$mime_state" \
    run_installer unregister-protocol --prefix "$live_prefix"
test "$(cat "$mime_state")" = 'org.example.Previous.desktop'

: > "$mime_state"
PATH="$fake_bin:$PATH" HOME="$home" XDG_STATE_HOME="$state" XDG_CONFIG_HOME="$config" \
FAKE_MIME_STATE="$mime_state" \
    run_installer register-protocol --prefix "$live_prefix"
cat > "$config/mimeapps.list" <<'EOF'
[Default Applications]
x-scheme-handler/wt-dotnet=com.awakecoding.WindowsTerminalDotNet.desktop;org.example.Other.desktop;
EOF
PATH="$fake_bin:$PATH" HOME="$home" XDG_STATE_HOME="$state" XDG_CONFIG_HOME="$config" \
FAKE_MIME_STATE="$mime_state" \
    run_installer unregister-protocol --prefix "$live_prefix"
grep -Fx 'x-scheme-handler/wt-dotnet=org.example.Other.desktop;' \
    "$config/mimeapps.list" >/dev/null

PATH="$fake_bin:$PATH" HOME="$home" XDG_STATE_HOME="$state" XDG_CONFIG_HOME="$config" \
    run_installer uninstall --prefix "$live_prefix"
test ! -e "$live_prefix/share/applications/com.awakecoding.WindowsTerminalDotNet.desktop"

echo "Linux desktop metadata, registration, and uninstall validation passed."
