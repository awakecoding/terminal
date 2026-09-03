# Complete .NET 10 / NativeAOT / Avalonia port

This is the historical plan for turning the prototype into **Devolutions
Terminal**, a behavior- and settings-compatible Windows Terminal host on
.NET 10, NativeAOT, and Avalonia instead of C++/WinRT/WinUI.

It is **not** a line-for-line transcription of CppWinRT, MIDL, or XAML Islands.
Those types do not exist in this stack. The bar is:

1. A user can point the app at a real `settings.json` and get the same profiles,
   schemes, keybindings, and startup behavior for the supported subset.
2. Modern shells, editors, and TUIs (`pwsh`, `cmd`, WSL, neovim, lazygit) work.
3. NativeAOT publish of a single `Devolutions.Terminal.exe` stays green.

The original C++ Windows Terminal tree is the compatibility oracle. This
project is intended to become the root of
https://github.com/Devolutions/devolutions-terminal.

## Current baseline (done)

| Project | What it covers today |
| --- | --- |
| `Devolutions.Terminal.Core` | Circular scrollback/reflow, grapheme-aware cells, shell marks/search/export, complete daily-driver VT plus bounded DCS/Sixel/DRCS/macros/VT52/rectangular operations |
| `Devolutions.Terminal.Render` | Immutable plans, HarfBuzz shaping, fallback fonts, bounded Skia text-blob and image caching, dirty-row contracts |
| `Devolutions.Terminal.Connection` | Restartable NativeAOT-safe ConPTY, Linux PTY, and Azure Cloud Shell HTTP/WebSocket connections |
| `Devolutions.Terminal.Settings` | Complete MTSM projection, tri-state inheritance, migrations, fragments, profile generators, state persistence, plus the 92-action inventory, typed action arguments, normalized key chords, current/legacy binding parsing, and source-generated NativeAOT-safe JSON |
| `Devolutions.Terminal.Control` | Avalonia `TermControl`: Skia rendering, search, shell-region selection, clipboard/paste policy, mouse/touch/IME, accessibility, and event-driven output draining |
| `Devolutions.Terminal.App` | Persistent tabs/panes, all-action dispatch, action/command-line/history/tab palettes, settings UI, Azure auth UI, scratchpad, and notifications |
| `Devolutions.Terminal` | NativeAOT executable, authenticated single-instance broker, multi-window routing, and notification-area integration |

The baseline also includes dedicated settings, connection, control, app,
compatibility, and UI test projects; x64/ARM64 NativeAOT CI; architecture
decisions; and a generated compatibility inventory covering 120 settings keys,
92 actions, 123 VT dispatch methods, 14 CLI commands, and 25 settings pages.

The practical daily-driver surface is implemented. Inventory actions that need
an upstream shell-completion/Quick Fix provider or a Windows-only native
integration return an explicit unavailable result or notification rather than
silently doing the wrong thing.

## Non-goals

Leave these in the C++ tree. Do not port them as part of this app:

- `conhost.exe` / console driver / server IOCTL protocol
- The inbox console property sheet (`propsheet`, `propslib`)
- WinUI, XAML Islands, CppWinRT, MIDL
- Store submission / OneBranch / PGO of the C++ build
- Pixel-identical AtlasEngine D3D shaders (match visually, not the HLSL)

Platform boundaries that remain intentionally external to the C# application:

- Cross-process visual tab tear-off has versioned broker contracts but cannot
  transfer a live ConPTY/HPCON through the public API.
- The Windows 11 Explorer verb requires an architecture-matched native
  `IExplorerCommand` COM DLL loaded by the shell.
- Default-terminal delegation requires Windows console registration and handoff
  contracts that are not exposed as a managed app-only API.
- System toast activation and jump lists require a WinRT/Windows App SDK
  projection. In-app accessible notifications and notification-area behavior
  remain available without those dependencies.

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
buffer/out + TerminalCore           Devolutions.Terminal.Core (buffer, engine)
terminal/parser + adapter + input   Devolutions.Terminal.Core.Vt (parser, dispatch, input)
renderer/atlas + base               Devolutions.Terminal.Render (Skia atlas)
cascadia/TerminalConnection         Devolutions.Terminal.Connection
cascadia/TerminalSettingsModel      Devolutions.Terminal.Settings
cascadia/TerminalControl            Devolutions.Terminal.Control (TermControl + search)
cascadia/TerminalApp                Devolutions.Terminal.App (tabs, panes, actions)
cascadia/Devolutions.Terminal            Devolutions.Terminal (windowing host)
cascadia/TerminalSettingsEditor     Devolutions.Terminal.Settings.Editor (Avalonia pages)
```

Layer rules:

- **Core has no Avalonia, no Win32 UI.** Parser and buffer must be testable
  on any `net10.0` RID.
- **Connection selects an OS transport.** Windows uses ConPTY; Linux uses the
  bundled `forkpty` relay. Keep each implementation behind its corresponding
  supported-platform annotation.
- **Control talks to Core + Connection.** It owns rendering, input, selection,
  search box, scrollbar.
- **App owns ActionMap, panes, tabs, command palette, CLI.**
- **NativeAOT:** `LibraryImport` not `DllImport`; source-generated JSON;
  no runtime XAML loading; no reflection DI; `IsAotCompatible=true` on
  production projects.
- **Windows shell:** the managed host keeps
  `BuiltInComInteropSupport=false`. Architecture-matched native package helpers
  own Explorer COM, jump lists, toasts, and the explicit unavailable
  OpenConsole handoff boundary.

## Linux

Linux uses the same Avalonia/Skia UI and built-in or Ghostty terminal engines as
Windows. Local processes are hosted by `dt-pty-host`, a small bundled `forkpty`
relay that preserves interactive shell, resize, signal, and process-group
semantics. Dynamic profiles discover `$SHELL`, bash, zsh, fish, PowerShell, and
`sh`; settings and state follow XDG directory conventions.

Build the deterministic NativeAOT package set on Linux:

```bash
dotnet/scripts/Build-LinuxPackage.sh linux-x64 0.1.0 artifacts/packages all
dotnet/scripts/Build-LinuxPackage.sh linux-arm64 0.1.0 artifacts/packages all
```

ARM64 cross-publish from x64 requires the GNU AArch64 linker/binutils packages.
The resulting tar, DEB, RPM, and AppImage preserve executable permissions for
`Devolutions.Terminal`, `wt`, and `dt-pty-host`. All four consume one canonical
install root containing validated freedesktop metadata, hicolor icons,
architecture-correct native libraries, licenses, deterministic inventory/SPDX
SBOM, and a DESTDIR-aware install/uninstall helper.
Linux opens and notifications use bounded, argument-list-only subprocesses:
the freedesktop portal is preferred, with `xdg-open` and `notify-send`
fallbacks. Protocol and default-terminal selection are separate explicit,
reversible helper actions; installation never overwrites those user choices.

Do not take a C++/CLI or C++/WinRT interop dependency on `Devolutions.Terminal.*`
DLLs. That reintroduces the runtime we are leaving.

## Renderer strategy

AtlasEngine (`src/renderer/atlas`) is a DWrite + D2D/D3D glyph atlas. Avalonia
already uses Skia. Porting Atlas 1:1 would mean a second graphics stack and
break NativeAOT simplicity.

**Decision: C# Skia glyph atlas inside `Devolutions.Terminal.Control` / `Devolutions.Terminal.Render`.**

Match Atlas *behavior*:

- Cell grid with wide glyphs, zero-width marks, and built-in row rendition
- Dirty-row invalidation, not full-frame redraw
- Font fallback (Cascadia Mono → Consolas → bundled Noto Color Emoji → Segoe UI Emoji)
- Bold/italic as real faces when present, synthetic otherwise
- Cursor styles: bar, vintage, underscore, filled/empty box
- Reverse video, underline, strikethrough
- Selection overlay and hyperlink underline
- Background images and platform-appropriate acrylic/Mica approximations

P1 now uses a retained Skia custom draw operation, HarfBuzz-shaped cell
clusters, bounded text-blob/typeface caches, dirty-row contracts, and image
overlays. Stable image ownership across reflow and built-in
double-width/double-height line rendition are implemented.

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

Actions and keybindings retain their lossless raw JSON and also project into
`ActionMap`/`ActionAndArgs` for typed lookup and dispatch.

Typed JSON is source-generated (`System.Text.Json` + `JsonSerializerContext`).
Settings layering and unknown-property preservation operate on `JsonNode`, so
NativeAOT round-trips comments-stripped WT JSON without reflection.

Default actions live in a C# copy of the `actions` block from
`src/cascadia/TerminalSettingsModel/defaults.json`. Keep the JSON in sync;
do not hand-code key chords in the window.

## VT / buffer completeness

`ITermDispatch` is ~160 methods. The port implements the daily-driver and
advanced bounded subsets; engine-specific limits are recorded in
`docs/parity-status.md`.

| Bucket | Sequences / features | Phase |
| --- | --- | --- |
| A. Editing | ICH/DCH/IL/DL/ECH, DECSTBM, SU/SD, DECSC/DECRC | P0 slice complete |
| B. Modes | DECCKM, DECAWM, DECTCEM, alt buffer, bracketed paste, mouse SGR | P0 slice complete |
| C. Reports | DA1/DA2, DSR CPR, DECRQM | P0 slice complete |
| D. Color | SGR 38/48 2/5, OSC 4/10/11/12, indexed/RGB | P0 slice complete |
| E. Unicode | UTF-8, wcwidth, emoji ZWJ, reflow on resize | Wide/combining/ZWJ behavior is substantial; complete UAX #29 coverage remains |
| F. Shell integration | OSC 7, OSC 133 marks, OSC 8 hyperlinks | Implemented |
| G. Input | Application keypad, win32-input-mode, Kitty protocol | Implemented in the built-in engine; pinned Ghostty ABI limitations are explicit |
| H. Images | Sixel, OSC 1337, ConEmu | Built-in bounded decode/render, DECSDM behavior, and stable ownership complete; pinned Ghostty image projection is explicitly unavailable |
| I. Rare VT | Rectangular ops, DECDLD, macros, DECRQSS, VT52 | Built-in core, DRCS rendering, and VT52 input implemented |

The .NET buffer now uses bounded circular scrollback, keeps independent
main/alternate state (cursor, margins, attributes, tab stops), reflows logical
wrapped lines on resize, repairs wide-cell boundaries, retains combining marks
and hyperlink metadata, and exposes detached read-only snapshots. Existing
`GetRow`, cursor, selection, and scroll-offset APIs remain compatible with
`Devolutions.Terminal.Control`.

Buffer work beyond the current parity slice:

- Complete UAX #29 grapheme and emoji ZWJ coverage
- Ghostty image projection if a future pinned C ABI exposes image resources

Bounded image payloads, stable image ownership, row rendition, DCS payload
dispatch, rectangular operations, downloadable resources and rendering,
macros, VT52 output/input, extended keyboard input, Sixel,
DECRQSS/XTGETTCAP, and OSC 52/133/1337 core behavior are implemented.

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
- Restart an active connection without replacing its pane/control

P1:

- WSL path translation
- `elevate` via a small helper (not the C++ shim at first)
- Inbound ConPTY listener / default-terminal handoff
- Working directory from OSC 7

P2:

- Azure Cloud Shell (`AzureConnection`)
- Process handoff from `wt` / `OpenConsole`

## Application shell

Reimplement `TerminalApp` + `Devolutions.Terminal` EXE as Avalonia, following the
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

`AllShortcutActions.h` is the action checklist (92 actions). P0 implements
the ones users hit every day; the rest still parse and return explicit
unsupported dispatch results.

### P0 actions

`CopyText`, `PasteText`, `NewTab`, `CloseTab`, `CloseWindow`, `NextTab`,
`PrevTab`, `SwitchToTab`, `DuplicateTab`, `SplitPane`, `ClosePane`,
`MoveFocus`, `ResizePane`, `TogglePaneZoom`, `AdjustFontSize`,
`ResetFontSize`, `ScrollUp`/`Down`/`Page`/`ToTop`/`ToBottom`, `Find`,
`OpenSettings`, `ToggleCommandPalette`, `NewWindow`, `Quit`, `SelectAll`,
`ClearBuffer`, `SendInput`, `MovePane`, `CloseOtherPanes`,
`ToggleSplitOrientation`, `RestartConnection`, `FindMatch`,
`ToggleAlwaysOnTop`, `ToggleFullscreen`, `ToggleFocusMode`, and
`MultipleActions`.

### P1 actions

Tab color/rename, mark mode, full-scrollback search, export buffer,
quake/global summon, broadcast input, suggestions, color selection,
restore last closed, and `wt` commandline.

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

Rendering, compatibility, extensions, new-tab-menu, and advanced profile pages
are now implemented in the compiled-XAML editor.

## CLI (`wt`)

Port `AppCommandlineArgs` onto `System.CommandLine` (AOT-friendly).

P0: `nt` / `new-tab`, `--profile`, `--startingDirectory`, `--`, `sp` /
`split-pane`, `--focus-tab`.

P1: window name (`-w`), `move-focus`, `move-pane`, `save`, startup actions
from settings.

## NativeAOT and packaging

- `PublishAot=true` on `Devolutions.Terminal` only; tests stay JIT
- `LibraryImport` + `partial` for all kernel32/user32
- Source-generated JSON and regex
- Cascadia fonts as `AvaloniaResource`
- Trimmer roots: `Devolutions.Terminal`, `Avalonia.Themes.Fluent`, settings
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
| Control | headless Avalonia/Skia tests | golden grids and interaction contracts |
| ConPTY | optional Windows-only test | spawn `cmd /c echo` |

The dedicated workflow builds/tests the .NET solution, publishes Windows and
Linux x64/ARM64 NativeAOT, validates MSIX plus tar/DEB/RPM/AppImage artifacts,
and runs native Linux ARM64 non-UI gates.

## Phased roadmap

### P0 — Daily driver

Goal: replace Windows Terminal for local `pwsh`/`cmd`/`wsl` work.

1. **Settings.** Load WT `defaults.json` + user file; inheritance;
   ActionMap; unknown-key preservation. **Implemented.**
2. **Actions + default keybindings.** Complete catalog dispatch is wired to the
   window or an explicit platform capability.
3. **Panes.** Binary split tree, focus movement, zoom, close. **Implemented.**
4. **Search.** Find in the visible buffer, next/prev. **Implemented; full
   scrollback Unicode search controller is ready for overlay integration.**
5. **Command palette.** Action search, fuzzy ranking, and dispatch.
   **Implemented.**
6. **Dynamic profiles.** Installed PowerShell, WSL, cmd, Windows PowerShell,
   SSH hosts, and Visual Studio developer shells. **Implemented.**
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

1. Sixel, OSC 1337, and bounded ConEmu image decoding/rendering complete
2. Azure Cloud Shell
3. Extension fragment discovery/merge complete; extension UI remains
4. Notification-area behavior and Windows global summon/quake hotkeys complete;
   Linux manual/broker summon is available with explicit portal limitations.
5. Broadcast input and command-history suggestions complete; provider-backed
   shell completion and Quick Fix report unavailable when the shell supplies no data.
6. Scratchpad window complete; markdown pane content remains outside the
   terminal-pane persistence contract.
7. Default-terminal handoff is a documented OS/native boundary.
8. x64/ARM64 MSIX and bundle include jump-list, toast, and Explorer helpers.
9. Versioned workspaces and bounded Skia retro effects are implemented.

## Suggested project layout (end state)

```
dotnet/
  Devolutions.Terminal.slnx
  src/
    Devolutions.Terminal.Core/          # buffer, VT, input encoding
    Devolutions.Terminal.Render/        # HarfBuzz shaping, Skia text-blob cache, render plans
    Devolutions.Terminal.Connection/    # ConPTY, Linux PTY, Azure Cloud Shell
    Devolutions.Terminal.Settings/      # CascadiaSettings, ActionMap, generators
    Devolutions.Terminal.Control/       # TermControl, search, scrollbar
    Devolutions.Terminal/        # app host, tabs, panes, palette, CLI
    Devolutions.Terminal.Settings.Editor/  # P1 settings pages
  tests/
    Devolutions.Terminal.Core.Tests/
    Devolutions.Terminal.Settings.Tests/
    Devolutions.Terminal.Control.Tests/
  PORTING.md
  README.md
```

## Completed integration stack

The settings model, ActionMap, panes/tabs, search/palette, VT/buffer,
dynamic-profile, CLI/broker, Skia renderer, accessibility, Azure, and MSIX
layers are integrated on the consolidated branch. Future changes must keep the
full Release suite, x64/ARM64 NativeAOT publish, broker smoke, and package
validation green.

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
- **Performance.** The Skia/HarfBuzz cache and dirty-row contracts are in place.
  Output draining is event-driven rather than a 125 Hz idle poll. Full-history
  search and viewport snapshot reuse remain the next profiling-led optimization
  opportunities.

## References in this repo

- Organization: `doc/ORGANIZATION.md`
- Settings schema: `doc/cascadia/SettingsSchema.md`,
  `src/cascadia/TerminalSettingsModel/defaults.json`
- Actions: `src/cascadia/TerminalSettingsModel/AllShortcutActions.h`
- VT surface: `src/terminal/adapter/ITermDispatch.hpp`
- Atlas: `src/renderer/atlas/README.md`
- Process model: `doc/specs/#5000 - Process Model 2.0/`
- ConPTY sample we followed: `samples/ConPTY/MiniTerm`
