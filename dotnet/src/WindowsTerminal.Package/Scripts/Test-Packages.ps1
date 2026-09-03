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

    function Get-PeMachine {
        param([string] $Path)

        $stream = [IO.File]::OpenRead($Path)
        $reader = [IO.BinaryReader]::new($stream)
        try {
            Assert-Condition ($reader.ReadUInt16() -eq 0x5A4D) "'$Path' is not a PE image."
            $stream.Position = 0x3C
            $peOffset = $reader.ReadUInt32()
            Assert-Condition ($peOffset -lt ($stream.Length - 6)) "'$Path' has an invalid PE header offset."
            $stream.Position = $peOffset
            Assert-Condition ($reader.ReadUInt32() -eq 0x00004550) "'$Path' has an invalid PE signature."
            return $reader.ReadUInt16()
        }
        finally {
            $reader.Dispose()
            $stream.Dispose()
        }
    }

    function Test-ShellHelperHashes {
        param([string] $ExtractPath)

        $hashManifest = Join-Path $ExtractPath "SHELL-INTEGRATIONS.sha256"
        Assert-Condition (Test-Path -LiteralPath $hashManifest -PathType Leaf) "The shell helper hash manifest is missing."
        $lines = @(Get-Content -LiteralPath $hashManifest | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
        Assert-Condition ($lines.Count -eq 2) "The shell helper hash manifest must contain exactly two entries."
        foreach ($line in $lines) {
            Assert-Condition ($line -match '^([A-F0-9]{64})  ([A-Za-z0-9.-]+)$') "Invalid shell helper hash entry '$line'."
            $expected = $Matches[1]
            $name = $Matches[2]
            Assert-Condition ($name -in @("WindowsTerminalShellExt.dll", "wt-shell-integration.exe")) "Unexpected hashed shell helper '$name'."
            $path = Join-Path $ExtractPath $name
            Assert-Condition (Test-Path -LiteralPath $path -PathType Leaf) "Hashed shell helper '$name' is missing."
            Assert-Condition ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -eq $expected) "Hash mismatch for '$name'."
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
            Assert-Condition (
                Test-Path -LiteralPath (Join-Path $extractPath "SHELL-INTEGRATION-NOTICE.txt")
            ) "The shell integration notice is missing from '$Path'."
            Test-ShellHelperHashes $extractPath

            [xml] $manifest = Get-Content -LiteralPath $manifestPath
            $namespace = [Xml.XmlNamespaceManager]::new($manifest.NameTable)
            $namespace.AddNamespace("f", "http://schemas.microsoft.com/appx/manifest/foundation/windows10")
            $namespace.AddNamespace("uap", "http://schemas.microsoft.com/appx/manifest/uap/windows10")
            $namespace.AddNamespace("uap3", "http://schemas.microsoft.com/appx/manifest/uap/windows10/3")
            $namespace.AddNamespace("uap10", "http://schemas.microsoft.com/appx/manifest/uap/windows10/10")
            $namespace.AddNamespace("com", "http://schemas.microsoft.com/appx/manifest/com/windows10")
            $namespace.AddNamespace("desktop", "http://schemas.microsoft.com/appx/manifest/desktop/windows10")
            $namespace.AddNamespace("desktop4", "http://schemas.microsoft.com/appx/manifest/desktop/windows10/4")
            $namespace.AddNamespace("desktop5", "http://schemas.microsoft.com/appx/manifest/desktop/windows10/5")
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
            $expectedMachine = if ($identity.ProcessorArchitecture -eq "arm64") { 0xAA64 } else { 0x8664 }
            foreach ($helper in @("WindowsTerminalShellExt.dll", "wt-shell-integration.exe")) {
                $helperPath = Join-Path $extractPath $helper
                Assert-Condition (Test-Path -LiteralPath $helperPath -PathType Leaf) "'$helper' is missing from '$Path'."
                $machine = Get-PeMachine $helperPath
                Assert-Condition ($machine -eq $expectedMachine) "'$helper' architecture does not match '$($identity.ProcessorArchitecture)'."
            }

            $application = $manifest.SelectSingleNode("/f:Package/f:Applications/f:Application", $namespace)
            Assert-Condition ($application.Executable -eq "WindowsTerminal.exe") "Unexpected application executable."
            Assert-Condition ($application.GetAttribute("RuntimeBehavior", $namespace.LookupNamespace("uap10")) -eq "packagedClassicApp") "Application must use packagedClassicApp runtime behavior."
            Assert-Condition ($application.GetAttribute("TrustLevel", $namespace.LookupNamespace("uap10")) -eq "mediumIL") "Application must run at medium integrity."

            $aliases = @($manifest.SelectNodes("//desktop:ExecutionAlias", $namespace) | ForEach-Object Alias)
            Assert-Condition ($aliases -contains "wt.exe") "The wt.exe execution alias is missing."
            Assert-Condition ($aliases -contains "WindowsTerminal.exe") "The WindowsTerminal.exe execution alias is missing."

            $protocol = $manifest.SelectSingleNode("//uap3:Protocol[@Name='wt-dotnet']", $namespace)
            Assert-Condition ($null -ne $protocol) "The wt-dotnet protocol registration is missing."
            $comClass = $manifest.SelectSingleNode("//com:SurrogateServer/com:Class[@Id='f4a5f6ac-02b1-46bd-939d-535d391be151']", $namespace)
            Assert-Condition ($null -ne $comClass) "The Explorer COM surrogate registration is missing."
            Assert-Condition ($comClass.Id -eq "f4a5f6ac-02b1-46bd-939d-535d391be151") "Unexpected Explorer COM CLSID."
            Assert-Condition ($comClass.Path -eq "WindowsTerminalShellExt.dll") "Unexpected Explorer COM server path."
            $toastClass = $manifest.SelectSingleNode("//com:SurrogateServer/com:Class[@Id='a3aeb121-45d9-4cd9-a278-4b43d19b95b1']", $namespace)
            Assert-Condition ($null -ne $toastClass) "The native toast activator COM registration is missing."
            $toastActivation = $manifest.SelectSingleNode("//desktop:ToastNotificationActivation", $namespace)
            Assert-Condition ($null -ne $toastActivation) "The toast activation extension is missing."
            Assert-Condition (
                $toastActivation.ToastActivatorCLSID -eq "a3aeb121-45d9-4cd9-a278-4b43d19b95b1"
            ) "Unexpected toast activator CLSID."
            $verbs = @($manifest.SelectNodes("//desktop5:Verb", $namespace))
            Assert-Condition ($verbs.Count -eq 2) "Directory and Directory\\Background Explorer verbs are required."
            Assert-Condition (
                (@($manifest.SelectNodes("//desktop5:ItemType", $namespace) | ForEach-Object Type) -join "|") -eq "Directory|Directory\Background"
            ) "Unexpected Explorer context-menu item types."
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
