# Windows Terminal — .NET 10 / NativeAOT / Avalonia port

A C# reimplementation of the Windows Terminal shell experience:

- **.NET 10** with **NativeAOT**
- **Avalonia 11** UI instead of WinUI/XAML Islands
- **ConPTY** for local shells and Azure Cloud Shell for remote Azure sessions
- VT/xterm parser, text buffer, tabs/panes, settings-driven actions and keybindings

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

Settings are stored at `%LOCALAPPDATA%\WindowsTerminal.NET\settings.json`.
Set `WT_DOTNET_SETTINGS_PATH` to load a specific Windows Terminal settings file.
Runtime application state is stored atomically in `state.json` beside that file.
The settings loader applies embedded defaults, generated profiles, extension
fragments, then the user layer. Its typed action map covers the complete
generated action inventory; actions not yet implemented by the Avalonia shell
produce an explicit unsupported dispatch result. Dynamic discovery covers installed PowerShell,
Windows PowerShell, Command Prompt, WSL distributions, OpenSSH config hosts, and
Visual Studio developer shells. Sources honor `disabledProfileSources`, retain
upstream GUID/source identities, and reconcile removed generated profiles through
`state.json`. Set `WT_RUN_MACHINE_PROFILE_TESTS=1` to opt into machine-dependent
generator smoke coverage.
The compiled-XAML settings editor and host integration are documented in
[doc/settings-editor.md](doc/settings-editor.md).

## Architecture

```
Terminal.Core         VT parser + text buffer + terminal engine
Terminal.Render       Immutable plans + HarfBuzz/Skia glyph renderer
Terminal.Connection   ConPTY + Azure Cloud Shell (HTTP/WebSocket, NativeAOT)
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
DECRQSS/XTGETTCAP reports, and OSC 1337 inline-image metadata.
`TextBuffer.CreateSnapshot` and `TerminalEngine.CreateSnapshot` provide detached,
read-only cell snapshots for render and test consumers; the existing live
`TextBuffer.GetRow` API remains available to `Terminal.Control`.

The full phased plan for a complete port is in [PORTING.md](PORTING.md).
Architecture decisions are recorded in [doc/decisions](doc/decisions).
Renderer contracts, cache ownership, and integration details are documented in
[doc/renderer.md](doc/renderer.md).
Control interaction, clipboard safety, IME, and accessibility contracts are documented in
[doc/control-accessibility.md](doc/control-accessibility.md).
The image-overlay renderer contract and protocol limits are documented in
[doc/advanced-vt-protocols.md](doc/advanced-vt-protocols.md).
Azure authentication, service protocol, diagnostics, and host composition are
documented in [doc/azure-cloud-shell.md](doc/azure-cloud-shell.md).

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
