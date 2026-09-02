[CmdletBinding()]
param(
    [string] $ZigPath,
    [string] $SourceDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$commit = "3c1ef5b32fc5ea6b93d28493fabf193f595139cf"
$expected = @{
    "win-x64" = "DCB3274F9D8C945AC765A11903614C5DA4BC0CC2EF4EBC23E8CD70C130B7B458"
    "win-arm64" = "691A331E92D0CE17B8407DD370D26394090B14AB8A7C398DF497442293D4ED72"
}
$targets = @{
    "win-x64" = "x86_64-windows-msvc"
    "win-arm64" = "aarch64-windows-msvc"
}

$nativeRoot = $PSScriptRoot
$dotnetRoot = [IO.Path]::GetFullPath((Join-Path $nativeRoot "..\.."))
$artifacts = Join-Path $dotnetRoot "artifacts"
if ([string]::IsNullOrWhiteSpace($SourceDirectory)) {
    $SourceDirectory = Join-Path $artifacts "ghostty-src"
}

if ([string]::IsNullOrWhiteSpace($ZigPath)) {
    $ZigPath = Join-Path $artifacts "tools\zig-0.16.0\zig.exe"
}

if (-not (Test-Path -LiteralPath $ZigPath)) {
    throw "Zig 0.16.0 was not found at '$ZigPath'."
}

if (-not (Test-Path -LiteralPath (Join-Path $SourceDirectory ".git"))) {
    & git clone --filter=blob:none https://github.com/ghostty-org/ghostty.git $SourceDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to clone Ghostty."
    }
}

& git -C $SourceDirectory fetch origin $commit
& git -C $SourceDirectory checkout --detach $commit
if ($LASTEXITCODE -ne 0) {
    throw "Failed to check out Ghostty commit $commit."
}

foreach ($rid in $targets.Keys) {
    $prefix = Join-Path $artifacts "ghostty-native\$rid"
    Push-Location $SourceDirectory
    try {
        & $ZigPath build `
            -Demit-lib-vt=true `
            "-Dtarget=$($targets[$rid])" `
            -Doptimize=ReleaseFast `
            -Dsimd=true `
            --prefix $prefix
        if ($LASTEXITCODE -ne 0) {
            throw "Ghostty build failed for $rid."
        }
    }
    finally {
        Pop-Location
    }

    $built = Join-Path $prefix "bin\ghostty-vt.dll"
    $hash = (Get-FileHash -LiteralPath $built -Algorithm SHA256).Hash
    if ($hash -ne $expected[$rid]) {
        throw "Unexpected $rid hash $hash. Expected $($expected[$rid])."
    }

    Copy-Item -LiteralPath $built -Destination (Join-Path $nativeRoot "$rid\ghostty-vt.dll") -Force
}
