[CmdletBinding()]
param(
    [Parameter(Mandatory, ValueFromPipeline)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string[]] $PackagePath,

    [switch] $RequireSignature,

    [switch] $AllowUntrustedRoot
)

begin {
    Set-StrictMode -Version Latest
    $ErrorActionPreference = "Stop"

    function Invoke-MakeAppx {
        param([string[]] $Arguments)

        $output = & winapp tool makeappx @Arguments 2>&1
        if ($LASTEXITCODE -ne 0) {
            $output | Out-Host
            throw "MakeAppx validation failed with exit code $LASTEXITCODE."
        }
    }

    function Assert-Condition {
        param(
            [bool] $Condition,
            [string] $Message
        )

        if (-not $Condition) {
            throw $Message
        }
    }

    function Test-Signature {
        param([string] $Path)

        $signature = Get-AuthenticodeSignature -FilePath $Path
        Assert-Condition ($null -ne $signature.SignerCertificate) "No signature was found on '$Path'."
        Assert-Condition (
            $signature.SignerCertificate.Subject -eq "CN=Awakecoding Windows Terminal Development"
        ) "The signer for '$Path' does not match the package publisher."

        $signatureOutput = & winapp tool signtool verify /pa /v $Path 2>&1
        if ($LASTEXITCODE -eq 0) {
            return
        }

        $signatureText = $signatureOutput -join [Environment]::NewLine
        if ($AllowUntrustedRoot -and $signatureText -match "(?s)root.*not trusted") {
            return
        }

        $signatureOutput | Out-Host
        throw "Signature validation failed for '$Path'."
    }

    function Test-Msix {
        param([string] $Path)

        $extractPath = Join-Path ([IO.Path]::GetTempPath()) ("wt-msix-" + [guid]::NewGuid())
        New-Item -ItemType Directory -Path $extractPath | Out-Null
        try {
            Invoke-MakeAppx -Arguments @("unpack", "/p", $Path, "/d", $extractPath, "/o")
            $manifestPath = Join-Path $extractPath "AppxManifest.xml"
            Assert-Condition (Test-Path -LiteralPath $manifestPath) "AppxManifest.xml is missing from '$Path'."
            Assert-Condition (Test-Path -LiteralPath (Join-Path $extractPath "WindowsTerminal.exe")) "WindowsTerminal.exe is missing from '$Path'."
            $ghosttyPath = Join-Path $extractPath "ghostty-vt.dll"
            Assert-Condition (Test-Path -LiteralPath $ghosttyPath) "ghostty-vt.dll is missing from '$Path'."
            Assert-Condition (
                Test-Path -LiteralPath (Join-Path $extractPath "THIRD-PARTY-NOTICES-GHOSTTY.txt")
            ) "The Ghostty license notice is missing from '$Path'."

            [xml] $manifest = Get-Content -LiteralPath $manifestPath
            $namespace = [Xml.XmlNamespaceManager]::new($manifest.NameTable)
            $namespace.AddNamespace("f", "http://schemas.microsoft.com/appx/manifest/foundation/windows10")
            $namespace.AddNamespace("uap", "http://schemas.microsoft.com/appx/manifest/uap/windows10")
            $namespace.AddNamespace("uap3", "http://schemas.microsoft.com/appx/manifest/uap/windows10/3")
            $namespace.AddNamespace("uap10", "http://schemas.microsoft.com/appx/manifest/uap/windows10/10")
            $namespace.AddNamespace("desktop", "http://schemas.microsoft.com/appx/manifest/desktop/windows10")
            $namespace.AddNamespace("rescap", "http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities")

            $identity = $manifest.SelectSingleNode("/f:Package/f:Identity", $namespace)
            Assert-Condition ($identity.Name -eq "Awakecoding.WindowsTerminal.Dev") "Unexpected package identity name."
            Assert-Condition ($identity.Publisher -eq "CN=Awakecoding Windows Terminal Development") "Unexpected package publisher."
            Assert-Condition ($identity.ProcessorArchitecture -in @("x64", "arm64")) "Unexpected package architecture '$($identity.ProcessorArchitecture)'."
            $expectedGhosttyHash = if ($identity.ProcessorArchitecture -eq "arm64") {
                "691A331E92D0CE17B8407DD370D26394090B14AB8A7C398DF497442293D4ED72"
            }
            else {
                "DCB3274F9D8C945AC765A11903614C5DA4BC0CC2EF4EBC23E8CD70C130B7B458"
            }
            $actualGhosttyHash = (Get-FileHash -LiteralPath $ghosttyPath -Algorithm SHA256).Hash
            Assert-Condition (
                $actualGhosttyHash -eq $expectedGhosttyHash
            ) "ghostty-vt.dll architecture/hash mismatch in '$Path'."

            $application = $manifest.SelectSingleNode("/f:Package/f:Applications/f:Application", $namespace)
            Assert-Condition ($application.Executable -eq "WindowsTerminal.exe") "Unexpected application executable."
            Assert-Condition ($application.GetAttribute("RuntimeBehavior", $namespace.LookupNamespace("uap10")) -eq "packagedClassicApp") "Application must use packagedClassicApp runtime behavior."
            Assert-Condition ($application.GetAttribute("TrustLevel", $namespace.LookupNamespace("uap10")) -eq "mediumIL") "Application must run at medium integrity."

            $aliases = @($manifest.SelectNodes("//desktop:ExecutionAlias", $namespace) | ForEach-Object Alias)
            Assert-Condition ($aliases -contains "wt.exe") "The wt.exe execution alias is missing."
            Assert-Condition ($aliases -contains "WindowsTerminal.exe") "The WindowsTerminal.exe execution alias is missing."

            $protocol = $manifest.SelectSingleNode("//uap3:Protocol[@Name='wt-dotnet']", $namespace)
            Assert-Condition ($null -ne $protocol) "The wt-dotnet protocol registration is missing."
            Assert-Condition ($null -ne $manifest.SelectSingleNode("//rescap:Capability[@Name='runFullTrust']", $namespace)) "The runFullTrust capability is missing."

            foreach ($asset in @(
                "StoreLogo.png",
                "Square44x44Logo.png",
                "Square150x150Logo.png",
                "Wide310x150Logo.png",
                "SmallTile.png",
                "LargeTile.png"
            )) {
                Assert-Condition (Test-Path -LiteralPath (Join-Path $extractPath "Assets\$asset")) "Asset '$asset' is missing."
            }

            if ($RequireSignature) {
                Test-Signature $Path
            }

            [pscustomobject]@{
                Path = $Path
                Architecture = $identity.ProcessorArchitecture
                Version = $identity.Version
                Signed = $RequireSignature.IsPresent
            }
        }
        finally {
            Remove-Item -Recurse -Force -LiteralPath $extractPath
        }
    }
}

process {
    foreach ($path in $PackagePath) {
        $resolvedPath = [IO.Path]::GetFullPath($path)
        if ([IO.Path]::GetExtension($resolvedPath) -eq ".msixbundle") {
            if ($RequireSignature) {
                Test-Signature $resolvedPath
            }

            $bundlePath = Join-Path ([IO.Path]::GetTempPath()) ("wt-msixbundle-" + [guid]::NewGuid())
            New-Item -ItemType Directory -Path $bundlePath | Out-Null
            try {
                Invoke-MakeAppx -Arguments @("unbundle", "/p", $resolvedPath, "/d", $bundlePath, "/o")
                $bundledPackages = @(Get-ChildItem -LiteralPath $bundlePath -Filter "*.msix" -File)
                Assert-Condition ($bundledPackages.Count -eq 2) "The bundle must contain exactly x64 and arm64 packages."
                $results = @($bundledPackages | ForEach-Object { Test-Msix $_.FullName })
                $architectures = @($results.Architecture | Sort-Object -Unique)
                Assert-Condition (
                    $architectures.Count -eq 2 -and
                    $architectures -contains "x64" -and
                    $architectures -contains "arm64"
                ) "The bundle does not contain both x64 and arm64 packages."
                $results
            }
            finally {
                Remove-Item -Recurse -Force -LiteralPath $bundlePath
            }
        }
        elseif ([IO.Path]::GetExtension($resolvedPath) -eq ".msix") {
            Test-Msix $resolvedPath
        }
        else {
            throw "Unsupported package extension for '$resolvedPath'."
        }
    }
}
