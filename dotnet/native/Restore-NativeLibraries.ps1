[CmdletBinding()]
param(
    [string] $Rid,
    [ValidateSet("all", "ghostty", "pty")]
    [string] $Component = "all",
    [string] $ZigPath,
    [switch] $Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "Zig.ps1")

$nativeRoot = $PSScriptRoot
$dotnetRoot = [IO.Path]::GetFullPath((Join-Path $nativeRoot ".."))
$manifest = Get-Content -LiteralPath (Join-Path $nativeRoot "ghostty\ghostty-upstream.json") -Raw |
    ConvertFrom-Json
$zigVersion = [string]$manifest.zig

if ([string]::IsNullOrWhiteSpace($Rid)) {
    $Rid = Get-HostRid
}

$buildGhostty = $Component -in @("all", "ghostty")
$isUnixRid = $Rid.StartsWith("linux-", [StringComparison]::Ordinal) -or
    $Rid.StartsWith("osx-", [StringComparison]::Ordinal)
$buildPty = ($Component -in @("all", "pty")) -and $isUnixRid
if (-not $buildGhostty -and -not $buildPty) {
    Write-Host "No native libraries required for $Rid ($Component)."
    return
}

$mutex = [System.Threading.Mutex]::new($false, "Devolutions.Terminal.NativeRestore")
$acquired = $false
try {
    $acquired = $mutex.WaitOne()
    $needsBuild = $false
    if ($buildGhostty) {
        $fileName = [string]$manifest.targets.$Rid.file
        if ($Force -or -not (Test-Path -LiteralPath (Join-Path $nativeRoot "ghostty\$Rid\$fileName"))) {
            $needsBuild = $true
        }
    }

    if ($buildPty -and ($Force -or -not (Test-Path -LiteralPath (Join-Path $nativeRoot "linux-pty\$Rid\dt-pty-host")))) {
        $needsBuild = $true
    }

    if ($needsBuild) {
        $ZigPath = Resolve-ZigPath -ZigPath $ZigPath -Version $zigVersion -DotnetRoot $dotnetRoot -Install
    }

    if ($buildGhostty) {
        $fileName = [string]$manifest.targets.$Rid.file
        $output = Join-Path $nativeRoot "ghostty\$Rid\$fileName"
        if ($Force -or -not (Test-Path -LiteralPath $output)) {
            $ghosttyArgs = @(
                "-Rid", $Rid,
                "-ZigPath", $ZigPath
            )
            if ($Force) {
                $ghosttyArgs += "-Force"
            }

            & (Join-Path $nativeRoot "ghostty\Build-Ghostty.ps1") @ghosttyArgs
            if ($LASTEXITCODE -ne 0) {
                throw "Build-Ghostty.ps1 failed for $Rid."
            }
        }
        else {
            Write-Host "Using existing $output"
        }
    }

    if ($buildPty) {
        $output = Join-Path $nativeRoot "linux-pty\$Rid\dt-pty-host"
        if ($Force -or -not (Test-Path -LiteralPath $output)) {
            $ptyArgs = @(
                "-Rid", $Rid,
                "-ZigPath", $ZigPath
            )
            if ($Force) {
                $ptyArgs += "-Force"
            }

            & (Join-Path $nativeRoot "linux-pty\Build-LinuxPtyHost.ps1") @ptyArgs
            if ($LASTEXITCODE -ne 0) {
                throw "Build-LinuxPtyHost.ps1 failed for $Rid."
            }
        }
        else {
            Write-Host "Using existing $output"
        }
    }
}
finally {
    if ($acquired) {
        $mutex.ReleaseMutex()
    }

    $mutex.Dispose()
}
