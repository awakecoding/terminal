[CmdletBinding()]
param(
    [string[]] $Rid = @(),
    [string] $ZigPath,
    [string] $SourceDirectory,
    [string] $LlvmStripPath,
    [switch] $InstallZig,
    [switch] $SkipHashCheck,
    [switch] $VerifyHash,
    [switch] $SkipCopy,
    [switch] $Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "..\Zig.ps1")

$nativeRoot = $PSScriptRoot
$manifestPath = Join-Path $nativeRoot "ghostty-upstream.json"
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$commit = [string]$manifest.commit
$zigVersion = [string]$manifest.zig
$dotnetRoot = [IO.Path]::GetFullPath((Join-Path $nativeRoot "..\.."))
$artifacts = Join-Path $dotnetRoot "artifacts"
$SkipHashCheck = $SkipHashCheck -or -not $VerifyHash

function Resolve-LlvmStrip {
    param([string] $Explicit)

    if (-not [string]::IsNullOrWhiteSpace($Explicit) -and (Test-Path -LiteralPath $Explicit)) {
        return $Explicit
    }

    $command = Get-Command llvm-strip -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -ne $command) {
        return $command.Source
    }

    if ((Test-HostWindows) -and $null -ne $env:ProgramFiles) {
        $vsStrip = Get-ChildItem -File -ErrorAction SilentlyContinue -Path (
            Join-Path $env:ProgramFiles "Microsoft Visual Studio\*\*\VC\Tools\Llvm\x64\bin\llvm-strip.exe"
        ) | Select-Object -First 1
        if ($null -ne $vsStrip) {
            return $vsStrip.FullName
        }
    }

    if (Test-HostMacOS) {
        $xcrun = Get-Command xcrun -ErrorAction SilentlyContinue
        if ($null -ne $xcrun) {
            $fromXcode = & xcrun --find llvm-strip 2>$null
            if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($fromXcode)) {
                return $fromXcode.Trim()
            }
        }
    }

    $strip = Get-Command strip -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -ne $strip) {
        return $strip.Source
    }

    return $null
}

function Find-BuiltLibrary {
    param(
        [string] $Prefix,
        [string] $FileName
    )

    $candidates = @(
        (Join-Path $Prefix "bin\$FileName"),
        (Join-Path $Prefix "lib\$FileName"),
        (Join-Path $Prefix "lib\$FileName.0.1.0")
    )
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) {
            return $candidate
        }
    }

    $search = Get-ChildItem -LiteralPath (Join-Path $Prefix "lib") -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like "$FileName*" } |
        Select-Object -First 1
    if ($null -ne $search) {
        return $search.FullName
    }

    throw "Zig build finished but '$FileName' was not found under '$Prefix'."
}

if ([string]::IsNullOrWhiteSpace($SourceDirectory)) {
    $SourceDirectory = Join-Path $artifacts "ghostty-src"
}

$ZigPath = Resolve-ZigPath -ZigPath $ZigPath -Version $zigVersion -DotnetRoot $dotnetRoot -Install

$selected = @()
if ($Rid.Count -eq 0) {
    $selected = @(Get-HostRid)
}
else {
    foreach ($value in $Rid) {
        if (-not (@($manifest.targets.PSObject.Properties.Name) -contains $value)) {
            throw "Unknown RID '$value'. Known: $($manifest.targets.PSObject.Properties.Name -join ', ')."
        }

        $selected += $value
    }
}

$pending = @()
foreach ($currentRid in $selected) {
    $fileName = [string]$manifest.targets.$currentRid.file
    $destination = Join-Path $nativeRoot "$currentRid\$fileName"
    if (-not $Force -and -not $SkipCopy -and (Test-Path -LiteralPath $destination)) {
        Write-Host "Skipping $currentRid (already built)"
        continue
    }

    $pending += $currentRid
}

if ($pending.Count -eq 0) {
    return
}

$selected = $pending

if (-not (Test-Path -LiteralPath (Join-Path $SourceDirectory ".git"))) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $SourceDirectory) | Out-Null
    & git clone --filter=blob:none $manifest.repository $SourceDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to clone Ghostty."
    }
}

& git -C $SourceDirectory fetch origin $commit
& git -C $SourceDirectory checkout --detach $commit
if ($LASTEXITCODE -ne 0) {
    throw "Failed to check out Ghostty commit $commit."
}

$hashes = [ordered]@{}
foreach ($currentRid in $selected) {
    $target = $manifest.targets.$currentRid
    $zigTarget = [string]$target.zigTarget
    $fileName = [string]$target.file
    $prefix = Join-Path $artifacts "ghostty-native\$currentRid"

    if ($currentRid.StartsWith("osx-", [StringComparison]::Ordinal) -and (Test-HostMacOS)) {
        $sdk = & xcrun --show-sdk-path
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($sdk)) {
            throw "xcrun --show-sdk-path failed; Xcode CLT is required for macOS libghostty-vt."
        }

        $env:SDKROOT = $sdk.Trim()
    }

    if (Test-Path -LiteralPath $prefix) {
        Remove-Item -LiteralPath $prefix -Recurse -Force
    }

    Push-Location $SourceDirectory
    try {
        & $ZigPath build `
            -Demit-lib-vt=true `
            "-Dtarget=$zigTarget" `
            "-Doptimize=$($manifest.optimize)" `
            -Dstrip=true `
            -Dsimd=true `
            --prefix $prefix
        if ($LASTEXITCODE -ne 0) {
            throw "Ghostty build failed for $currentRid."
        }
    }
    finally {
        Pop-Location
    }

    $built = Find-BuiltLibrary -Prefix $prefix -FileName $fileName
    $linux = $currentRid.StartsWith("linux-", [StringComparison]::Ordinal)
    if ($linux -and $manifest.strip) {
        $stripTool = Resolve-LlvmStrip -Explicit $LlvmStripPath
        if ([string]::IsNullOrWhiteSpace($stripTool)) {
            $requiresHash = @($target.PSObject.Properties.Name) -contains "sha256" -and
                -not [string]::IsNullOrWhiteSpace([string]$target.sha256)
            if (-not $SkipHashCheck -and $requiresHash) {
                throw "llvm-strip/strip was not found. Pass -LlvmStripPath or -SkipHashCheck."
            }
        }
        else {
            $stripped = "$built.stripped"
            & $stripTool --strip-debug -o $stripped $built
            if ($LASTEXITCODE -ne 0) {
                throw "Failed to strip debug data from $currentRid."
            }

            $built = $stripped
        }
    }

    $hash = (Get-FileHash -LiteralPath $built -Algorithm SHA256).Hash
    $expected = $null
    if (@($target.PSObject.Properties.Name) -contains "sha256") {
        $expected = [string]$target.sha256
    }

    if (-not $SkipHashCheck -and -not [string]::IsNullOrWhiteSpace($expected) -and $hash -ne $expected) {
        throw "Unexpected $currentRid hash $hash. Expected $expected."
    }

    $hashes[$currentRid] = $hash
    Write-Host "$currentRid SHA-256 $hash"

    if (-not $SkipCopy) {
        $destinationDir = Join-Path $nativeRoot $currentRid
        New-Item -ItemType Directory -Force -Path $destinationDir | Out-Null
        $destination = Join-Path $destinationDir $fileName
        Copy-Item -LiteralPath $built -Destination $destination -Force
        Set-Content -LiteralPath "$destination.sha256" -Value $hash
    }
}

if ($env:GITHUB_STEP_SUMMARY) {
    $lines = @(
        "## libghostty-vt",
        "",
        "Commit ``$commit`` built with Zig $zigVersion.",
        "",
        "| RID | SHA-256 |",
        "| --- | --- |"
    )
    foreach ($entry in $hashes.GetEnumerator()) {
        $lines += "| ``$($entry.Key)`` | ``$($entry.Value)`` |"
    }

    Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value ($lines -join "`n")
}
