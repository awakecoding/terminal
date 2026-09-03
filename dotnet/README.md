# Windows Terminal — .NET 10 / NativeAOT / Avalonia port

A C# reimplementation of the Windows Terminal shell experience:

- **.NET 10** with **NativeAOT**
- **Avalonia 12** UI instead of WinUI/XAML Islands
- **ConPTY** on Windows and a real **forkpty** transport on Linux
- Azure Cloud Shell for remote Azure sessions
- selectable built-in or **Ghostty** VT engine
- text buffer, tabs/panes, settings-driven actions and keybindings

The original C++/WinUI tree is unchanged. This port lives entirely under `dotnet/`.

## Build and run

```powershell
cd dotnet
dotnet test Terminal.slnx
dotnet run --project src/WindowsTerminal
```

## NativeAOT publish

```powershell
cd dotnet
dotnet publish src/WindowsTerminal -c Release -r win-x64 --self-contained
```

The native executable is written to
`src/WindowsTerminal/bin/Release/net10.0/win-x64/publish/WindowsTerminal.exe`.

Linux x64 and ARM64 NativeAOT packages are built on Linux with:

```bash
dotnet/scripts/Build-LinuxPackage.sh linux-x64 0.1.0 artifacts/packages all
dotnet/scripts/Build-LinuxPackage.sh linux-arm64 0.1.0 artifacts/packages all
bash dotnet/scripts/Test-LinuxPackage.sh linux-x64 artifacts/packages/*-linux-x64.*
```

The builder emits `.tar.gz`, `.deb`, `.rpm`, `.AppImage`, and `.sha256` files.
Pass `tar`, `deb`, `rpm`, or `appimage` as the fourth argument to build one
format. Every format consumes the same normalized `/opt/windows-terminal-dotnet`
payload and `/usr` desktop integration layout, including NativeAOT hosts,
architecture-matched Ghostty/Skia/HarfBuzz/PTY assets, licenses, a deterministic
inventory, and an SPDX 2.3 SBOM. `SOURCE_DATE_EPOCH` controls all package
timestamps. RPM output requires `rpmbuild`; AppImage output requires
`mksquashfs` and a pinned architecture-matched type-2 runtime supplied through
`APPIMAGE_RUNTIME_FILE`. Packaging performs no runtime or tool download.
Missing tools produce install instructions before publishing starts.

The archive includes a reversible installer helper under
`/opt/windows-terminal-dotnet/linux/`. After extracting it at the filesystem
root:

```bash
sudo /opt/windows-terminal-dotnet/linux/Install-LinuxDesktopIntegration.sh install
/opt/windows-terminal-dotnet/linux/Install-LinuxDesktopIntegration.sh register-protocol
/opt/windows-terminal-dotnet/linux/Install-LinuxDesktopIntegration.sh set-default-terminal
```

The first command installs the `.desktop` entry, AppStream metadata, hicolor
icons, and an `x-terminal-emulator` compatibility wrapper. The latter two are
explicit per-user actions: they register the `wt-dotnet:` handler and prepend
the desktop ID to the applicable desktop-specific or generic
`xdg-terminals.list` when `xdg-terminal-exec` is available.
Use `unregister-protocol`, `unset-default-terminal`, then `uninstall` to reverse
them. `--destdir` and `DESTDIR` stage integration assets without changing live
configuration; `--prefix` and `--app-dir` support non-default layouts.

On Debian-family systems without `xdg-terminal-exec`, an administrator may
explicitly select the compatibility wrapper with
`set-default-terminal --method alternatives`; the matching unset action restores
the previous alternative. No install action changes a user's protocol or
default-terminal choice automatically.

## MSIX packages

The package project builds unsigned `win-x64` and `win-arm64` MSIX packages and
an architecture-selecting bundle without affecting direct unpackaged runs:

```powershell
cd dotnet
.\src\WindowsTerminal.Package\Scripts\Build-Packages.ps1
```

Development signing, trust, install, validation, and uninstall commands are in
[`src/WindowsTerminal.Package/README.md`](src/WindowsTerminal.Package/README.md).

## Keyboard

Keyboard input is resolved through the Windows Terminal-compatible `actions` and
`keybindings` settings. The embedded defaults include tab, pane, clipboard,
scrollback, font, find, settings, and command-palette bindings. User bindings can
refer to command IDs or inline commands; normalized chord aliases, unbinding, and
last-definition-wins conflicts match the settings model.

Settings are stored at `%LOCALAPPDATA%\WindowsTerminal.NET\settings.json` on
Windows and under `$XDG_CONFIG_HOME/windows-terminal-dotnet` on Linux, with the
usual `~/.config` fallback.
Set `WT_DOTNET_SETTINGS_PATH` to load a specific Windows Terminal settings file.
Runtime application state is stored atomically in `state.json` beside that file.
Set `"experimental.terminalEngine": "ghostty"` to use the pinned
`libghostty-vt` engine globally. A profile can override it with the same key
set to `"builtin"` or `"ghostty"`. ConPTY remains the Windows process transport
for both engines; the setting controls VT parsing, state, modes, and reflow.
The settings loader applies embedded defaults, generated profiles, extension
fragments, then the user layer. Its typed action map covers the complete
generated action inventory; actions not yet implemented by the Avalonia shell
produce an explicit unsupported dispatch result. Dynamic discovery covers installed PowerShell,
Windows PowerShell, Command Prompt, WSL distributions, and
Visual Studio developer shells. Sources honor `disabledProfileSources`, retain
upstream GUID/source identities, and reconcile removed generated profiles through
`state.json`. Set `WT_RUN_MACHINE_PROFILE_TESTS=1` to opt into machine-dependent
generator smoke coverage.
Dynamic SSH profiles match native stable WT's feature gate and are disabled by
default; set `WT_ENABLE_SSH_PROFILES=1` to opt into OpenSSH config discovery.
Azure Cloud Shell is generated when `WT_AZURE_CLIENT_ID` contains the GUID of a
host-owned Entra public-client application. The host supplies device-code and
tenant-selection UI and never receives or persists access/refresh tokens.
The command palette supports action, `wt` command-line, tab-search, profile-launch,
and shell command-history flows. Settings-driven notification-area behavior is
available for unpackaged and packaged runs.
On Windows, the x64/ARM64 MSIX includes a native `IExplorerCommand` for
directory/background **Open in Terminal**, profile-derived jump lists refreshed
after settings saves, and system toasts whose bounded activation payload is
validated before broker routing. Packaged operations derive the AUMID from
package identity. Supported unpackaged toast use requires a registered
Start-menu shortcut plus `WT_DOTNET_AUMID` and
`WT_DOTNET_TOAST_SHORTCUT`; otherwise diagnostics explicitly report the missing
identity. The OpenConsole default-terminal handoff remains unavailable behind a
versioned native helper boundary. Global summon and quake route named windows
through the broker; Windows hotkeys use collision-safe `RegisterHotKey` and
re-register after settings saves.
On Linux, file, directory, and URI opening prefers the freedesktop portal over
the session D-Bus and falls back to `xdg-open`. System notifications similarly
prefer the portal and fall back to `notify-send`; the in-app accessible
notification remains visible if both providers fail. Run
`WindowsTerminal --diagnose-desktop` or the install helper's `diagnose` action
for detected providers and actionable missing-package diagnostics. Broker and
manual summon work on Linux, but global shortcut registration is explicitly
unsupported until a reflection-free interactive GlobalShortcuts portal session
provider is bundled.
Stock visual defaults mirror Windows Terminal: Fluent/Segoe UI chrome, equal-width
tabs with profile icons and visible profile titles, 12-point packaged Cascadia
Mono with Atlas-compatible cell rounding, Campbell colors, 8-DIP default
padding and scrollbar behavior, `%USERPROFILE%` startup directories, and per-session
`WT_SESSION`/`WT_PROFILE_ID` (including WSL forwarding).
The compiled-XAML settings editor and host integration are documented in
[doc/settings-editor.md](doc/settings-editor.md).

## Architecture

```
Terminal.Core         VT parser + text buffer + terminal engine
Terminal.Ghostty      NativeAOT-safe libghostty-vt engine adapter
Terminal.Render       Immutable plans + HarfBuzz/Skia glyph renderer
Terminal.Connection   ConPTY + Linux PTY + Azure Cloud Shell
Terminal.Settings     Layered Windows Terminal-compatible JSON settings
Terminal.Control      Avalonia TermControl renderer
WindowsTerminal.App   Tabs, title bar, panes, actions, window behavior
WindowsTerminal       NativeAOT executable and composition root
```

This is not a line-for-line translation of every C++ file. The engine covers the
VT sequences needed for modern shells, editors, and TUIs: bounded circular
scrollback with resize reflow, wide/combining cells, main/alternate buffers,
editing and cursor commands, DEC/ANSI modes and reports, SGR including truecolor,
OSC color resources/titles/working directories/hyperlinks, and incremental UTF-8.
Advanced Core protocols add bounded DCS parsing, Sixel indexed/RGBA images,
DECRQSS/XTGETTCAP reports, OSC 1337/ConEmu inline-image metadata, and stable
logical-line image ownership through scrollback and reflow.
`TextBuffer.CreateSnapshot` and `TerminalEngine.CreateSnapshot` provide detached,
read-only cell snapshots for render and test consumers; the existing live
`TextBuffer.GetRow` API remains available to `Terminal.Control`.

The full phased plan for a complete port is in [PORTING.md](PORTING.md).
The measured remaining parity contract is in
[doc/parity-status.md](doc/parity-status.md).
Architecture decisions are recorded in [doc/decisions](doc/decisions).
Renderer contracts, cache ownership, and integration details are documented in
[doc/renderer.md](doc/renderer.md).
Control interaction, clipboard safety, IME, and accessibility contracts are documented in
[doc/control-accessibility.md](doc/control-accessibility.md).
The image-overlay renderer contract and protocol limits are documented in
[doc/advanced-vt-protocols.md](doc/advanced-vt-protocols.md).
Azure authentication, service protocol, diagnostics, and host composition are
documented in [doc/azure-cloud-shell.md](doc/azure-cloud-shell.md).
Build, migration, signing, and release gates are documented in
[doc/release.md](doc/release.md).
The UniGetUI/Avalonia 12 theme analysis and adopted WinUI settings patterns are
documented in [doc/unigetui-theme-adoption.md](doc/unigetui-theme-adoption.md).

## Compatibility inventory

The port tracks the C++ implementation's settings, actions, VT dispatch,
command line, and settings-page surfaces in
[`compat/windows-terminal.json`](compat/windows-terminal.json). Regenerate it
after an intentional upstream compatibility change:

```powershell
dotnet run --project tools/Terminal.PortInventory -- .. compat/windows-terminal.json
```

The compatibility test fails when the C++ source changes without updating this
manifest, making parity work explicit.
