# Build, migration, and release guide

## Prerequisites

- Windows 10 version 2004 (build 19041) or later for Windows builds
- glibc-based x64 or ARM64 Linux for Linux builds
- .NET SDK selected by `dotnet/global.json`
- Visual Studio 2022/2026 Build Tools with Desktop C++ for NativeAOT linking
- WinApp CLI 0.6.0 for MSIX creation and validation
- A trusted code-signing certificate for distributable MSIX artifacts

Windows local sessions use ConPTY. Linux local sessions use the bundled
`forkpty` relay. The Avalonia shell, settings, renderer, and terminal engines
are shared.

## Developer build

```powershell
cd dotnet
dotnet restore Terminal.slnx
dotnet build Terminal.slnx -c Debug
dotnet test Terminal.slnx -c Release
dotnet run --project src/WindowsTerminal
```

Warnings are errors for production projects. The trim and NativeAOT analyzers
run continuously rather than only during release packaging.

## NativeAOT

```powershell
dotnet publish src/WindowsTerminal -c Release -r win-x64 --self-contained `
  -o artifacts/native/win-x64
dotnet publish src/WindowsTerminal -c Release -r win-arm64 --self-contained `
  -o artifacts/native/win-arm64
```

Each publish directory contains:

- `WindowsTerminal.exe` — GUI host and broker primary
- `wt.exe` — console command-line/broker client
- Avalonia/Skia native dependencies
- bundled Noto Color Emoji fallback and its SIL OFL notice

MSIX staging additionally builds `wt-shell-integration.exe` and
`WindowsTerminalShellExt.dll` for the package architecture with the installed
MSVC/Windows SDK toolchain.

Linux package formats:

```bash
dotnet/scripts/Build-LinuxPackage.sh linux-x64 0.1.0 artifacts/packages all
dotnet/scripts/Build-LinuxPackage.sh linux-arm64 0.1.0 artifacts/packages all
```

These commands publish NativeAOT executables and create reproducible tar, DEB,
RPM, and AppImage artifacts plus a SHA-256 manifest. Every format is staged from
one canonical filesystem root and includes `WindowsTerminal`, `wt`,
`wt-pty-host`, Ghostty, Skia, HarfBuzz, licenses, freedesktop metadata, icons,
the reversible integration helper, a sorted inventory, and an SPDX 2.3 SBOM.
Set `SOURCE_DATE_EPOCH` for release reproducibility. To reuse a separately
validated publish, set `LINUX_PUBLISH_DIR`.

`ar`, GNU tar, `readelf`, `file`, `sha256sum`, and Python 3 are required.
RPM builds additionally require `rpmbuild`; AppImage builds require
`mksquashfs` and a pinned, architecture-matched type-2 runtime in
`APPIMAGE_RUNTIME_FILE`; the builder never downloads a tool or runtime.
Validation of RPM and AppImage extraction requires `rpm2cpio`/`cpio` and
`unsquashfs`. The scripts fail before packaging with distribution-specific
installation guidance when a tool is absent.

Validate Linux desktop assets without launching the application:

```bash
dotnet/scripts/Test-LinuxDesktopIntegration.sh
bash dotnet/scripts/Test-LinuxPackagingMetadata.sh
bash dotnet/scripts/Test-LinuxPackage.sh linux-x64 artifacts/packages/*-linux-x64.*
bash dotnet/scripts/Test-LinuxArm64Runtime.sh artifacts/packages
```

The test stages an install under `dotnet/artifacts`, checks desktop/AppStream
metadata and all icon sizes, exercises a custom installed executable path,
simulates reversible protocol and `xdg-terminal-exec` registration, and verifies
uninstall removes every owned file. When installed, `desktop-file-validate` and
`appstreamcli` provide additional schema validation.

`Test-LinuxArm64Runtime.sh` must run on native Linux ARM64 (`uname -m` equal to
`aarch64` or `arm64`) and deliberately rejects x64 and QEMU-based
cross-execution. It runs only non-UI gates: NativeAOT `wt` startup/parser,
Ghostty ABI and feed, the built-in engine, real `forkpty` lifecycle, broker
concurrency, Linux profile/XDG discovery, and package lifecycle under a
disposable root. CI uses the exact GitHub-hosted runner label
`ubuntu-24.04-arm` in job `linux-arm64-hardware`. If that label is unavailable
to a fork or plan, configure a native Linux ARM64 self-hosted runner and change
only `runs-on`; do not fall back to emulation because the script's architecture
guard is part of the release gate.

AppImage validation checks its ARM64 runtime, extracts its SquashFS payload
without mounting/FUSE, and executes the embedded `wt`. `AppRun` starts the GUI
host and is intentionally not executed in display-free CI; live AppImage UI
startup remains deferred to the final UI gate.

For package staging, the helper never updates host caches or configuration:

```bash
DESTDIR="$PWD/package-root" \
  dotnet/linux/Install-LinuxDesktopIntegration.sh install --prefix /usr
dotnet/linux/Install-LinuxDesktopIntegration.sh uninstall \
  --destdir "$PWD/package-root" --prefix /usr
```

Smoke the x64 output:

```powershell
.\scripts\Test-PublishedApp.ps1 `
  -Executable .\artifacts\native\win-x64\WindowsTerminal.exe
```

## Settings migration

The port accepts Windows Terminal's modern and legacy settings shapes,
including comments and trailing commas. It resolves:

1. Embedded defaults
2. Generated PowerShell, cmd, WSL, SSH, and Visual Studio profiles
3. Extension fragments
4. User settings

Set `WT_DOTNET_SETTINGS_PATH` to test an existing file without replacing it:

```powershell
$env:WT_DOTNET_SETTINGS_PATH = "$env:LOCALAPPDATA\Packages\Microsoft.WindowsTerminal_8wekyb3d8bbwe\LocalState\settings.json"
dotnet run --project src/WindowsTerminal
```

Use a copy when evaluating editor saves. The editor canonicalizes comments and
whitespace while retaining unknown/local-layer data. Runtime state is stored in
`state.json` beside the selected settings file.

## MSIX

Create unsigned x64/ARM64 packages and a bundle:

```powershell
.\src\WindowsTerminal.Package\Scripts\Build-Packages.ps1 -Version 0.1.0.0
$packages = Get-ChildItem .\artifacts\msix\packages\*.msix,
  .\artifacts\msix\packages\*.msixbundle
.\src\WindowsTerminal.Package\Scripts\Test-Packages.ps1 `
  -PackagePath $packages.FullName
```

Development signing:

```powershell
$password = Read-Host "Certificate password" -AsSecureString
.\src\WindowsTerminal.Package\Scripts\New-DevelopmentCertificate.ps1 `
  -OutputDirectory .\artifacts\msix\certificates -Password $password
.\src\WindowsTerminal.Package\Scripts\Sign-Packages.ps1 `
  -PackageDirectory .\artifacts\msix\packages `
  -CertificatePath .\artifacts\msix\certificates\Awakecoding.WindowsTerminal.Dev.pfx `
  -Password $password -Version 0.1.0.0
```

Never commit PFX files, passwords, certificate private keys, or signed internal
artifacts. CI produces unsigned packages unless a protected release environment
injects signing credentials.

## Release gates

1. Regenerate `compat/windows-terminal.json` and review inventory changes.
2. Run the full Release solution tests with no failures or unconditional skips.
3. Publish and launch-smoke NativeAOT x64.
4. Cross-publish NativeAOT ARM64.
5. Build and structurally validate both MSIX packages and the bundle, including
   shell-helper PE architecture, SHA-256 manifests, COM/Explorer extensions,
   and notices.
6. Sign and verify package publisher/identity/version in a protected environment.
7. Install, launch `WindowsTerminal.exe`, invoke `wt.exe`, upgrade, and uninstall
   on clean x64 and ARM64 Windows VMs.
8. Run multi-tab/pane, settings, CLI forwarding, UIA, Unicode/CJK/emoji, VT,
   ConPTY cancellation, and long-output stress workflows.
9. Compare startup time, working set, renderer allocations, and artifact size
   with the previous candidate.
10. Review diagnostics and documentation for intentional platform differences.
11. Build all four Linux formats for x64 and ARM64, run
    `scripts/Test-LinuxPackage.sh` over each artifact, compare a repeated x64
    build's SHA-256 manifest, and run `scripts/Test-LinuxDesktopIntegration.sh`.
    The validator checks every ELF architecture/dependency/debug section,
    metadata, paths, modes, licenses, inventory/SBOM hashes, desktop/protocol
    declarations, and idempotent install/uninstall behavior without launching
    the UI. The `linux-arm64-hardware` job must then pass on the native
    `ubuntu-24.04-arm` runner before release.
12. Verify Windows global-hotkey collision diagnostics, settings
    re-registration, named broker summon, current/mouse monitor placement,
    quake sizing, dropdown completion, and the native system menu. These are
    protected live-UI checks; unit/headless validation does not register a
    desktop shortcut or display a window.

## Platform constraints

- Public out-of-process ConPTY can filter or alter DCS/APC payloads on some
  Windows builds. Sixel works when the selected connection passes DCS bytes
  through unchanged.
- Avalonia 12 exposes the terminal as a readable UIA Document/Value provider,
  but does not provide a public bridge for native UIA TextPattern/TextPattern2
  or LiveSetting events. Managed ranges and visible notification text remain
  available.
- Azure Cloud Shell requires a public-client application ID and host-provided
  device-code/tenant UI. No client secret is embedded.
- The development package identity and `wt.exe` alias can conflict with an
  installed Microsoft Windows Terminal alias; Windows alias settings choose the
  active provider.
- The notification-area icon and minimize-to-area behavior work packaged and
  unpackaged. MSIX builds include architecture-matched Explorer, jump-list, and
  toast helpers. Packaged operations use
  `<package-family-name>!Terminal`. Unpackaged system toasts are supported only
  when `WT_DOTNET_AUMID` names an AUMID registered by a Start-menu shortcut and
  `WT_DOTNET_TOAST_SHORTCUT` points to that `.lnk`; the shortcut and
  COM/sparse-package registration must also name toast activator
  `a3aeb121-45d9-4cd9-a278-4b43d19b95b1`. Otherwise the diagnostic is explicit.
  Default-terminal registration still requires the unbundled
  OpenConsole handoff v3 proxy/stub and host, so only its versioned diagnostic
  boundary exists and no incomplete manifest extension is registered.
- System-toast activation payloads contain no secrets and accept only protocol
  version 1, a GUID notification id, `focus`, and `use-any` or a positive window
  id before authenticated broker routing.
- Linux portal calls are bounded to three seconds and fall back to `xdg-open`
  or `notify-send`. Basic system notifications are supported; portal
  notification actions/activation are not. Tray availability is owned by the
  Avalonia desktop backend and has no reliable freedesktop capability probe.
- Windows global summon uses `RegisterHotKey` through a source-generated
  `LibraryImport` boundary and reports collisions per binding. Public,
  reliable virtual-desktop movement is unavailable, so desktop movement is
  best-effort and diagnosed. Linux broker/manual summon and quake placement
  remain available, but cross-desktop shortcut registration is explicitly
  unsupported until a reflection-free interactive freedesktop
  GlobalShortcuts portal session provider is bundled.
- `ToggleShaderEffects` supports only the bounded Skia retro/scanline pass
  selected by `experimental.retroTerminalEffect`. Arbitrary custom HLSL from
  `experimental.pixelShaderPath` remains unsupported.
- `xdg-terminal-exec` is preferred for an explicit per-user default-terminal
  choice. Debian `update-alternatives` is available only through an explicit,
  reversible administrator action. Other distro-specific default-terminal
  registries are unsupported.
- Linux package creation and validation do not alter live protocol/default
  terminal choices. Those remain explicit, reversible helper actions after
  installation.

## Versioning

Assembly versions derive from `VersionPrefix` in `Directory.Build.props`.
Package versions use four numeric components. CI uses
`0.1.<run-number>.0`; release automation must set the final package version
explicitly and must never reuse a published MSIX version.
