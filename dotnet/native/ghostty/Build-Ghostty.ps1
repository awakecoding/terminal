[CmdletBinding()]
param(
    [string] $ZigPath,
    [string] $SourceDirectory,
    [string] $LlvmStripPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$commit = "3c1ef5b32fc5ea6b93d28493fabf193f595139cf"
$expected = @{
    "win-x64" = "DCB3274F9D8C945AC765A11903614C5DA4BC0CC2EF4EBC23E8CD70C130B7B458"
    "win-arm64" = "691A331E92D0CE17B8407DD370D26394090B14AB8A7C398DF497442293D4ED72"
    "linux-x64" = "46AC64A83F91542D38D60BC0DC169157E9475958566349D7D0B4EEE621C5F929"
    "linux-arm64" = "0D2CB6B391592CF772A166D9393DA859884C46614B219B64E3792B4BC989DADC"
}
$targets = @{
    "win-x64" = "x86_64-windows-msvc"
    "win-arm64" = "aarch64-windows-msvc"
    "linux-x64" = "x86_64-linux-gnu.2.31"
    "linux-arm64" = "aarch64-linux-gnu.2.31"
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

if ([string]::IsNullOrWhiteSpace($LlvmStripPath)) {
    $stripCommand = Get-Command llvm-strip -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($null -ne $stripCommand) {
        $LlvmStripPath = $stripCommand.Source
    }
    elseif ($null -ne $env:ProgramFiles) {
        $LlvmStripPath = Get-ChildItem -File -ErrorAction SilentlyContinue -Path (
            Join-Path $env:ProgramFiles "Microsoft Visual Studio\*\*\VC\Tools\Llvm\x64\bin\llvm-strip.exe"
        ) | Select-Object -First 1 -ExpandProperty FullName
    }
}

if ([string]::IsNullOrWhiteSpace($LlvmStripPath) -or
    -not (Test-Path -LiteralPath $LlvmStripPath)) {
    throw "llvm-strip was not found. Pass -LlvmStripPath to produce deterministic Linux libraries."
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
            -Dstrip=true `
            -Dsimd=true `
            --prefix $prefix
        if ($LASTEXITCODE -ne 0) {
            throw "Ghostty build failed for $rid."
        }
    }
    finally {
        Pop-Location
    }

    $linux = $rid.StartsWith("linux-", [StringComparison]::Ordinal)
    $built = if ($linux) {
        Join-Path $prefix "lib\libghostty-vt.so.0.1.0"
    }
    else {
        Join-Path $prefix "bin\ghostty-vt.dll"
    }
    if ($linux) {
        $stripped = "$built.stripped"
        & $LlvmStripPath --strip-debug -o $stripped $built
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to strip debug data from $rid."
        }

        $built = $stripped
    }

    $hash = (Get-FileHash -LiteralPath $built -Algorithm SHA256).Hash
    if ($hash -ne $expected[$rid]) {
        throw "Unexpected $rid hash $hash. Expected $($expected[$rid])."
    }

    $destinationName = if ($linux) { "libghostty-vt.so" } else { "ghostty-vt.dll" }
    Copy-Item -LiteralPath $built -Destination (Join-Path $nativeRoot "$rid\$destinationName") -Force
}
