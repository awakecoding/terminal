[CmdletBinding()]
param(
    [ValidateSet("x64", "arm64")]
    [string[]] $Architectures = @("x64", "arm64"),

    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Release",

    [string] $OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$sourceRoot = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $sourceRoot "bin"
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)

$vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path -LiteralPath $vswhere)) {
    throw "Visual Studio Installer's vswhere.exe was not found. Install the MSVC C++ build tools."
}

$installation = & $vswhere -latest -products * `
    -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
    -property installationPath
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($installation)) {
    throw "Visual C++ build tools were not found."
}

$vsDevCmd = Join-Path $installation "Common7\Tools\VsDevCmd.bat"
$optimization = if ($Configuration -eq "Release") { "/O2 /GL /DNDEBUG" } else { "/Od /Zi /D_DEBUG" }
$runtimeLibrary = if ($Configuration -eq "Release") { "/MT" } else { "/MTd" }

foreach ($architecture in $Architectures) {
    $output = Join-Path $OutputDirectory $architecture
    New-Item -ItemType Directory -Force -Path $output | Out-Null

    $common = "/nologo /std:c++20 /EHsc /permissive- /W4 /WX /Brepro $runtimeLibrary /DUNICODE /D_UNICODE /DWIN32_LEAN_AND_MEAN /DWINVER=0x0A00 /D_WIN32_WINNT=0x0A00 $optimization"
    $helperSource = Join-Path $sourceRoot "shell-helper.cpp"
    $extensionSource = Join-Path $sourceRoot "shell-extension.cpp"
    $extensionDefinition = Join-Path $sourceRoot "shell-extension.def"
    $helperOutput = Join-Path $output "dt-shell-integration.exe"
    $extensionOutput = Join-Path $output "Devolutions.Terminal.ShellExt.dll"
    $helperPdb = Join-Path $output "dt-shell-integration.pdb"
    $extensionPdb = Join-Path $output "Devolutions.Terminal.ShellExt.pdb"

    $commands = @(
        "call `"$vsDevCmd`" -no_logo -arch=$architecture -host_arch=x64",
        "cl $common `"$helperSource`" /Fe:`"$helperOutput`" /Fd:`"$helperPdb`" /link /Brepro /INCREMENTAL:NO /OPT:REF /OPT:ICF windowsapp.lib ole32.lib shell32.lib propsys.lib",
        "cl $common /LD `"$extensionSource`" /Fe:`"$extensionOutput`" /Fd:`"$extensionPdb`" /link /Brepro /INCREMENTAL:NO /OPT:REF /OPT:ICF /DEF:`"$extensionDefinition`" ole32.lib shell32.lib"
    ) -join " && "

    & $env:ComSpec /d /s /c $commands
    if ($LASTEXITCODE -ne 0) {
        throw "Native shell integration build failed for $architecture with exit code $LASTEXITCODE."
    }

    foreach ($required in @($helperOutput, $extensionOutput)) {
        if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
            throw "Native shell integration output '$required' was not produced."
        }
    }
}

Get-ChildItem -LiteralPath $OutputDirectory -File -Recurse |
    Where-Object Extension -In ".exe", ".dll" |
    Sort-Object FullName
