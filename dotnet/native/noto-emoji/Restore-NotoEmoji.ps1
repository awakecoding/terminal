[CmdletBinding()]
param(
    [switch] $Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$nativeRoot = $PSScriptRoot
$manifest = Get-Content -LiteralPath (Join-Path $nativeRoot "noto-emoji.json") -Raw |
    ConvertFrom-Json
$destination = Join-Path $nativeRoot ([string]$manifest.file)

function Get-FileSha256([string] $Path) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $hash = $sha.ComputeHash([IO.File]::ReadAllBytes($Path))
        return [BitConverter]::ToString($hash).Replace("-", "")
    }
    finally {
        $sha.Dispose()
    }
}

if (-not $Force -and (Test-Path -LiteralPath $destination)) {
    $existing = Get-FileSha256 $destination
    if ($existing -eq [string]$manifest.sha256) {
        Write-Host "Using existing $destination"
        return
    }

    Write-Host "Replacing $destination (hash $existing did not match pin)."
}

$url = "$($manifest.repository)/raw/$($manifest.commit)/$($manifest.path)"
$temp = Join-Path ([IO.Path]::GetTempPath()) ("NotoColorEmoji." + [Guid]::NewGuid().ToString("N") + ".ttf")
try {
    Write-Host "Downloading $url"
    Invoke-WebRequest -Uri $url -OutFile $temp
    $actual = Get-FileSha256 $temp
    if ($actual -ne [string]$manifest.sha256) {
        throw "Noto Color Emoji hash $actual did not match $($manifest.sha256)."
    }

    Copy-Item -LiteralPath $temp -Destination $destination -Force
}
finally {
    if (Test-Path -LiteralPath $temp) {
        Remove-Item -LiteralPath $temp -Force
    }
}
