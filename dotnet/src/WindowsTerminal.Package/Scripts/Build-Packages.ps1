[CmdletBinding()]
param(
    [ValidateSet("x64", "arm64")]
    [string[]] $Architectures = @("x64", "arm64"),

    [ValidatePattern("^\d{1,5}\.\d{1,5}\.\d{1,5}\.\d{1,5}$")]
    [string] $Version = "0.1.0.0",

    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Release",

    [string] $OutputDirectory,

    [switch] $SkipPublish,

    [switch] $SkipBundle,

    [string] $CertificatePath,

    [securestring] $CertificatePassword
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$packageRoot = Split-Path -Parent $PSScriptRoot
$dotnetRoot = [IO.Path]::GetFullPath((Join-Path $packageRoot "..\.."))
$hostProject = Join-Path $dotnetRoot "src\WindowsTerminal\WindowsTerminal.csproj"
$sourceManifest = Join-Path $packageRoot "Package.appxmanifest"
$sourceAssets = Join-Path $packageRoot "Assets"

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $dotnetRoot "artifacts\msix"
}

$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$layoutRoot = Join-Path $OutputDirectory "layout"
$packageOutput = Join-Path $OutputDirectory "packages"
$metadataRoot = Join-Path $OutputDirectory "metadata"
New-Item -ItemType Directory -Force -Path $layoutRoot | Out-Null
if (Test-Path -LiteralPath $packageOutput) {
    Remove-Item -Recurse -Force -LiteralPath $packageOutput
}

New-Item -ItemType Directory -Path $packageOutput | Out-Null
if (Test-Path -LiteralPath $metadataRoot) {
    Remove-Item -Recurse -Force -LiteralPath $metadataRoot
}

New-Item -ItemType Directory -Path $metadataRoot | Out-Null
Copy-Item -Recurse -LiteralPath $sourceAssets -Destination $metadataRoot
$versionedManifest = Join-Path $metadataRoot "Package.appxmanifest"

function Invoke-Checked {
    param(
        [Parameter(Mandatory)]
        [string] $FilePath,

        [Parameter(ValueFromRemainingArguments)]
        [string[]] $ArgumentList
    )

    & $FilePath @ArgumentList
    if ($LASTEXITCODE -ne 0) {
        throw "'$FilePath' failed with exit code $LASTEXITCODE."
    }
}

function Get-PlainText {
    param([securestring] $Value)

    if ($null -eq $Value) {
        return $null
    }

    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToGlobalAllocUnicode($Value)
    try {
        return [Runtime.InteropServices.Marshal]::PtrToStringUni($pointer)
    }
    finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeGlobalAllocUnicode($pointer)
    }
}

function Write-VersionedManifest {
    param([string] $Destination)

    [xml] $manifest = Get-Content -LiteralPath $sourceManifest
    $manifest.Package.Identity.Version = $Version

    $settings = [Xml.XmlWriterSettings]::new()
    $settings.Encoding = [Text.UTF8Encoding]::new($false)
    $settings.Indent = $true
    $writer = [Xml.XmlWriter]::Create($Destination, $settings)
    try {
        $manifest.Save($writer)
    }
    finally {
        $writer.Dispose()
    }
}

Write-VersionedManifest $versionedManifest

$plainTextPassword = Get-PlainText $CertificatePassword
try {
    $layouts = @()
    foreach ($architecture in $Architectures) {
        $runtimeIdentifier = "win-$architecture"
        $layout = Join-Path $layoutRoot $runtimeIdentifier

        if (-not $SkipPublish) {
            if (Test-Path -LiteralPath $layout) {
                Remove-Item -Recurse -Force -LiteralPath $layout
            }

            New-Item -ItemType Directory -Force -Path $layout | Out-Null
            Invoke-Checked -FilePath dotnet -ArgumentList @(
                "publish",
                $hostProject,
                "-c", $Configuration,
                "-r", $runtimeIdentifier,
                "--self-contained",
                "-o", $layout
            )
        }
        elseif (-not (Test-Path -LiteralPath (Join-Path $layout "WindowsTerminal.exe"))) {
            throw "Published output for '$runtimeIdentifier' was not found at '$layout'."
        }

        foreach ($generatedPath in @(
            (Join-Path $layout "Assets"),
            (Join-Path $layout "Package.appxmanifest"),
            (Join-Path $layout "AppxManifest.xml"),
            (Join-Path $layout "resources.pri"),
            (Join-Path $layout "pri.resfiles"),
            (Join-Path $layout "priconfig.xml")
        )) {
            if (Test-Path -LiteralPath $generatedPath) {
                Remove-Item -Recurse -Force -LiteralPath $generatedPath
            }
        }

        Get-ChildItem -LiteralPath $layout -Filter "*.pdb" -File -Recurse |
            Remove-Item -Force

        $msixPath = Join-Path $packageOutput "Awakecoding.WindowsTerminal.Dev_${Version}_${architecture}.msix"
        $arguments = @(
            "package",
            $layout,
            "--manifest", $versionedManifest,
            "--output", $msixPath,
            "--skip-pri",
            "--quiet"
        )
        if (-not [string]::IsNullOrWhiteSpace($CertificatePath)) {
            $arguments += @("--cert", [IO.Path]::GetFullPath($CertificatePath))
            if ($null -ne $plainTextPassword) {
                $arguments += @("--cert-password", $plainTextPassword)
            }
        }

        Invoke-Checked -FilePath winapp -ArgumentList $arguments
        $layouts += $layout
    }

    if (-not $SkipBundle -and $layouts.Count -gt 1) {
        $architectureLabel = $Architectures -join "_"
        $bundlePath = Join-Path $packageOutput "Awakecoding.WindowsTerminal.Dev_${Version}_${architectureLabel}.msixbundle"
        $arguments = @("package") + $layouts + @(
            "--manifest", $versionedManifest,
            "--output", $bundlePath,
            "--skip-pri",
            "--quiet"
        )
        if (-not [string]::IsNullOrWhiteSpace($CertificatePath)) {
            $arguments += @("--cert", [IO.Path]::GetFullPath($CertificatePath))
            if ($null -ne $plainTextPassword) {
                $arguments += @("--cert-password", $plainTextPassword)
            }
        }

        Invoke-Checked -FilePath winapp -ArgumentList $arguments
    }
}
finally {
    $plainTextPassword = $null
}

Get-ChildItem -LiteralPath $packageOutput -File |
    Where-Object Extension -In ".msix", ".msixbundle" |
    Sort-Object Name
