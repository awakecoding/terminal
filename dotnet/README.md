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
dotnet test
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

## Architecture

```
Terminal.Core         VT parser + text buffer + terminal engine
Terminal.Connection   ConPTY (LibraryImport, NativeAOT-safe)
Terminal.Settings     JSON settings (source-generated)
Terminal.Control      Avalonia TermControl renderer
WindowsTerminal       Tabs, title bar, app host
```

This is not a line-for-line translation of every C++ file. The engine covers the
VT sequences needed for modern shells, editors, and TUIs (SGR including truecolor,
cursor/erase, scroll regions, alt screen, OSC titles, mouse/bracketed-paste modes).
