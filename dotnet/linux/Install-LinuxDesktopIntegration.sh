#!/usr/bin/env bash
set -euo pipefail

app_id="com.devolutions.Terminal"
scheme="x-scheme-handler/dterm"
script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
action="${1:-}"
if [[ -z "$action" ]]; then
    echo "Usage: $0 <install|uninstall|register-protocol|unregister-protocol|set-default-terminal|unset-default-terminal|diagnose> [options]" >&2
    exit 64
fi
shift

destdir="${DESTDIR:-}"
prefix="/usr"
app_dir="/opt/devolutions-terminal"
method="auto"
while (($#)); do
    case "$1" in
        --destdir)
            destdir="${2:?--destdir requires a value}"
            shift 2
            ;;
        --prefix)
            prefix="${2:?--prefix requires a value}"
            shift 2
            ;;
        --app-dir)
            app_dir="${2:?--app-dir requires a value}"
            shift 2
            ;;
        --method)
            method="${2:?--method requires a value}"
            shift 2
            ;;
        *)
            echo "Unknown option: $1" >&2
            exit 64
            ;;
    esac
done

if [[ ! "$prefix" =~ ^/[A-Za-z0-9._+/-]+$ ||
      ! "$app_dir" =~ ^/[A-Za-z0-9._+/-]+$ ||
      "$prefix" =~ (^|/)\.\.?(/|$) ||
      "$app_dir" =~ (^|/)\.\.?(/|$) ]]; then
    echo "--prefix and --app-dir must be absolute paths without whitespace, shell metacharacters, or '..' segments." >&2
    exit 64
fi
if [[ -n "$destdir" && "$destdir" != /* ]]; then
    echo "--destdir must be an absolute path." >&2
    exit 64
fi
if [[ "$destdir" =~ (^|/)\.\.?(/|$) ]]; then
    echo "--destdir cannot contain '.' or '..' path segments." >&2
    exit 64
fi

applications_dir="${destdir}${prefix}/share/applications"
metainfo_dir="${destdir}${prefix}/share/metainfo"
icons_dir="${destdir}${prefix}/share/icons/hicolor"
bin_dir="${destdir}${prefix}/bin"
desktop_file="$applications_dir/$app_id.desktop"
wrapper_file="$bin_dir/devolutions-terminal-x-terminal-emulator"
state_home="${XDG_STATE_HOME:-${HOME:?HOME is required}/.local/state}/devolutions-terminal"
config_home="${XDG_CONFIG_HOME:-${HOME:?HOME is required}/.config}"
data_home="${XDG_DATA_HOME:-${HOME:?HOME is required}/.local/share}"

refresh_caches() {
    [[ -n "$destdir" ]] && return
    if command -v update-desktop-database >/dev/null 2>&1; then
        update-desktop-database "$applications_dir"
    else
        echo "warning: update-desktop-database is unavailable; install desktop-file-utils to refresh the application cache." >&2
    fi
    if command -v gtk-update-icon-cache >/dev/null 2>&1; then
        gtk-update-icon-cache --force --ignore-theme-index "$icons_dir"
    else
        echo "warning: gtk-update-icon-cache is unavailable; the desktop may refresh the icon cache later." >&2
    fi
}

install_assets() {
    install -d "$applications_dir" "$metainfo_dir" "$bin_dir"
    sed "s|/opt/devolutions-terminal/Devolutions.Terminal|$app_dir/Devolutions.Terminal|g" \
        "$script_dir/$app_id.desktop" > "$desktop_file"
    chmod 0644 "$desktop_file"
    install -m 0644 "$script_dir/$app_id.metainfo.xml" "$metainfo_dir/$app_id.metainfo.xml"
    for size in 16 32 48 64 96 256; do
        install -d "$icons_dir/${size}x${size}/apps"
        install -m 0644 \
            "$script_dir/icons/$app_id-${size}.png" \
            "$icons_dir/${size}x${size}/apps/$app_id.png"
    done
    cat > "$wrapper_file" <<EOF
#!/bin/sh
if [ "\${1-}" = "-e" ] || [ "\${1-}" = "--" ]; then
    shift
fi
if [ "\$#" -eq 0 ]; then
    exec "$app_dir/Devolutions.Terminal"
fi
exec "$app_dir/Devolutions.Terminal" -- "\$@"
EOF
    chmod 0755 "$wrapper_file"
    refresh_caches
}

uninstall_assets() {
    rm -f -- "$desktop_file" "$metainfo_dir/$app_id.metainfo.xml" "$wrapper_file"
    for size in 16 32 48 64 96 256; do
        rm -f -- "$icons_dir/${size}x${size}/apps/$app_id.png"
    done
    refresh_caches
}

require_live_user_action() {
    if [[ -n "$destdir" ]]; then
        echo "$action changes live user or system configuration and cannot be used with DESTDIR." >&2
        exit 64
    fi
}

register_protocol() {
    require_live_user_action
    command -v xdg-mime >/dev/null 2>&1 ||
        { echo "xdg-mime is required; install xdg-utils." >&2; exit 69; }
    [[ -f "${prefix}/share/applications/$app_id.desktop" ]] ||
        { echo "Install $app_id.desktop before registering the protocol." >&2; exit 66; }
    install -d "$state_home"
    local previous
    if ! previous="$(xdg-mime query default "$scheme")"; then
        echo "Could not query the current $scheme handler." >&2
        exit 1
    fi
    if [[ ! -f "$state_home/protocol.previous" ]]; then
        printf '%s\n' "$previous" > "$state_home/protocol.previous"
    fi
    xdg-mime default "$app_id.desktop" "$scheme"
}

remove_owned_mime_assignment() {
    local file temporary
    for file in \
        "$config_home/mimeapps.list" \
        "$data_home/applications/mimeapps.list"; do
        [[ -f "$file" ]] || continue
        temporary="$file.devolutions-terminal.$$"
        awk -v key="$scheme" -v value="$app_id.desktop" '
            index($0, key "=") == 1 {
                handlers = substr($0, length(key) + 2)
                count = split(handlers, input, ";")
                output = ""
                for (i = 1; i <= count; i++) {
                    if (input[i] != "" && input[i] != value) {
                        output = output input[i] ";"
                    }
                }
                if (output == "") next
                print key "=" output
                next
            }
            { print }
        ' "$file" > "$temporary"
        mv -f -- "$temporary" "$file"
    done
}

unregister_protocol() {
    require_live_user_action
    command -v xdg-mime >/dev/null 2>&1 ||
        { echo "xdg-mime is required; install xdg-utils." >&2; exit 69; }
    local current previous=""
    if ! current="$(xdg-mime query default "$scheme")"; then
        echo "Could not query the current $scheme handler." >&2
        exit 1
    fi
    [[ -f "$state_home/protocol.previous" ]] &&
        previous="$(<"$state_home/protocol.previous")"
    if [[ "$current" == "$app_id.desktop" ]]; then
        if [[ -n "$previous" ]]; then
            xdg-mime default "$previous" "$scheme"
        else
            remove_owned_mime_assignment
        fi
    fi
    rm -f -- "$state_home/protocol.previous"
}

set_xdg_default_terminal() {
    command -v xdg-terminal-exec >/dev/null 2>&1 ||
        { echo "xdg-terminal-exec is unavailable. Install it or use --method alternatives on a Debian-family distribution." >&2; exit 69; }
    [[ -f "${prefix}/share/applications/$app_id.desktop" ]] ||
        { echo "Install $app_id.desktop before selecting it as the default terminal." >&2; exit 66; }
    install -d "$config_home" "$state_home"
    local list
    if [[ -f "$state_home/default-terminal.list-path" ]]; then
        list="$(cat "$state_home/default-terminal.list-path")"
    else
        local desktop="${XDG_CURRENT_DESKTOP:-}"
        desktop="${desktop%%:*}"
        desktop="${desktop,,}"
        if [[ "$desktop" =~ ^[a-z0-9_-]+$ ]]; then
            list="$config_home/${desktop}-xdg-terminals.list"
        else
            list="$config_home/xdg-terminals.list"
        fi
        printf '%s\n' "$list" > "$state_home/default-terminal.list-path"
    fi
    local temporary="$list.devolutions-terminal.$$"
    if [[ ! -f "$state_home/default-terminal.was-present" ]]; then
        if [[ -f "$list" ]] && grep -Fxq "$app_id.desktop" "$list"; then
            printf 'yes\n' > "$state_home/default-terminal.was-present"
        else
            printf 'no\n' > "$state_home/default-terminal.was-present"
        fi
    fi
    {
        printf '%s\n' "$app_id.desktop"
        [[ ! -f "$list" ]] || grep -Fxv "$app_id.desktop" "$list" || [[ $? -eq 1 ]]
    } > "$temporary"
    mv -f -- "$temporary" "$list"
    printf 'xdg\n' > "$state_home/default-terminal.method"
}

unset_xdg_default_terminal() {
    local list="$config_home/xdg-terminals.list"
    if [[ -f "$state_home/default-terminal.list-path" ]]; then
        list="$(cat "$state_home/default-terminal.list-path")"
    fi
    local temporary="$list.devolutions-terminal.$$"
    local was_present="no"
    if [[ -f "$state_home/default-terminal.was-present" ]]; then
        was_present="$(cat "$state_home/default-terminal.was-present")"
    fi
    if [[ -f "$list" && "$was_present" != "yes" ]]; then
        grep -Fxv "$app_id.desktop" "$list" > "$temporary" || [[ $? -eq 1 ]]
        mv -f -- "$temporary" "$list"
    fi
    rm -f -- \
        "$state_home/default-terminal.was-present" \
        "$state_home/default-terminal.list-path" \
        "$state_home/default-terminal.method"
}

set_alternatives_default_terminal() {
    [[ "$(id -u)" -eq 0 ]] ||
        { echo "The alternatives method must run as root." >&2; exit 77; }
    command -v update-alternatives >/dev/null 2>&1 ||
        { echo "update-alternatives is unavailable on this distribution." >&2; exit 69; }
    local target="${prefix}/bin/devolutions-terminal-x-terminal-emulator"
    [[ -x "$target" ]] ||
        { echo "Install the terminal wrapper at $target first." >&2; exit 66; }
    install -d "$state_home"
    if [[ ! -f "$state_home/alternatives.previous" ]]; then
        local alternatives
        if alternatives="$(update-alternatives --query x-terminal-emulator 2>/dev/null)"; then
            awk '/^Value: / { sub(/^Value: /, ""); print; exit }' \
                <<<"$alternatives" > "$state_home/alternatives.previous"
        else
            : > "$state_home/alternatives.previous"
            echo "No previous x-terminal-emulator alternative was found; unset will return alternatives to automatic mode." >&2
        fi
    fi
    update-alternatives --install /usr/bin/x-terminal-emulator x-terminal-emulator "$target" 40
    update-alternatives --set x-terminal-emulator "$target"
    printf 'alternatives\n' > "$state_home/default-terminal.method"
}

unset_alternatives_default_terminal() {
    [[ "$(id -u)" -eq 0 ]] ||
        { echo "The alternatives method must run as root." >&2; exit 77; }
    local target="${prefix}/bin/devolutions-terminal-x-terminal-emulator"
    local previous=""
    [[ -f "$state_home/alternatives.previous" ]] &&
        previous="$(<"$state_home/alternatives.previous")"
    if [[ -n "$previous" && -x "$previous" ]]; then
        update-alternatives --set x-terminal-emulator "$previous"
    else
        update-alternatives --auto x-terminal-emulator
    fi
    update-alternatives --remove x-terminal-emulator "$target"
    rm -f -- "$state_home/alternatives.previous" "$state_home/default-terminal.method"
}

set_default_terminal() {
    require_live_user_action
    case "$method" in
        auto|xdg)
            set_xdg_default_terminal
            ;;
        alternatives)
            set_alternatives_default_terminal
            ;;
        *)
            echo "--method must be auto, xdg, or alternatives." >&2
            exit 64
            ;;
    esac
}

unset_default_terminal() {
    require_live_user_action
    local saved_method="$method"
    if [[ "$saved_method" == "auto" ]]; then
        if [[ -f "$state_home/default-terminal.method" ]]; then
            saved_method="$(cat "$state_home/default-terminal.method")"
        else
            saved_method="xdg"
        fi
    fi
    case "$saved_method" in
        xdg) unset_xdg_default_terminal ;;
        alternatives) unset_alternatives_default_terminal ;;
        *)
            echo "No recognized default-terminal registration state was found." >&2
            exit 66
            ;;
    esac
}

diagnose() {
    printf 'Desktop: %s\n' "${XDG_CURRENT_DESKTOP:-unknown}"
    printf 'D-Bus session: %s\n' "$([[ -n "${DBUS_SESSION_BUS_ADDRESS:-}" ]] && printf available || printf unavailable)"
    for command in gdbus xdg-open notify-send xdg-mime xdg-terminal-exec update-desktop-database gtk-update-icon-cache update-alternatives; do
        if command -v "$command" >/dev/null 2>&1; then
            printf '%s: available\n' "$command"
        else
            printf '%s: unavailable\n' "$command"
        fi
    done
    printf 'Tray capability: desktop/backend dependent (no reliable freedesktop probe)\n'
    printf 'Global summon shortcut portal: unsupported\n'
}

case "$action" in
    install) install_assets ;;
    uninstall) uninstall_assets ;;
    register-protocol) register_protocol ;;
    unregister-protocol) unregister_protocol ;;
    set-default-terminal) set_default_terminal ;;
    unset-default-terminal) unset_default_terminal ;;
    diagnose) diagnose ;;
    *)
        echo "Unknown action: $action" >&2
        exit 64
        ;;
esac
