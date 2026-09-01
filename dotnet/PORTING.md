# Complete .NET 10 / NativeAOT / Avalonia port

This is the plan for turning the `dotnet/` prototype into a **behavior- and
settings-compatible** Windows Terminal, hosted on .NET 10, NativeAOT, and
Avalonia instead of C++/WinRT/WinUI.

It is **not** a line-for-line transcription of CppWinRT, MIDL, or XAML Islands.
Those types do not exist in this stack. The bar is:

1. A user can point the app at a real `settings.json` and get the same profiles,
   schemes, keybindings, and startup behavior for the supported subset.
2. Modern shells, editors, and TUIs (`pwsh`, `cmd`, WSL, neovim, lazygit) work.
3. NativeAOT publish of a single `WindowsTerminal.exe` stays green.

The original C++ tree stays in place. All new work lives under `dotnet/`.

## Current baseline (done)

| Project | What it covers today |
| --- | --- |
| `Terminal.Core` | Cell/attributes, text buffer, VT ground/CSI/OSC subset, alt screen, SGR (16/256/truecolor) |
| `Terminal.Render` | Renderer-neutral contracts for the future Skia glyph atlas |
| `Terminal.Connection` | NativeAOT-safe ConPTY via `LibraryImport`, transactional safe-handle lifecycle, cancellation, async writes, resize, and environment overrides |
| `Terminal.Settings` | Complete MTSM global/window/profile/font/appearance/theme/scheme/media/new-tab-menu projection; tri-state inheritance, legacy migrations, fragments/origins, diagnostics, stable profile GUIDs, deterministic PowerShell/WSL/SSH/Visual Studio generators, orphan reconciliation, local-layer diff serialization, and atomic `settings.json`/`state.json` persistence |
| `Terminal.Control` | Avalonia `TermControl`: Skia text, selection, copy/paste, key map |
| `WindowsTerminal.App` | Tabbed window, title bar, Ctrl+Shift+T/W/N/C/V |
| `WindowsTerminal` | NativeAOT executable and composition root |

The baseline also includes dedicated settings, connection, control, app,
compatibility, and UI test projects; x64/ARM64 NativeAOT CI; architecture
decisions; and a generated compatibility inventory covering 120 settings keys,
92 actions, 123 VT dispatch methods, 14 CLI commands, and 25 settings pages.

What it is **not**: a daily driver. No ActionMap, no settings UI, no
Atlas-quality rendering, no `wt` CLI, no search, no command palette, no
accessibility.

## Non-goals

Leave these in the C++ tree. Do not port them as part of this app:

- `conhost.exe` / console driver / server IOCTL protocol
- The inbox console property sheet (`propsheet`, `propslib`)
- WinUI, XAML Islands, CppWinRT, MIDL
- Store submission / OneBranch / PGO of the C++ build
- Pixel-identical AtlasEngine D3D shaders (match visually, not the HLSL)

Deferred until after P1 unless a user scenario requires them:

- Azure Cloud Shell
- Sixel / DRCS / VT macros
- Tab tear-off across processes
- `Open Terminal here` Explorer COM server (AOT-hostile)
- MSIX identity, jumplists, default-terminal handoff

## Compatibility bar

### Must remain compatible

- `settings.json` keys used by WT defaults + common user files
  (`profiles`, `profiles.defaults`, `schemes`, `actions`/`keybindings`,
  `theme`, launch/window settings). Unknown keys are preserved on round-trip.
- Profile GUIDs for PowerShell, cmd, Windows PowerShell.
- Default keybindings from `defaults.json`.
- `wt` subcommands that map to actions: `nt`, `sp`, `ft`, `mf`, `focus-tab`.
- OSC 0/2 title, OSC 7 cwd, OSC 8 hyperlinks, OSC 9;9 notifications,
  CSI SGR, CUP/ED/EL/IL/DL, DECSET 1/7/25/47/1049/2004/1000/1006,
  win32-input-mode where ConPTY emits it.

### May differ

- WinUI chrome, TabView, Mica/acrylic material (Avalonia equivalents)
- AtlasEngine D3D glyph cache internals
- UIA peer class names
- `ms-appx:///` icon URIs (map to `avares://` or file paths)
- Elevation / packaged identity until MSIX exists

## Architecture mapping

```
C++ / WinUI                         .NET / Avalonia
─────────────────────────────────   ──────────────────────────────────
buffer/out + TerminalCore           Terminal.Core (buffer, engine)
terminal/parser + adapter + input   Terminal.Core.Vt (parser, dispatch, input)
renderer/atlas + base               Terminal.Render (Skia atlas)
cascadia/TerminalConnection         Terminal.Connection
cascadia/TerminalSettingsModel      Terminal.Settings
cascadia/TerminalControl            Terminal.Control (TermControl + search)
cascadia/TerminalApp                WindowsTerminal.App (tabs, panes, actions)
cascadia/WindowsTerminal            WindowsTerminal (windowing host)
cascadia/TerminalSettingsEditor     WindowsTerminal.Settings (Avalonia pages)
```

Layer rules:

- **Core has no Avalonia, no Win32 UI.** Parser and buffer must be testable
  on any `net10.0` RID.
- **Connection is Windows-only** (`LibraryImport` ConPTY). Guard with
  `[SupportedOSPlatform("windows")]`.
- **Control talks to Core + Connection.** It owns rendering, input, selection,
  search box, scrollbar.
- **App owns ActionMap, panes, tabs, command palette, CLI.**
- **NativeAOT:** `LibraryImport` not `DllImport`; source-generated JSON;
  no runtime XAML loading; no reflection DI; `IsAotCompatible=true` on
  production projects.

Do not take a C++/CLI or C++/WinRT interop dependency on `Microsoft.Terminal.*`
DLLs. That reintroduces the runtime we are leaving.

## Renderer strategy

AtlasEngine (`src/renderer/atlas`) is a DWrite + D2D/D3D glyph atlas. Avalonia
already uses Skia. Porting Atlas 1:1 would mean a second graphics stack and
break NativeAOT simplicity.

**Decision: C# Skia glyph atlas inside `Terminal.Control` / `Terminal.Render`.**

Match Atlas *behavior*:

- Cell grid with wide glyphs, zero-width marks, line rendition later
- Dirty-row invalidation, not full-frame redraw
- Font fallback (Cascadia Mono → Consolas → Segoe UI Emoji)
- Bold/italic as real faces when present, synthetic otherwise
- Cursor styles: bar, vintage, underscore, filled/empty box
- Reverse video, underline, strikethrough
- Selection overlay and hyperlink underline
- Optional background image / acrylic later

P0 can keep the current `FormattedText` path. P1 replaces it with a glyph
atlas; that is the difference between “works” and “feels like Terminal”.

## Settings model

Reimplement `TerminalSettingsModel` in C#, not WinRT projections.

Implemented load order:

1. Embedded `defaults.json` (checked in, generated from the C++ copy)
2. Deterministic dynamic profiles (PowerShell, inbox shells, WSL, SSH, VS)
3. Fragment files (`%LOCALAPPDATA%\Microsoft\Windows Terminal\Fragments\`,
   `%PROGRAMDATA%\...`)
4. User `settings.json`

Fragment `updates` are applied after profile identity is known, including to
user-created profiles. Ordinary `null` clears an override and resumes
inheritance; nullable color/tab settings preserve an explicit null. User profile
order is retained before unmatched inbox/fragment profiles. Models expose
`User`, `InBox`, `Fragment`, `Generated`, and `ProfilesDefaults` origins.

`state.json` is a separate, source-generated model for settings hashes,
generated profiles, recent commands, dismissed UI, persisted windows, and named
workspaces. It is loaded independently (no settings layering), rejects malformed
payloads as a whole, and is written atomically.

Required types:

- Global/window settings from `MTSMSettings.h`
- Profiles with `profiles.defaults`, font, focused/unfocused appearance, and
  compatibility aliases
- Color schemes, theme pairs and nested themes, media resources, and new-tab menu
- Warnings for invalid defaults, profile/scheme/theme references, environment
  names, and menu structure

Actions and keybindings are deliberately retained as lossless raw JSON until the
dedicated action phase adds `ActionMap` and `ActionAndArgs`.

Typed JSON is source-generated (`System.Text.Json` + `JsonSerializerContext`).
Settings layering and unknown-property preservation operate on `JsonNode`, so
NativeAOT round-trips comments-stripped WT JSON without reflection.

Default actions live in a C# copy of the `actions` block from
`src/cascadia/TerminalSettingsModel/defaults.json`. Keep the JSON in sync;
do not hand-code key chords in the window.

## VT / buffer completeness

`ITermDispatch` is ~160 methods. The prototype implements the daily-driver
subset. Remaining work is grouped so tests can gate each bucket.

| Bucket | Sequences / features | Phase |
| --- | --- | --- |
| A. Editing | ICH/DCH/IL/DL/ECH, DECSTBM, SU/SD, DECSC/DECRC | P0 slice complete |
| B. Modes | DECCKM, DECAWM, DECTCEM, alt buffer, bracketed paste, mouse SGR | P0 slice complete |
| C. Reports | DA1/DA2, DSR CPR, DECRQM | P0 slice complete |
| D. Color | SGR 38/48 2/5, OSC 4/10/11/12, indexed/RGB | P0 slice complete |
| E. Unicode | UTF-8, wcwidth, emoji ZWJ (best-effort), reflow on resize | P0 reflow/wide/combining complete; grapheme shaping remains |
| F. Shell integration | OSC 7, OSC 133 marks, OSC 8 hyperlinks | OSC 7/8 complete; OSC 133 remains P1 |
| G. Input | Application keypad, win32-input-mode, Kitty protocol | P1 |
| H. Images | Sixel, OSC 1337, ConEmu | P2 |
| I. Rare VT | Rectangular ops, DECDLD, macros, DECRQSS, VT52 | P2 |

The .NET buffer now uses bounded circular scrollback, keeps independent
main/alternate state (cursor, margins, attributes, tab stops), reflows logical
wrapped lines on resize, repairs wide-cell boundaries, retains combining marks
and hyperlink metadata, and exposes detached read-only snapshots. Existing
`GetRow`, cursor, selection, and scroll-offset APIs remain compatible with
`Terminal.Control`.

Buffer work beyond the current parity slice:

- Double-width / double-height rows
- Full grapheme-cluster and emoji ZWJ shaping
- Search over the buffer (`src/buffer/out/search.cpp`)
- Image slices (sixel) as a later overlay

Parser/core gaps intentionally left for later buckets include DCS payload
dispatch (including sixel and DECRQSS), OSC 52/133/1337, selective and rectangular
erase/attribute operations, downloadable character sets, VT52 mode, and the
extended keyboard protocols.

Port tests from `src/terminal/parser/ut_parser` and
`src/cascadia/UnitTests_TerminalCore` as xUnit facts. That is the correctness
oracle — not visual screenshots.

## Connections

P0:

- ConPTY: create, resize, write UTF-8, wait, close
- Env inheritance + profile `environment` map
- `startingDirectory`, `commandline` expansion
- `closeOnExit` (always / graceful / never / automatic)
- Resize must call `ResizePseudoConsole` and `TerminalEngine.Resize`

P1:

- WSL path translation
- `elevate` via a small helper (not the C++ shim at first)
- Restart connection
- Inbound ConPTY listener / default-terminal handoff
- Working directory from OSC 7

P2:

- Azure Cloud Shell (`AzureConnection`)
- Process handoff from `wt` / `OpenConsole`

## Application shell

Reimplement `TerminalApp` + `WindowsTerminal` EXE as Avalonia, following the
same object graph:

```
App
 └─ Window (per IslandWindow / AppHost)
     ├─ Titlebar + TabRow
     ├─ Pane tree (binary split: horizontal / vertical)
     │    └─ TermControl
     ├─ Command palette / Suggestions
     ├─ Search box (in-control)
     └─ Settings page (optional pane content)
```

`AllShortcutActions.h` is the action checklist (~90 actions). P0 implements
the ones users hit every day; the rest become no-ops that still parse.

### P0 actions

`CopyText`, `PasteText`, `NewTab`, `CloseTab`, `CloseWindow`, `NextTab`,
`PrevTab`, `SwitchToTab`, `DuplicateTab`, `SplitPane`, `ClosePane`,
`MoveFocus`, `ResizePane`, `TogglePaneZoom`, `AdjustFontSize`,
`ResetFontSize`, `ScrollUp`/`Down`/`Page`/`ToTop`/`ToBottom`, `Find`,
`OpenSettings`, `ToggleCommandPalette`, `NewWindow`, `Quit`, `SelectAll`,
`ClearBuffer`, `OpenNewTabDropdown`.

### P1 actions

Tab color/rename, mark mode, find match, export buffer, quake/global summon,
always-on-top, fullscreen/focus mode, broadcast input, suggestions,
color selection, restore last closed, multiple actions, `wt` commandline.

### P2 actions

Quick Fix, workspaces, scratchpad, markdown pane, identify windows,
shader effects, opacity.

Windowing (`WindowEmperor`, remoting):

- P0: one process, many windows (`Ctrl+Shift+N`) is enough
- P1: single-instance + `wt -w` targeting (named pipe / localhost socket,
  not WinRT remoting)
- P2: tab tear-off, quake, tray icon, virtual desktop

## Settings UI

`src/cascadia/TerminalSettingsEditor` is ~150 XAML/IDL/cpp files. Do not
clone that page-for-page on day one.

P0: “Open settings file” (already there) + JSON schema validation errors
in a toast.

P1: Avalonia settings window with the pages people actually use:

- Startup / Launch
- Interaction
- Appearance (global)
- Color schemes
- Profiles (base + appearance)
- Actions (list + key chord capture)

P2: remaining pages (rendering, compatibility, extensions, new tab menu
editor, orphaned profiles).

## CLI (`wt`)

Port `AppCommandlineArgs` onto `System.CommandLine` (AOT-friendly).

P0: `nt` / `new-tab`, `--profile`, `--startingDirectory`, `--`, `sp` /
`split-pane`, `--focus-tab`.

P1: window name (`-w`), `move-focus`, `move-pane`, `save`, startup actions
from settings.

## NativeAOT and packaging

- `PublishAot=true` on `WindowsTerminal` only; tests stay JIT
- `LibraryImport` + `partial` for all kernel32/user32
- Source-generated JSON and regex
- Cascadia fonts as `AvaloniaResource`
- Trimmer roots: `WindowsTerminal`, `Avalonia.Themes.Fluent`, settings
  context
- COM (shell extension, WSL query, VS setup) isolated behind feature
  switches; prefer subprocess/`vswhere` over in-proc COM in P1
- MSIX / sparse package identity is P2 (notifications, default terminal)

## Testing strategy

| Layer | Tests | Source of truth |
| --- | --- | --- |
| Parser | xUnit, byte-level | `ut_parser` cases |
| Buffer | xUnit, reflow/scroll | `UnitTests_TerminalCore` |
| Settings | xUnit, JSON round-trip | `UnitTests_SettingsModel` + real `defaults.json` |
| Actions | xUnit, key chord → action | `defaults.json` actions |
| Control | headless Skia snapshot (later) | golden grids |
| ConPTY | optional Windows-only test | spawn `cmd /c echo` |

CI job (later): `dotnet test` + `dotnet publish -r win-x64` on the `dotnet/`
solution only. Do not gate this port on the C++ OpenConsole build.

## Phased roadmap

### P0 — Daily driver (this is the next implementation slice)

Goal: replace Windows Terminal for local `pwsh`/`cmd`/`wsl` work.

1. **Settings.** Load WT `defaults.json` + user file; inheritance;
   ActionMap; unknown-key preservation.
2. **Actions + default keybindings.** Dispatch table wired to the window.
3. **Panes.** Binary split tree, focus movement, zoom, close.
4. **Search.** Incremental find in the buffer, next/prev.
5. **Command palette.** Action search, fuzzy filter.
6. **Dynamic profiles.** Complete: installed PowerShell, WSL `-l -q`, cmd,
   Windows PowerShell, SSH config hosts, and Visual Studio developer shells.
7. **Buffer/VT bucket A–D** completed and tested.
8. **Scrollbar, copy HTML/RTF optional, confirm close.**
9. **`wt nt` / `wt sp` CLI.**

Exit criteria: open the app, split a pane, bind a custom action in
`settings.json`, search scrollback, WSL profile appears, NativeAOT still
publishes.

### P1 — Real Terminal

Goal: settings UI and rendering that do not feel like a prototype.

1. Skia glyph atlas, ligatures optional, CJK widths, dirty-row paint
2. Hyperlinks, OSC 7, shell-integration marks
3. Settings editor (launch, profiles, schemes, actions)
4. Multi-window + single-instance `wt -w`
5. Selection mark mode, find match, export buffer
6. Themes, acrylic/Mica approximation, tab color
7. win32-input-mode / application keypad
8. Restart connection, elevate, WSL path translation
9. Accessibility: basic UIA via Avalonia AutomationPeer

Exit criteria: a typical `settings.json` from a WT Preview user works
without hand edits; neovim and lazygit look correct.

### P2 — Completeness

1. Sixel and image slices
2. Azure Cloud Shell
3. Extension fragment discovery/merge complete; extension UI remains
4. Quake, tray, global summon
5. Broadcast input, suggestions, Quick Fix
6. Scratchpad / markdown panes
7. Default-terminal handoff
8. MSIX, jumplist, shell extension
9. Workspaces, shader effects

## Suggested project layout (end state)

```
dotnet/
  Terminal.slnx
  src/
    Terminal.Core/          # buffer, VT, input encoding
    Terminal.Render/        # Skia atlas (extracted from Control in P1)
    Terminal.Connection/    # ConPTY, later Azure
    Terminal.Settings/      # CascadiaSettings, ActionMap, generators
    Terminal.Control/       # TermControl, search, scrollbar
    WindowsTerminal/        # app host, tabs, panes, palette, CLI
    WindowsTerminal.Settings/  # P1 settings pages
  tests/
    Terminal.Core.Tests/
    Terminal.Settings.Tests/
    Terminal.Control.Tests/
  PORTING.md
  README.md
```

## Implementation order for the next PRs

Keep PRs stacked and reviewable. Do not mix renderer rewrites with settings
work.

1. `settings-model` — `CascadiaSettings` load/save + `defaults.json` embed
2. `action-map` — parse actions, default keybindings, dispatch
3. `panes` — split tree in the Avalonia window
4. `search-palette` — find + command palette
5. `vt-buffer-p0` — remaining bucket A–D + tests from C++ oracles
6. `dynamic-profiles` — pwsh / WSL / cmd
7. `wt-cli` — `System.CommandLine` front-end
8. `skia-atlas` — renderer replacement (largest PR; isolate it)

Each PR must keep `dotnet test` and Debug `WindowsTerminal` build green.
NativeAOT publish is required on 1, 2, 5, and 8 (AOT-sensitive).

## Risks

- **VT completeness is the long pole.** Under-implementing dispatch is how
  TUIs break. Gate on parser tests, not screenshots.
- **Reflow and wide glyphs.** Easy to get wrong; steal cases from
  `ReflowTests.cpp`.
- **COM + NativeAOT.** VS/WSL detection must not use runtime-callable
  wrappers. Prefer CLI (`wsl.exe -l -q`, `vswhere`).
- **settings.json comments.** WT allows comments; STJ needs
  `ReadCommentHandling.Skip` on a serializer that still AOT-compiles.
- **Default terminal / handoff.** Requires packaged identity. Do not block
  P0/P1 on it.
- **Performance.** `FormattedText` per run is fine for P0. Atlas-level
  throughput needs the Skia atlas before people live in the app.

## References in this repo

- Organization: `doc/ORGANIZATION.md`
- Settings schema: `doc/cascadia/SettingsSchema.md`,
  `src/cascadia/TerminalSettingsModel/defaults.json`
- Actions: `src/cascadia/TerminalSettingsModel/AllShortcutActions.h`
- VT surface: `src/terminal/adapter/ITermDispatch.hpp`
- Atlas: `src/renderer/atlas/README.md`
- Process model: `doc/specs/#5000 - Process Model 2.0/`
- ConPTY sample we followed: `samples/ConPTY/MiniTerm`
