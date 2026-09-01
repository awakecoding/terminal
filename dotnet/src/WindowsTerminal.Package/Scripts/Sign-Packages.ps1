[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Container })]
    [string] $PackageDirectory,

    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string] $CertificatePath,

    [Parameter(Mandatory)]
    [ValidatePattern("^\d{1,5}\.\d{1,5}\.\d{1,5}\.\d{1,5}$")]
    [string] $Version,

    [securestring] $Password
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ($null -eq $Password) {
    $Password = Read-Host "Signing certificate password" -AsSecureString
}

$PackageDirectory = [IO.Path]::GetFullPath($PackageDirectory)
$CertificatePath = [IO.Path]::GetFullPath($CertificatePath)
$expectedPackageNames = @(
    "Awakecoding.WindowsTerminal.Dev_${Version}_x64.msix",
    "Awakecoding.WindowsTerminal.Dev_${Version}_arm64.msix"
)
$packages = @(
    foreach ($packageName in $expectedPackageNames) {
        $packagePath = Join-Path $PackageDirectory $packageName
        if (-not (Test-Path -LiteralPath $packagePath -PathType Leaf)) {
            throw "Expected MSIX package '$packagePath' was not found."
        }

        Get-Item -LiteralPath $packagePath
    }
)

if ($packages.Count -ne $expectedPackageNames.Count) {
    throw "Signing requires the x64 and arm64 MSIX packages for version '$Version'."
}

$pointer = [Runtime.InteropServices.Marshal]::SecureStringToGlobalAllocUnicode($Password)
try {
    $plainTextPassword = [Runtime.InteropServices.Marshal]::PtrToStringUni($pointer)
    foreach ($package in $packages) {
        & winapp sign $package.FullName $CertificatePath --password $plainTextPassword
        if ($LASTEXITCODE -ne 0) {
            throw "Signing '$($package.Name)' failed with exit code $LASTEXITCODE."
        }
    }

    if ($packages.Count -gt 1) {
        $bundleInput = Join-Path $PackageDirectory (".bundle-input-" + [guid]::NewGuid())
        New-Item -ItemType Directory -Path $bundleInput | Out-Null
        try {
            Copy-Item -LiteralPath $packages.FullName -Destination $bundleInput
            $bundlePath = Join-Path $PackageDirectory "Awakecoding.WindowsTerminal.Dev_${Version}_x64_arm64.msixbundle"
            & winapp tool makeappx bundle /d $bundleInput /p $bundlePath /bv $Version /o
            if ($LASTEXITCODE -ne 0) {
                throw "Rebuilding the signed MSIX bundle failed with exit code $LASTEXITCODE."
            }

            & winapp sign $bundlePath $CertificatePath --password $plainTextPassword
            if ($LASTEXITCODE -ne 0) {
                throw "Signing the MSIX bundle failed with exit code $LASTEXITCODE."
            }
        }
        finally {
            if (Test-Path -LiteralPath $bundleInput) {
                Remove-Item -Recurse -Force -LiteralPath $bundleInput
            }
        }
    }
}
finally {
    $plainTextPassword = $null
    [Runtime.InteropServices.Marshal]::ZeroFreeGlobalAllocUnicode($pointer)
}
