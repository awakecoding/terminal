[CmdletBinding()]
param(
    [string[]] $Rid = @(),
    [string] $ZigPath,
    [switch] $Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "..\Zig.ps1")

$nativeRoot = $PSScriptRoot
$dotnetRoot = [IO.Path]::GetFullPath((Join-Path $nativeRoot "..\.."))
$manifest = Get-Content -LiteralPath (Join-Path $nativeRoot "..\ghostty\ghostty-upstream.json") -Raw |
    ConvertFrom-Json
$ZigPath = Resolve-ZigPath -ZigPath $ZigPath -Version ([string]$manifest.zig) -DotnetRoot $dotnetRoot -Install

$targets = [ordered]@{
    "linux-x64" = "x86_64-linux-gnu.2.31"
    "linux-arm64" = "aarch64-linux-gnu.2.31"
    "osx-x64" = "x86_64-macos.13.0"
    "osx-arm64" = "aarch64-macos.13.0"
}

$selected = @()
if ($Rid.Count -eq 0) {
    $selected = @(Get-HostRid)
}
else {
    $selected = @($Rid)
}

foreach ($currentRid in $selected) {
    if (-not (@($targets.Keys) -contains $currentRid)) {
        throw "dt-pty-host has no target for '$currentRid'."
    }

    $output = Join-Path $nativeRoot "$currentRid\dt-pty-host"
    if (-not $Force -and (Test-Path -LiteralPath $output)) {
        Write-Host "Skipping $currentRid dt-pty-host (already built)"
        continue
    }

    if ($currentRid.StartsWith("osx-", [StringComparison]::Ordinal) -and (Test-HostMacOS)) {
        $sdk = & xcrun --show-sdk-path
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($sdk)) {
            throw "xcrun --show-sdk-path failed; Xcode CLT is required for macOS dt-pty-host."
        }

        $env:SDKROOT = $sdk.Trim()
    }

    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $output) | Out-Null
    & $ZigPath cc `
        "-target" $targets[$currentRid] `
        -O2 `
        (Join-Path $nativeRoot "dt-pty-host.c") `
        -lutil `
        -o $output
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to build dt-pty-host for $currentRid."
    }
}
