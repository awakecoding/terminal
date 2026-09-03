# Devolutions Terminal MSIX packaging

This project owns the development package identity and the scripts that turn the
`win-x64` and `win-arm64` NativeAOT publishes into MSIX packages and a bundle.
Direct `dotnet run` and `dotnet publish` remain unpackaged and do not require
registration.

## Identity

| Field | Value |
| --- | --- |
| Package name | `Devolutions.Terminal` |
| Publisher | `CN=Devolutions Inc.` |
| Application ID | `Terminal` |
| Execution aliases | `dt.exe`, `Devolutions.Terminal.exe` |
| Protocol | `dterm:` |
| Minimum Windows | Windows 10, version 2004 (`10.0.19041.0`) |

The identity is stable across local and CI builds so package-scoped data can
survive upgrades. Production/Store onboarding can replace the identity and
publisher in a separate manifest without changing the unpackaged host.

The package is a medium-integrity, full-trust desktop package. It declares only
`runFullTrust`; it does not request broad file-system or network capabilities.
The checked-in visual assets live with the package project.

The effective packaged AUMID is
`Devolutions.Terminal_<publisher-id>!Terminal`; code derives the
publisher-id with `GetCurrentPackageFamilyName` and never guesses it.

## Build unsigned packages

Install [winapp CLI](https://learn.microsoft.com/windows/apps/dev-tools/winapp-cli/)
0.6.0 or newer, then run:

```powershell
.\src\Devolutions.Terminal.Package\Scripts\Build-Packages.ps1
.\src\Devolutions.Terminal.Package\Scripts\Test-Packages.ps1 `
  -PackagePath .\artifacts\msix\packages\*.msix, `
               .\artifacts\msix\packages\*.msixbundle
```

Unsigned packages are the default so CI can publish artifacts for a trusted
release-signing stage. They cannot be installed until signed.

To package NativeAOT outputs produced elsewhere, place them in
`artifacts\msix\layout\win-x64` and `artifacts\msix\layout\win-arm64`, then pass
`-SkipPublish`. `Build-Packages.ps1` still cross-builds the x64/ARM64 shell
helpers. `-SkipNativeBuild` is only valid when matching helper outputs already
exist under `artifacts\msix\native-shell\<architecture>`.

## Development signing and installation

Private keys and generated package artifacts live under `dotnet\artifacts`,
which is ignored by Git. Never commit a `.pfx` or its password.

```powershell
$password = Read-Host "Certificate password" -AsSecureString

.\src\Devolutions.Terminal.Package\Scripts\New-DevelopmentCertificate.ps1 `
  -Password $password

# Rebuild the bundle from the newly signed architecture packages, then sign it.
.\src\Devolutions.Terminal.Package\Scripts\Sign-Packages.ps1 `
  -PackageDirectory .\artifacts\msix\packages `
  -CertificatePath .\artifacts\msix\certificates\Devolutions.Terminal.pfx `
  -Password $password `
  -Version 0.1.0.0
```

Trust only the exported public certificate from an **elevated Administrator**
terminal:

```powershell
.\src\Devolutions.Terminal.Package\Scripts\Trust-DevelopmentCertificate.ps1 `
  -CertificatePath .\artifacts\msix\certificates\Devolutions.Terminal.cer
```

Install, launch, validate, and uninstall:

```powershell
$bundle = ".\artifacts\msix\packages\Devolutions.Terminal_0.1.0.0_x64_arm64.msixbundle"
.\src\Devolutions.Terminal.Package\Scripts\Test-Packages.ps1 -PackagePath $bundle -RequireSignature
.\src\Devolutions.Terminal.Package\Scripts\Install-Package.ps1 -PackagePath $bundle -Launch
dt.exe
Start-Process "dterm:"
.\src\Devolutions.Terminal.Package\Scripts\Uninstall-Package.ps1
```

Use `-ReplaceExisting` for an in-place update or downgrade of the development
package while preserving package data. Before the certificate is trusted,
signature integrity and publisher matching can still be checked with
`-RequireSignature -AllowUntrustedRoot`; installation continues to require a
trusted certificate.

## Capability boundary

`PackageEnvironment.DetectCurrent()` uses NativeAOT-safe generated P/Invoke.
The package includes architecture-matched `Devolutions.Terminal.ShellExt.dll` and
`dt-shell-integration.exe`. The DLL implements the package-registered
`IExplorerCommand` directory/background verbs. The helper implements protocol-1
jump-list refresh and toast publication; requests are bounded, versioned, and
authenticated with a random token passed only through standard input and the
child process environment.

Visible profile name/GUID/icon data produces jump-list tasks. The app refreshes
them after startup and every settings-editor or snippet save. Toast launch data
contains only a version, random notification id, validated `use-any`/positive
window target, and `focus` action. Activation is validated before it is routed
through the authenticated broker; no broker token, command line, environment
data, or other secret is embedded in the toast.

Packaged jump lists/toasts use the package family AUMID. Supported unpackaged
toast use requires both:

- `WT_DOTNET_AUMID` set to an AUMID registered on a Start-menu shortcut.
- `WT_DOTNET_TOAST_SHORTCUT` set to that existing `.lnk` path.
- The shortcut's toast activator property and COM/sparse-package registration
  point to `a3aeb121-45d9-4cd9-a278-4b43d19b95b1`.

Unpackaged jump lists require the same registered AUMID. The helper reports an
explicit unsupported/failed diagnostic when these identity requirements are not
met; the accessible in-app notification remains independent.

Default-terminal delegation is intentionally not advertised in the manifest.
The versioned `default-terminal-delegation.v1` boundary reports unsupported
until the OpenConsole handoff v3 proxy/stub and host are built and package
registered. `BuiltInComInteropSupport=false` remains set on the NativeAOT host.
