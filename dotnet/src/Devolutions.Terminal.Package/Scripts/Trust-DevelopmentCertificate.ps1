[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string] $CertificatePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$principal = [Security.Principal.WindowsPrincipal]::new(
    [Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Certificate trust installation requires an elevated Administrator terminal."
}

& winapp cert install ([IO.Path]::GetFullPath($CertificatePath))
if ($LASTEXITCODE -ne 0) {
    throw "Certificate trust installation failed with exit code $LASTEXITCODE."
}
