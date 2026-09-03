# Devolutions Terminal

A cross-platform terminal emulator implemented in C# on **.NET 10**, published
with **NativeAOT**, and rendered with **Avalonia 12** / Skia:

- **ConPTY** on Windows and a real **forkpty** transport on Linux and macOS
- Azure Cloud Shell for remote Azure sessions
- selectable built-in or **Ghostty** VT engine
- Windows Terminal-compatible `settings.json`, actions, keybindings, and `dt` CLI

This is the [Devolutions Terminal](https://github.com/Devolutions/devolutions-terminal)
source tree. Projects, namespaces, and the GUI host use `Devolutions.Terminal.*`.
The CLI executable is `dt`.

## Build and run

```powershell
dotnet test Devolutions.Terminal.slnx
dotnet run --project src/Devolutions.Terminal
```

## NativeAOT publish

```powershell
dotnet publish src/Devolutions.Terminal -c Release -r win-x64 --self-contained
```

The native executable is written to
`src/Devolutions.Terminal/bin/Release/net10.0/win-x64/publish/Devolutions.Terminal.exe`.

Linux x64 and ARM64 NativeAOT packages are built on Linux with:

```bash
scripts/Build-LinuxPackage.sh linux-x64 0.1.0 artifacts/packages all
scripts/Build-LinuxPackage.sh linux-arm64 0.1.0 artifacts/packages all
bash scripts/Test-LinuxPackage.sh linux-x64 artifacts/packages/*-linux-x64.*
```

The builder emits `.tar.gz`, `.deb`, `.rpm`, `.AppImage`, and `.sha256` files.
Every format consumes the same normalized `/opt/devolutions-terminal` payload
and `/usr` desktop integration layout. `SOURCE_DATE_EPOCH` controls package
timestamps.

After extracting a tarball at the filesystem root:

```bash
sudo /opt/devolutions-terminal/linux/Install-LinuxDesktopIntegration.sh install
/opt/devolutions-terminal/linux/Install-LinuxDesktopIntegration.sh register-protocol
/opt/devolutions-terminal/linux/Install-LinuxDesktopIntegration.sh set-default-terminal
```

## MSIX packages

```powershell
.\src\Devolutions.Terminal.Package\Scripts\Build-Packages.ps1
```

Development signing, trust, install, validation, and uninstall commands are in
[`src/Devolutions.Terminal.Package/README.md`](src/Devolutions.Terminal.Package/README.md).

## Settings and engines

Settings are stored at `%LOCALAPPDATA%\Devolutions\Terminal\settings.json` on
Windows and under `$XDG_CONFIG_HOME/devolutions-terminal` on Linux, with the
usual `~/.config` fallback.
Set `DTERM_SETTINGS_PATH` (or the compatibility alias `WT_DOTNET_SETTINGS_PATH`)
to load a specific settings file. Runtime state is stored atomically in
`state.json` beside that file.

Set `"experimental.terminalEngine": "ghostty"` to use the pinned
`libghostty-vt` engine globally. A profile can override it with `"builtin"` or
`"ghostty"`. ConPTY remains the Windows process transport for both engines.

The compiled-XAML settings editor is documented in
[docs/settings-editor.md](docs/settings-editor.md).

## Architecture

```
Devolutions.Terminal.Core         VT parser + text buffer + terminal engine
Devolutions.Terminal.Ghostty      NativeAOT-safe libghostty-vt engine adapter
Devolutions.Terminal.Render       Immutable plans + HarfBuzz/Skia glyph renderer
Devolutions.Terminal.Connection   ConPTY + Linux PTY + Azure Cloud Shell
Devolutions.Terminal.Settings     Layered Windows Terminal-compatible JSON settings
Devolutions.Terminal.Control      Avalonia TermControl renderer
Devolutions.Terminal.App   Tabs, title bar, panes, actions, window behavior
Devolutions.Terminal       NativeAOT executable and composition root
```

The measured remaining parity contract is in
[docs/parity-status.md](docs/parity-status.md).
Architecture decisions are recorded in [docs/decisions](docs/decisions).
Renderer contracts are documented in [docs/renderer.md](docs/renderer.md).
Control, clipboard, IME, and accessibility contracts are documented in
[docs/control-accessibility.md](docs/control-accessibility.md).
Advanced VT protocols are documented in
[docs/advanced-vt-protocols.md](docs/advanced-vt-protocols.md).
Azure Cloud Shell is documented in [docs/azure-cloud-shell.md](docs/azure-cloud-shell.md).
Build and release gates are documented in [docs/release.md](docs/release.md).

## Compatibility inventory

The port tracks Windows Terminal settings, actions, VT dispatch, command line,
and settings-page surfaces in
[`compat/windows-terminal.json`](compat/windows-terminal.json). Tests use that
checked-in snapshot. Regenerating it requires a separate Microsoft Windows
Terminal C++ checkout:

```powershell
dotnet run --project tools/Devolutions.Terminal.PortInventory -- <windows-terminal-checkout> compat/windows-terminal.json
```
