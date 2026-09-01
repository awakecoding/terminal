# Windows Terminal — .NET 10 / NativeAOT / Avalonia port

A C# reimplementation of the Windows Terminal shell experience:

- **.NET 10** with **NativeAOT**
- **Avalonia 11** UI instead of WinUI/XAML Islands
- **ConPTY** for hosting `pwsh`, Windows PowerShell, and `cmd`
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

## Keyboard

Keyboard input is resolved through the Windows Terminal-compatible `actions` and
`keybindings` settings. The embedded defaults include tab, pane, clipboard,
scrollback, font, find, settings, and command-palette bindings. User bindings can
refer to command IDs or inline commands; normalized chord aliases, unbinding, and
last-definition-wins conflicts match the settings model.

Settings are stored at `%LOCALAPPDATA%\WindowsTerminal.NET\settings.json`.
Set `WT_DOTNET_SETTINGS_PATH` to load a specific Windows Terminal settings file.
Runtime application state is stored atomically in `state.json` beside that file.
The settings loader applies embedded defaults, fragments, then the user layer.
Its typed action map covers the complete generated action inventory. Actions not
yet implemented by the Avalonia shell still parse and produce an explicit
unsupported dispatch result.

## Architecture

```
Terminal.Core         VT parser + text buffer + terminal engine
Terminal.Render       Renderer-neutral contracts; Skia atlas lands in P1
Terminal.Connection   ConPTY (safe handles, cancellation, NativeAOT)
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
`TextBuffer.CreateSnapshot` and `TerminalEngine.CreateSnapshot` provide detached,
read-only cell snapshots for render and test consumers; the existing live
`TextBuffer.GetRow` API remains available to `Terminal.Control`.

The full phased plan for a complete port is in [PORTING.md](PORTING.md).
Architecture decisions are recorded in [doc/decisions](doc/decisions).

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
