Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Test-HostWindows {
    return [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
        [System.Runtime.InteropServices.OSPlatform]::Windows)
}

function Test-HostLinux {
    return [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
        [System.Runtime.InteropServices.OSPlatform]::Linux)
}

function Test-HostMacOS {
    return [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
        [System.Runtime.InteropServices.OSPlatform]::OSX)
}

function Get-HostRid {
    $arch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture
    $archRid = switch ($arch) {
        "X64" { "x64" }
        "Arm64" { "arm64" }
        default { throw "Unsupported host architecture '$arch'." }
    }

    if (Test-HostWindows) {
        return "win-$archRid"
    }

    if (Test-HostLinux) {
        return "linux-$archRid"
    }

    if (Test-HostMacOS) {
        return "osx-$archRid"
    }

    throw "Unsupported host OS."
}

function Get-HostZigTriple {
    $arch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture
    $archName = switch ($arch) {
        "X64" { "x86_64" }
        "Arm64" { "aarch64" }
        default { throw "Unsupported host architecture '$arch'." }
    }

    if (Test-HostWindows) {
        return "$archName-windows"
    }

    if (Test-HostLinux) {
        return "$archName-linux"
    }

    if (Test-HostMacOS) {
        return "$archName-macos"
    }

    throw "Unsupported host OS."
}

function Install-Zig {
    param(
        [Parameter(Mandatory)]
        [string] $Version,
        [Parameter(Mandatory)]
        [string] $DotnetRoot
    )

    $triple = Get-HostZigTriple
    $extension = if (Test-HostWindows) { "zip" } else { "tar.xz" }
    $archiveName = "zig-$triple-$Version.$extension"
    $url = "https://ziglang.org/download/$Version/$archiveName"
    $toolRoot = Join-Path $DotnetRoot "artifacts\tools"
    $extractRoot = Join-Path $toolRoot "zig-$Version"
    $marker = Join-Path $extractRoot ".downloaded"
    New-Item -ItemType Directory -Force -Path $toolRoot | Out-Null

    if (-not (Test-Path -LiteralPath $marker)) {
        $archivePath = Join-Path $toolRoot $archiveName
        Write-Host "Downloading $url"
        Invoke-WebRequest -Uri $url -OutFile $archivePath
        if (Test-Path -LiteralPath $extractRoot) {
            Remove-Item -LiteralPath $extractRoot -Recurse -Force
        }

        New-Item -ItemType Directory -Force -Path $extractRoot | Out-Null
        if ($extension -eq "zip") {
            Expand-Archive -LiteralPath $archivePath -DestinationPath $extractRoot
        }
        else {
            & tar -xJf $archivePath -C $extractRoot
            if ($LASTEXITCODE -ne 0) {
                throw "Failed to extract $archivePath."
            }
        }

        Set-Content -LiteralPath $marker -Value $url
    }

    $zigName = if (Test-HostWindows) { "zig.exe" } else { "zig" }
    $found = Get-ChildItem -LiteralPath $extractRoot -Filter $zigName -Recurse -File |
        Select-Object -First 1
    if ($null -eq $found) {
        throw "Zig $Version was extracted but '$zigName' was not found under '$extractRoot'."
    }

    return $found.FullName
}

function Resolve-ZigPath {
    param(
        [string] $ZigPath,
        [Parameter(Mandatory)]
        [string] $Version,
        [Parameter(Mandatory)]
        [string] $DotnetRoot,
        [switch] $Install
    )

    if (-not [string]::IsNullOrWhiteSpace($ZigPath) -and (Test-Path -LiteralPath $ZigPath)) {
        return $ZigPath
    }

    $command = Get-Command zig -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -ne $command) {
        return $command.Source
    }

    $zigName = if (Test-HostWindows) { "zig.exe" } else { "zig" }
    $fallback = Join-Path $DotnetRoot "artifacts\tools\zig-$Version"
    $found = Get-ChildItem -LiteralPath $fallback -Filter $zigName -Recurse -File -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($null -ne $found) {
        return $found.FullName
    }

    if ($Install -or [string]::IsNullOrWhiteSpace($ZigPath)) {
        return Install-Zig -Version $Version -DotnetRoot $DotnetRoot
    }

    throw "Zig $Version was not found. Pass -ZigPath or allow automatic install."
}
