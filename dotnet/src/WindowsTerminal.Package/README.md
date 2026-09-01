# Windows Terminal .NET MSIX packaging

This project owns the development package identity and the scripts that turn the
`win-x64` and `win-arm64` NativeAOT publishes into MSIX packages and a bundle.
Direct `dotnet run` and `dotnet publish` remain unpackaged and do not require
registration.

## Identity

| Field | Value |
| --- | --- |
| Package name | `Awakecoding.WindowsTerminal.Dev` |
| Publisher | `CN=Awakecoding Windows Terminal Development` |
| Application ID | `Terminal` |
| Execution aliases | `wt.exe`, `WindowsTerminal.exe` |
| Protocol | `wt-dotnet:` |
| Minimum Windows | Windows 10, version 2004 (`10.0.19041.0`) |

The identity is stable across local and CI builds so package-scoped data can
survive upgrades. Production/Store onboarding can replace the identity and
publisher in a separate manifest without changing the unpackaged host.

The package is a medium-integrity, full-trust desktop package. It declares only
`runFullTrust`; it does not request broad file-system or network capabilities.
The checked-in visual assets are copies of the matching scale-100 assets under
`res\terminal\images`.

## Build unsigned packages

Install [winapp CLI](https://learn.microsoft.com/windows/apps/dev-tools/winapp-cli/)
0.6.0 or newer, then run:

```powershell
cd dotnet
.\src\WindowsTerminal.Package\Scripts\Build-Packages.ps1
.\src\WindowsTerminal.Package\Scripts\Test-Packages.ps1 `
  -PackagePath .\artifacts\msix\packages\*.msix, `
               .\artifacts\msix\packages\*.msixbundle
```

Unsigned packages are the default so CI can publish artifacts for a trusted
release-signing stage. They cannot be installed until signed.

To package NativeAOT outputs produced elsewhere, place them in
`artifacts\msix\layout\win-x64` and `artifacts\msix\layout\win-arm64`, then pass
`-SkipPublish`.

## Development signing and installation

Private keys and generated package artifacts live under `dotnet\artifacts`,
which is ignored by Git. Never commit a `.pfx` or its password.

```powershell
$password = Read-Host "Certificate password" -AsSecureString

.\src\WindowsTerminal.Package\Scripts\New-DevelopmentCertificate.ps1 `
  -Password $password

# Rebuild the bundle from the newly signed architecture packages, then sign it.
.\src\WindowsTerminal.Package\Scripts\Sign-Packages.ps1 `
  -PackageDirectory .\artifacts\msix\packages `
  -CertificatePath .\artifacts\msix\certificates\Awakecoding.WindowsTerminal.Dev.pfx `
  -Password $password `
  -Version 0.1.0.0
```

Trust only the exported public certificate from an **elevated Administrator**
terminal:

```powershell
.\src\WindowsTerminal.Package\Scripts\Trust-DevelopmentCertificate.ps1 `
  -CertificatePath .\artifacts\msix\certificates\Awakecoding.WindowsTerminal.Dev.cer
```

Install, launch, validate, and uninstall:

```powershell
$bundle = ".\artifacts\msix\packages\Awakecoding.WindowsTerminal.Dev_0.1.0.0_x64_arm64.msixbundle"
.\src\WindowsTerminal.Package\Scripts\Test-Packages.ps1 -PackagePath $bundle -RequireSignature
.\src\WindowsTerminal.Package\Scripts\Install-Package.ps1 -PackagePath $bundle -Launch
wt.exe
Start-Process "wt-dotnet:"
.\src\WindowsTerminal.Package\Scripts\Uninstall-Package.ps1
```

Use `-ReplaceExisting` for an in-place update or downgrade of the development
package while preserving package data. Before the certificate is trusted,
signature integrity and publisher matching can still be checked with
`-RequireSignature -AllowUntrustedRoot`; installation continues to require a
trusted certificate.

## Capability boundary

`PackageEnvironment.DetectCurrent()` uses the NativeAOT-safe
`GetCurrentPackageFullName` interop in `WindowsTerminal.Interop`. Packaged
builds currently report package identity, execution aliases, and protocol
activation as available. Notifications, jump lists, default-terminal
registration, and the Explorer shell verb remain explicit unavailable
capabilities until their application-side integrations are implemented.
