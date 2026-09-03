[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$packageName = "Devolutions.Terminal"
$installed = @(Get-AppxPackage -Name $packageName)
if ($installed.Count -eq 0) {
    Write-Host "Package '$packageName' is not installed."
    return
}

$installed | Remove-AppxPackage
if (Get-AppxPackage -Name $packageName) {
    throw "Package '$packageName' is still registered after uninstall."
}
