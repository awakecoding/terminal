# Build, migration, and release guide

## Prerequisites

- Windows 10 version 2004 (build 19041) or later
- .NET SDK selected by `dotnet/global.json`
- Visual Studio 2022/2026 Build Tools with Desktop C++ for NativeAOT linking
- WinApp CLI 0.6.0 for MSIX creation and validation
- A trusted code-signing certificate for distributable MSIX artifacts

The app is Windows-only because local sessions use ConPTY. `Terminal.Core`,
`Terminal.Settings`, and most renderer planning tests remain platform-neutral.

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
5. Build and structurally validate both MSIX packages and the bundle.
6. Sign and verify package publisher/identity/version in a protected environment.
7. Install, launch `WindowsTerminal.exe`, invoke `wt.exe`, upgrade, and uninstall
   on clean x64 and ARM64 Windows VMs.
8. Run multi-tab/pane, settings, CLI forwarding, UIA, Unicode/CJK/emoji, VT,
   ConPTY cancellation, and long-output stress workflows.
9. Compare startup time, working set, renderer allocations, and artifact size
   with the previous candidate.
10. Review diagnostics and documentation for intentional platform differences.

## Platform constraints

- Public out-of-process ConPTY can filter or alter DCS/APC payloads on some
  Windows builds. Sixel works when the selected connection passes DCS bytes
  through unchanged.
- Avalonia 11.3 exposes the terminal as a readable UIA Document/Value provider,
  but does not provide a public bridge for native UIA TextPattern/TextPattern2
  or LiveSetting events. Managed ranges and visible notification text remain
  available.
- Azure Cloud Shell requires a public-client application ID and host-provided
  device-code/tenant UI. No client secret is embedded.
- The development package identity and `wt.exe` alias can conflict with an
  installed Microsoft Windows Terminal alias; Windows alias settings choose the
  active provider.
- Default-terminal registration, shell verbs, notifications, and jumplists are
  package/Windows-version capabilities and must degrade explicitly when running
  unpackaged.

## Versioning

Assembly versions derive from `VersionPrefix` in `Directory.Build.props`.
Package versions use four numeric components. CI uses
`0.1.<run-number>.0`; release automation must set the final package version
explicitly and must never reuse a published MSIX version.
