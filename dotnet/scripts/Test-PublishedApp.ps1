[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Executable,

    [int] $StartupTimeoutSeconds = 10
)

$resolved = (Resolve-Path -LiteralPath $Executable -ErrorAction Stop).Path
$process = Start-Process -FilePath $resolved -PassThru

try {
    $deadline = [DateTime]::UtcNow.AddSeconds($StartupTimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline -and -not $process.HasExited -and $process.MainWindowHandle -eq 0) {
        Start-Sleep -Milliseconds 100
        $process.Refresh()
    }

    if ($process.HasExited) {
        throw "Devolutions.Terminal exited during startup with code $($process.ExitCode)."
    }

    if ($process.MainWindowHandle -eq 0) {
        throw "Devolutions.Terminal did not create a window within $StartupTimeoutSeconds seconds."
    }

    Write-Host "Devolutions.Terminal created window handle $($process.MainWindowHandle)."
}
finally {
    if (-not $process.HasExited) {
        Stop-Process -Id $process.Id
        $process.WaitForExit()
    }

    $process.Dispose()
}
