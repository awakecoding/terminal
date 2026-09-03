[CmdletBinding()]
param(
    [string] $OutputDirectory,

    [securestring] $Password
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$packageRoot = Split-Path -Parent $PSScriptRoot
$dotnetRoot = [IO.Path]::GetFullPath((Join-Path $packageRoot "..\.."))
$manifest = Join-Path $packageRoot "Package.appxmanifest"
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $dotnetRoot "artifacts\msix\certificates"
}

if ($null -eq $Password) {
    $Password = Read-Host "Development certificate password" -AsSecureString
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$certificatePath = [IO.Path]::GetFullPath(
    (Join-Path $OutputDirectory "Devolutions.Terminal.pfx"))
$publicCertificatePath = [IO.Path]::ChangeExtension($certificatePath, ".cer")

$pointer = [Runtime.InteropServices.Marshal]::SecureStringToGlobalAllocUnicode($Password)
try {
    $plainTextPassword = [Runtime.InteropServices.Marshal]::PtrToStringUni($pointer)
    & winapp cert generate `
        --manifest $manifest `
        --output $certificatePath `
        --password $plainTextPassword `
        --export-cer `
        --if-exists skip
    if ($LASTEXITCODE -ne 0) {
        throw "Development certificate generation failed with exit code $LASTEXITCODE."
    }

    if (-not (Test-Path -LiteralPath $publicCertificatePath -PathType Leaf)) {
        $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new(
            $certificatePath,
            $plainTextPassword,
            [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
        try {
            $certificateBytes = $certificate.Export(
                [Security.Cryptography.X509Certificates.X509ContentType]::Cert)
            [IO.File]::WriteAllBytes($publicCertificatePath, $certificateBytes)
        }
        finally {
            $certificate.Dispose()
        }
    }
}
finally {
    $plainTextPassword = $null
    [Runtime.InteropServices.Marshal]::ZeroFreeGlobalAllocUnicode($pointer)
}

Get-Item -LiteralPath $certificatePath
Get-Item -LiteralPath $publicCertificatePath
