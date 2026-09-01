# Windows Terminal — .NET 10 / NativeAOT / Avalonia port

A C# reimplementation of the Windows Terminal shell experience:

- **.NET 10** with **NativeAOT**
- **Avalonia 11** UI instead of WinUI/XAML Islands
- **ConPTY** for hosting `pwsh`, Windows PowerShell, and `cmd`
- VT/xterm parser, text buffer, tabs, copy/paste, Campbell color scheme

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

| Chord | Action |
| --- | --- |
| `Ctrl+Shift+T` | New tab |
| `Ctrl+Shift+W` | Close tab |
| `Ctrl+Shift+N` | New window |
| `Ctrl+Shift+C` | Copy |
| `Ctrl+Shift+V` | Paste |

Settings are stored at `%LOCALAPPDATA%\WindowsTerminal.NET\settings.json`.
Set `WT_DOTNET_SETTINGS_PATH` to load a specific Windows Terminal settings file.
Runtime application state is stored atomically in `state.json` beside that file.
The settings loader applies embedded defaults, fragments, then the user layer;
actions and keybindings are preserved losslessly for the dedicated action phase.

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
VT sequences needed for modern shells, editors, and TUIs (SGR including truecolor,
cursor/erase, scroll regions, alt screen, OSC titles, mouse/bracketed-paste modes).

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
