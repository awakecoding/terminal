[CmdletBinding()]
param(
    [string] $ZigPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$nativeRoot = $PSScriptRoot
$dotnetRoot = [IO.Path]::GetFullPath((Join-Path $nativeRoot "..\.."))
if ([string]::IsNullOrWhiteSpace($ZigPath)) {
    $ZigPath = Join-Path $dotnetRoot "artifacts\tools\zig-0.16.0\zig.exe"
}

if (-not (Test-Path -LiteralPath $ZigPath)) {
    throw "Zig 0.16.0 was not found at '$ZigPath'."
}

$targets = @{
    "linux-x64" = "x86_64-linux-gnu.2.31"
    "linux-arm64" = "aarch64-linux-gnu.2.31"
    "osx-x64" = "x86_64-macos"
    "osx-arm64" = "aarch64-macos"
}
foreach ($rid in $targets.Keys) {
    $output = Join-Path $nativeRoot "$rid\dt-pty-host"
    New-Item -ItemType Directory -Force (Split-Path -Parent $output) | Out-Null
    & $ZigPath cc `
        "-target" $targets[$rid] `
        -O2 `
        (Join-Path $nativeRoot "dt-pty-host.c") `
        -lutil `
        -o $output
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to build dt-pty-host for $rid."
    }
}
