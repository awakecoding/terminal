using System.Buffers.Binary;
using System.Xml.Linq;
using Xunit;

namespace WindowsTerminal.App.Tests;

public sealed class LinuxDesktopMetadataTests
{
    private const string AppId = "com.awakecoding.WindowsTerminalDotNet";
    private static readonly string LinuxAssets =
        Path.Combine(AppContext.BaseDirectory, "linux");

    [Fact]
    public void DesktopEntryHasSafeInstalledCommandsAndProtocolHandler()
    {
        var sections = ReadDesktopFile(Path.Combine(LinuxAssets, $"{AppId}.desktop"));
        var application = sections["Desktop Entry"];

        Assert.Equal("Application", application["Type"]);
        Assert.Equal("false", application["Terminal"]);
        Assert.Equal("--", application["X-TerminalArgExec"]);
        Assert.Equal("/opt/windows-terminal-dotnet/WindowsTerminal", application["TryExec"]);
        Assert.Equal(
            "\"/opt/windows-terminal-dotnet/WindowsTerminal\" %u",
            application["Exec"]);
        Assert.Equal("x-scheme-handler/wt-dotnet;", application["MimeType"]);
        Assert.Equal($"{AppId}", application["Icon"]);
        Assert.Equal("NewWindow;NewTab;", application["Actions"]);
        Assert.DoesNotContain('`', application["Exec"]);
        Assert.DoesNotContain('$', application["Exec"]);

        Assert.Equal(
            "\"/opt/windows-terminal-dotnet/WindowsTerminal\" -w new",
            sections["Desktop Action NewWindow"]["Exec"]);
        Assert.Equal(
            "\"/opt/windows-terminal-dotnet/WindowsTerminal\" -w use-any",
            sections["Desktop Action NewTab"]["Exec"]);
    }

    [Fact]
    public void AppStreamMetadataReferencesDesktopEntryAndBinaries()
    {
        var document = XDocument.Load(Path.Combine(LinuxAssets, $"{AppId}.metainfo.xml"));
        var component = Assert.IsType<XElement>(document.Root);

        Assert.Equal("desktop-application", (string?)component.Attribute("type"));
        Assert.Equal(AppId, (string?)component.Element("id"));
        Assert.Equal(
            $"{AppId}.desktop",
            (string?)component.Elements("launchable")
                .Single(element => (string?)element.Attribute("type") == "desktop-id"));
        Assert.Contains(component.Descendants("binary"), node => node.Value == "WindowsTerminal");
        Assert.Contains(component.Descendants("binary"), node => node.Value == "wt");
        Assert.Equal("MIT AND OFL-1.1", (string?)component.Element("project_license"));
        Assert.NotNull(component.Element("content_rating"));
    }

    [Theory]
    [InlineData(16)]
    [InlineData(32)]
    [InlineData(48)]
    [InlineData(64)]
    [InlineData(96)]
    [InlineData(256)]
    public void HicolorIconHasDeclaredDimensions(int size)
    {
        var bytes = File.ReadAllBytes(
            Path.Combine(LinuxAssets, "icons", $"{AppId}-{size}.png"));

        Assert.Equal(
            [137, 80, 78, 71, 13, 10, 26, 10],
            bytes[..8]);
        Assert.Equal(size, BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4)));
        Assert.Equal(size, BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4)));
    }

    [Fact]
    public void InstallerIsDestdirAwareAndRegistrationIsExplicit()
    {
        var script = File.ReadAllText(
            Path.Combine(LinuxAssets, "Install-LinuxDesktopIntegration.sh"));

        Assert.Contains("applications_dir=\"${destdir}${prefix}/share/applications\"", script);
        Assert.Contains("register-protocol) register_protocol", script);
        Assert.Contains("unregister-protocol) unregister_protocol", script);
        Assert.Contains("set-default-terminal) set_default_terminal", script);
        Assert.Contains("unset-default-terminal) unset_default_terminal", script);
        Assert.Contains("cannot be used with DESTDIR", script);
        Assert.DoesNotContain("mktemp", script);
    }

    private static Dictionary<string, Dictionary<string, string>> ReadDesktopFile(string path)
    {
        var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        Dictionary<string, string>? current = null;
        foreach (var rawLine in File.ReadAllLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                current = new Dictionary<string, string>(StringComparer.Ordinal);
                sections.Add(line[1..^1], current);
                continue;
            }

            Assert.NotNull(current);
            var separator = line.IndexOf('=');
            Assert.True(separator > 0, $"Invalid desktop entry line: {line}");
            current.Add(line[..separator], line[(separator + 1)..]);
        }

        return sections;
    }
}
