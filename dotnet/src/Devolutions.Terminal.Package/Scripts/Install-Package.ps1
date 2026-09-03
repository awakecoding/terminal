[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string] $PackagePath,

    [switch] $ReplaceExisting,

    [switch] $Launch
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$packageName = "Devolutions.Terminal"
$existing = @(Get-AppxPackage -Name $packageName)
if ($existing.Count -gt 0 -and -not $ReplaceExisting) {
    throw "Package '$packageName' is already installed. Use -ReplaceExisting to replace it."
}

$installArguments = @{
    Path = [IO.Path]::GetFullPath($PackagePath)
    ForceApplicationShutdown = $true
}
if ($ReplaceExisting) {
    $installArguments.ForceUpdateFromAnyVersion = $true
}

Add-AppxPackage @installArguments
$installed = Get-AppxPackage -Name $packageName
if ($null -eq $installed) {
    throw "Package installation completed without registering '$packageName'."
}

$installed
if ($Launch) {
    $applicationUserModelId = "$($installed.PackageFamilyName)!Terminal"
    Start-Process explorer.exe "shell:AppsFolder\$applicationUserModelId"
}
