using System.Xml.Linq;
using Xunit;

namespace Devolutions.Terminal.Package.Tests;

public sealed class ManifestTests
{
    private static readonly XNamespace Foundation =
        "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
    private static readonly XNamespace Uap3 =
        "http://schemas.microsoft.com/appx/manifest/uap/windows10/3";
    private static readonly XNamespace Uap10 =
        "http://schemas.microsoft.com/appx/manifest/uap/windows10/10";
    private static readonly XNamespace Com =
        "http://schemas.microsoft.com/appx/manifest/com/windows10";
    private static readonly XNamespace Desktop =
        "http://schemas.microsoft.com/appx/manifest/desktop/windows10";
    private static readonly XNamespace Desktop4 =
        "http://schemas.microsoft.com/appx/manifest/desktop/windows10/4";
    private static readonly XNamespace Desktop5 =
        "http://schemas.microsoft.com/appx/manifest/desktop/windows10/5";
    private static readonly XNamespace RestrictedCapabilities =
        "http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities";

    private static XDocument LoadManifest() =>
        XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Package.appxmanifest"));

    [Fact]
    public void ManifestUsesStableDevelopmentIdentity()
    {
        var identity = LoadManifest().Root!.Element(Foundation + "Identity")!;

        Assert.Equal("Devolutions.Terminal", (string?)identity.Attribute("Name"));
        Assert.Equal("CN=Devolutions Inc.", (string?)identity.Attribute("Publisher"));
        Assert.Equal("0.1.0.0", (string?)identity.Attribute("Version"));
    }

    [Fact]
    public void ManifestRegistersArchitectureMatchedExplorerCommand()
    {
        var document = LoadManifest();
        var comClass = document.Descendants(Com + "Class")
            .Single(element =>
                (string?)element.Attribute("Id") == "f4a5f6ac-02b1-46bd-939d-535d391be151");
        var itemTypes = document.Descendants(Desktop5 + "ItemType")
            .Select(item => (string?)item.Attribute("Type"))
            .ToArray();
        var verbs = document.Descendants(Desktop5 + "Verb").ToArray();

        Assert.Equal("f4a5f6ac-02b1-46bd-939d-535d391be151", (string?)comClass.Attribute("Id"));
        Assert.Equal("Devolutions.Terminal.ShellExt.dll", (string?)comClass.Attribute("Path"));
        Assert.Equal("STA", (string?)comClass.Attribute("ThreadingModel"));
        Assert.Contains(document.Descendants(Desktop4 + "Extension"),
            extension => (string?)extension.Attribute("Category") == "windows.fileExplorerContextMenus");
        Assert.Equal(new string?[] { "Directory", @"Directory\Background" }, itemTypes);
        Assert.All(verbs, verb =>
            Assert.Equal((string?)comClass.Attribute("Id"), (string?)verb.Attribute("Clsid")));
    }

    [Fact]
    public void ManifestRegistersNativeToastActivator()
    {
        var document = LoadManifest();
        const string toastClsid = "a3aeb121-45d9-4cd9-a278-4b43d19b95b1";
        var toastClass = document.Descendants(Com + "Class")
            .Single(element => (string?)element.Attribute("Id") == toastClsid);
        var activation = document.Descendants(Desktop + "ToastNotificationActivation").Single();

        Assert.Equal("Devolutions.Terminal.ShellExt.dll", (string?)toastClass.Attribute("Path"));
        Assert.Equal(toastClsid, (string?)activation.Attribute("ToastActivatorCLSID"));
        Assert.Equal(
            "windows.toastNotificationActivation",
            (string?)activation.Parent!.Attribute("Category"));
    }

    [Fact]
    public void ManifestRegistersFullTrustHostAliasesAndProtocol()
    {
        var document = LoadManifest();
        var application = document.Descendants(Foundation + "Application").Single();
        var aliases = document.Descendants(Desktop + "ExecutionAlias")
            .Select(alias => (string?)alias.Attribute("Alias"))
            .ToArray();

        Assert.Equal("Devolutions.Terminal.exe", (string?)application.Attribute("Executable"));
        Assert.Equal("packagedClassicApp", (string?)application.Attribute(Uap10 + "RuntimeBehavior"));
        Assert.Equal("mediumIL", (string?)application.Attribute(Uap10 + "TrustLevel"));
        Assert.Contains("dt.exe", aliases);
        Assert.Contains("Devolutions.Terminal.exe", aliases);
        AssertAliasTargets(document, "dt.exe", "dt.exe");
        AssertAliasTargets(document, "Devolutions.Terminal.exe", "dt.exe");
        Assert.Equal(
            "dterm",
            (string?)document.Descendants(Uap3 + "Protocol").Single().Attribute("Name"));
        Assert.Single(
            document.Descendants(RestrictedCapabilities + "Capability"),
            capability => (string?)capability.Attribute("Name") == "runFullTrust");
    }

    private static void AssertAliasTargets(
        XDocument document,
        string aliasName,
        string executable)
    {
        var alias = document.Descendants(Desktop + "ExecutionAlias")
            .Single(element => (string?)element.Attribute("Alias") == aliasName);
        var extension = alias.Ancestors(Uap3 + "Extension").Single();

        Assert.Equal(executable, (string?)extension.Attribute("Executable"));
    }
}
