[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $SourceDirectory,
    [string] $Rid,
    [string] $NativeRoot = $PSScriptRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$manifest = Get-Content -LiteralPath (Join-Path $NativeRoot "ghostty-upstream.json") -Raw |
    ConvertFrom-Json

function Copy-Rid {
    param(
        [string] $CurrentRid,
        [string] $From
    )

    $fileName = [string]$manifest.targets.$CurrentRid.file
    if ([string]::IsNullOrWhiteSpace($fileName)) {
        throw "Unknown Ghostty RID '$CurrentRid'."
    }

    $source = Join-Path $From $fileName
    if (-not (Test-Path -LiteralPath $source)) {
        $nested = Join-Path $From "$CurrentRid\$fileName"
        if (Test-Path -LiteralPath $nested) {
            $source = $nested
        }
        else {
            throw "Missing '$fileName' under '$From'."
        }
    }

    $destinationDir = Join-Path $NativeRoot $CurrentRid
    New-Item -ItemType Directory -Force -Path $destinationDir | Out-Null
    Copy-Item -LiteralPath $source -Destination (Join-Path $destinationDir $fileName) -Force
    $hashFile = "$source.sha256"
    if (Test-Path -LiteralPath $hashFile) {
        Copy-Item -LiteralPath $hashFile -Destination (Join-Path $destinationDir "$fileName.sha256") -Force
    }
}

if (-not [string]::IsNullOrWhiteSpace($Rid)) {
    Copy-Rid -CurrentRid $Rid -From $SourceDirectory
    return
}

$copied = 0
foreach ($property in $manifest.targets.PSObject.Properties) {
    $candidate = Join-Path $SourceDirectory $property.Name
    if (Test-Path -LiteralPath $candidate) {
        Copy-Rid -CurrentRid $property.Name -From $candidate
        $copied++
    }
}

if ($copied -eq 0) {
    throw "No libghostty-vt RID folders were found under '$SourceDirectory'."
}
