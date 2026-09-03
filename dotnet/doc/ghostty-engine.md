# Ghostty terminal engine

The Windows host always uses ConPTY for local processes. The optional Ghostty
mode replaces the VT parser/state engine, not the Windows pseudoconsole or the
Avalonia renderer.

Select it globally:

```json
{
  "experimental.terminalEngine": "ghostty"
}
```

Or override a profile:

```json
{
  "profiles": {
    "list": [
      {
        "name": "PowerShell",
        "experimental.terminalEngine": "ghostty"
      }
    ]
  }
}
```

`builtin` remains the default. New and restarted terminal sessions resolve the
setting; an already running session keeps its current engine.

## Native integration

`Terminal.Ghostty` uses source-generated `LibraryImport` calls and safe handles.
The pinned x64 and ARM64 `ghostty-vt.dll` files are copied by RID into JIT,
NativeAOT, and MSIX outputs. The ABI manifest is validated at startup before a
Ghostty engine is created. The pinned source commit, hashes, license, and
rebuild procedure are in `native/ghostty`.

## Current rendering boundary

Ghostty owns VT parsing, modes, viewport state, resize/reflow, scrollback,
terminal replies, titles, and bells. Its render-state API is projected into the
shared immutable cell model consumed by the existing Skia renderer. This keeps
Avalonia tabs, panes, accessibility, clipboard, selection, search, and window
behavior common across engines.

The pinned C ABI exposes no Sixel, OSC 1337, or ConEmu image resources. The
engine excludes those image capability flags and emits deterministic unsupported
diagnostics instead of pretending to project graphics or silently switching the
selected engine.
