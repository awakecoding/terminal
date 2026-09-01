using System.Xml.Linq;
using Xunit;

namespace WindowsTerminal.Package.Tests;

public sealed class ManifestTests
{
    private static readonly XNamespace Foundation =
        "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
    private static readonly XNamespace Uap3 =
        "http://schemas.microsoft.com/appx/manifest/uap/windows10/3";
    private static readonly XNamespace Uap10 =
        "http://schemas.microsoft.com/appx/manifest/uap/windows10/10";
    private static readonly XNamespace Desktop =
        "http://schemas.microsoft.com/appx/manifest/desktop/windows10";
    private static readonly XNamespace RestrictedCapabilities =
        "http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities";

    private static XDocument LoadManifest() =>
        XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Package.appxmanifest"));

    [Fact]
    public void ManifestUsesStableDevelopmentIdentity()
    {
        var identity = LoadManifest().Root!.Element(Foundation + "Identity")!;

        Assert.Equal("Awakecoding.WindowsTerminal.Dev", (string?)identity.Attribute("Name"));
        Assert.Equal("CN=Awakecoding Windows Terminal Development", (string?)identity.Attribute("Publisher"));
        Assert.Equal("0.1.0.0", (string?)identity.Attribute("Version"));
    }

    [Fact]
    public void ManifestRegistersFullTrustHostAliasesAndProtocol()
    {
        var document = LoadManifest();
        var application = document.Descendants(Foundation + "Application").Single();
        var aliases = document.Descendants(Desktop + "ExecutionAlias")
            .Select(alias => (string?)alias.Attribute("Alias"))
            .ToArray();

        Assert.Equal("WindowsTerminal.exe", (string?)application.Attribute("Executable"));
        Assert.Equal("packagedClassicApp", (string?)application.Attribute(Uap10 + "RuntimeBehavior"));
        Assert.Equal("mediumIL", (string?)application.Attribute(Uap10 + "TrustLevel"));
        Assert.Contains("wt.exe", aliases);
        Assert.Contains("WindowsTerminal.exe", aliases);
        Assert.Equal(
            "wt-dotnet",
            (string?)document.Descendants(Uap3 + "Protocol").Single().Attribute("Name"));
        Assert.Single(
            document.Descendants(RestrictedCapabilities + "Capability"),
            capability => (string?)capability.Attribute("Name") == "runFullTrust");
    }
}
