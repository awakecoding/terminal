# Post-port parity status

This document is the authoritative measured parity record for the .NET 10,
NativeAOT, and Avalonia port. `PORTING.md` records the historical roadmap;
this file records measured behavior after the Windows, Ghostty, and Linux
milestones.

Implementation, unit, headless Avalonia, protocol, process, package-structure,
and NativeAOT gates completed before the final live UI matrix.

## Completed platform baseline

| Surface | Status |
| --- | --- |
| Windows process transport | ConPTY with input, resize, cancellation, restart, exit metadata, and x64/ARM64 NativeAOT |
| Linux process transport | Bundled `forkpty` relay with input, resize, cancellation, restart, exit metadata, and x64/ARM64 NativeAOT |
| Terminal engines | Selectable built-in and pinned Ghostty engines on Windows and Linux |
| Application shell | Multi-window broker, tabs, panes, settings editor, palettes, tray behavior, accessibility, clipboard, and notifications |
| Settings | Layering, dynamic profiles, fragments, source-generated JSON, Windows paths, XDG paths, and state persistence |
| Distribution | Windows x64/ARM64 MSIX and bundle; reproducible Linux x64/ARM64 tar, DEB, RPM, and AppImage packages with canonical freedesktop assets, licenses, checksums, inventory/SPDX SBOM, and DESTDIR-aware helpers |

## Actions

The settings catalog contains 92 valid actions. `MainWindow` directly
registers 91 and `MultipleActions` is implemented by `ActionDispatcher`.
Global summon resolves broker-visible names case-insensitively, creates a named
window when necessary, applies deterministic show/hide/focus behavior, and
places quake windows across the selected monitor's top half. Dropdown duration
is bounded to two seconds.

`ToggleShaderEffects` toggles the active terminal's bounded, deterministic
Skia retro/scanline pass when `experimental.retroTerminalEffect` is enabled.
Arbitrary `experimental.pixelShaderPath` HLSL remains unsupported and is not
advertised as active.

`Suggestions` and `QuickFix` are registered but depend on data supplied by a
shell provider. Their no-data result is explicit. Cross-window live-PTY transfer
remains unavailable because public ConPTY and Linux PTY APIs do not provide a
portable transactional handle-transfer contract.

## Layouts and workspaces

The app captures and restores versioned window, tab, pane, profile,
working-directory, title, color, geometry, launch-mode, active-pane, and zoom
descriptors. `wt save`, `--saved`, `OpenWorkspace`, and `Workspaces` route
through the authenticated broker and atomic state store. Restoration always
creates new platform connections; live PTY handles are never serialized.
Native Windows Terminal action-array layouts are rejected with a non-destructive
diagnostic because they are not the same persistence contract.

## Platform integration

| Capability | Windows | Linux | Remaining acceptance |
| --- | --- | --- | --- |
| Open URI/file | Registered shell with argument-safe process launch | Portal preferred, `xdg-open` fallback, bounded execution, explicit errors | Complete for supported providers |
| Open current directory | Registered shell | Same portal/fallback service with directory validation | Complete for supported providers |
| Tray | Implemented | Implemented where the Avalonia desktop backend supports it | Capability report records that freedesktop has no reliable tray probe |
| Global summon/quake | Collision-safe `RegisterHotKey`, broker routing, mouse/current monitor placement, bounded dropdown, settings re-registration | Broker/manual summon works; cross-desktop global shortcut registration is explicitly unsupported because no reflection-free interactive GlobalShortcuts portal session provider is bundled | Windows and Linux quake/summon placement and visibility were verified live; virtual-desktop movement remains best-effort |
| Default terminal | Versioned native boundary; explicitly unavailable because OpenConsole handoff v3 proxy/stub and host are not bundled | Explicit, reversible `xdg-terminal-exec` preference and Debian alternatives helper | Windows package registration must wait for the real handoff implementation; other Linux distro registries are unsupported |
| Explorer integration | Packaged x64/ARM64 `IExplorerCommand` server for directory and directory-background verbs | N/A | Clean-machine package interaction remains a protected signing/release gate |
| Jump lists | Native helper generates visible profile tasks and refreshes after startup/settings saves | N/A | Clean-machine pinned-task verification remains a protected signing/release gate |
| System notifications | In-app plus packaged system toast; supported unpackaged AUMID/shortcut identity; validated broker activation | Accessible in-app notification plus portal-preferred/`notify-send` fallback | Linux notification actions remain unsupported |
| Package protocol | Manifest registration plus safe basic Windows activation normalization | `.desktop` handler plus safe basic activation | Command-bearing payloads remain intentionally rejected |
| Desktop metadata | N/A | Validated `.desktop`, actions, AppStream, six hicolor sizes, URI metadata, canonical tar/DEB/RPM/AppImage staging, registration, upgrade, and uninstall | Portal routing was verified; the WSLg image had no visible file-manager provider |

Native Windows integrations preserve `BuiltInComInteropSupport=false` in the
managed NativeAOT host. COM/default-terminal functionality belongs in
architecture-matched native helpers with versioned, authenticated boundaries.
The package gate validates helper PE architecture, SHA-256 manifests, Explorer
registrations, and notices for both x64 and ARM64.

## Terminal protocol and rendering

| Area | Status | Remaining acceptance |
| --- | --- | --- |
| Search | Implemented across both engines | Add canonical-equivalence/ZWJ cases and preserve selected matches across reflow/eviction |
| Grapheme/emoji | Built-in supports Hangul, Indic conjuncts, prepend, spacing marks, RI pairs, emoji modifiers/selectors, and emoji ZWJ sequences at arbitrary UTF-8 feed boundaries; bundled Noto Color Emoji provides deterministic Linux fallback | Full Unicode GraphemeBreakTest conformance remains; the pinned Ghostty render ABI does not expose equivalent cluster geometry |
| Row rendition | Built-in DECDWL/DECDHL parser, snapshots, logical cursor clipping, reflow preservation, and render transforms implemented | The pinned Ghostty C ABI does not expose row rendition; capability is explicitly unavailable there |
| Sixel | Built-in decode/render, DECSDM scrolling/display behavior, retained cell geometry, and stable ownership implemented | The pinned Ghostty C ABI exposes no image resources and reports the capability unavailable |
| OSC 1337 | Built-in bounded inline decode/render and stable ownership implemented; non-inline transfer is explicitly rejected without I/O | The pinned Ghostty C ABI exposes no image resources and reports the capability unavailable |
| ConEmu images | Bounded single-part `st=0;sz=` payloads decode into shared overlay metadata and render safely | Multipart payloads are explicitly rejected; the pinned Ghostty C ABI exposes no image resources |
| Image ownership | Stable logical-line anchors survive scrollback and reflow in main and alternate buffers and are removed on owning-line eviction | Ghostty image projection is unavailable in the pinned C ABI and produces deterministic unsupported diagnostics |
| VT52 | Output plus host cursor/PF/application-keypad encoding implemented, with built-in/Ghostty differential mode coverage | No remaining shared subset work |
| DRCS | Built-in parse/resource mapping, bounded snapshot masks, render planning, and downloaded-pixel rendering implemented | The pinned Ghostty C ABI does not expose DRCS resources; capability is explicitly unavailable there |
| Extended keyboard | Built-in Kitty set/query/push/pop flags, CSI-u event bytes, `modifyOtherKeys`, Win32-input mode, and press/repeat/release encoding implemented | Kitty alternate-key reporting and associated-text reporting are not advertised; the pinned Ghostty C ABI exposes no keyboard protocol state and reports these capabilities unavailable |
| Shader effects | Optional deterministic, bounded Skia retro/scanline pass, toggleable per active terminal | Custom arbitrary HLSL/pixel-shader files are not loaded or advertised |

## Distribution and validation

The `linux-arm64-hardware` CI job runs on GitHub's native
`ubuntu-24.04-arm` hosted runner. It rejects any machine whose `uname -m` is
not `aarch64` or `arm64`; emulation is not an accepted substitute. The job
downloads the cross-built ARM64 tar, DEB, RPM, and AppImage artifacts and runs
`scripts/Test-LinuxArm64Runtime.sh`. The gate executes the NativeAOT `wt`
help/error paths from every extracted format, Ghostty ABI and engine feeds, the
built-in parser/engine, real `forkpty` input/resize/exit/cancellation/restart,
broker concurrency, Linux shell profile and XDG paths, and disposable-root
package install/upgrade/uninstall.

The AppImage ELF architecture and SquashFS payload are validated, and its
embedded ARM64 `wt` is executed after `unsquashfs`. The AppImage `AppRun`
launches the GUI host, so it is intentionally not executed by this non-UI,
display-free gate; no FUSE mount is used.

Protected production release work is limited to signing and clean-machine
install/upgrade/uninstall verification for Windows x64/ARM64 packages.

## Final UI results

Windows x64 and packaged Linux x64 were validated with built-in and Ghostty
engines after the full non-UI matrix. The runs covered three independent tabs,
split panes, focus/resize/close isolation, clipboard, fullscreen,
maximize/restore, settings, workspace recreation, marks, summon/quake,
truecolor, Unicode/CJK/emoji, row rendition, images, and shader effects.

- PowerShell 7 loaded PSReadLine normally. Windows PowerShell 5.1 on the test
  machine rejected its PSReadLine script module because of the machine's
  execution policy; ConPTY handles were confirmed as non-redirected.
- Built-in Linux rendered Sixel and inline OSC 1337 images. Ghostty displayed
  explicit limitation notifications for image protocols and row rendition that
  its pinned C ABI does not expose.
- Bundled Noto Color Emoji fixed Linux emoji-ZWJ tofu. Portable settings icons
  and narrower content padding removed Linux settings-page clipping.
- Mark actions display notifications and colored scrollbar ticks.
- Linux Open CWD selected the portal/`xdg-open` path; the WSLg image did not
  provide a visible file-manager handler.
- Native Windows ARM64 and Linux ARM64 remain non-UI hardware/package gates;
  no ARM64 desktop was available in this session.
