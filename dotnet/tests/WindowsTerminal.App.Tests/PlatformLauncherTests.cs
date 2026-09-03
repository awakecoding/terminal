using System.Diagnostics;
using Microsoft.Terminal.Settings;
using WindowsTerminal.Platform;
using WindowsTerminal.Package;
using Xunit;

namespace WindowsTerminal.App.Tests;

public sealed class PlatformLauncherTests
{
    [Fact]
    public void LinuxUsesXdgOpenWithoutShellParsing()
    {
        var launcher = new PlatformLauncher(
            DesktopPlatform.Linux,
            linuxCapabilities: Capabilities(LinuxDesktopCapability.XdgOpen));

        var startInfo = launcher.CreateStartInfo("https://example.com/a path");

        Assert.Equal("xdg-open", startInfo.FileName);
        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.CreateNoWindow);
        Assert.Equal(["https://example.com/a path"], startInfo.ArgumentList);
    }

    [Fact]
    public void MacOsUsesOpenWithoutShellParsing()
    {
        var launcher = new PlatformLauncher(DesktopPlatform.MacOS);

        var startInfo = launcher.CreateStartInfo("/tmp/a path");

        Assert.Equal("open", startInfo.FileName);
        Assert.False(startInfo.UseShellExecute);
        Assert.Equal(["/tmp/a path"], startInfo.ArgumentList);
    }

    [Fact]
    public void WindowsDelegatesToTheRegisteredShell()
    {
        var launcher = new PlatformLauncher(DesktopPlatform.Windows);

        var startInfo = launcher.CreateStartInfo(@"C:\source");

        Assert.Equal(@"C:\source", startInfo.FileName);
        Assert.True(startInfo.UseShellExecute);
        Assert.Empty(startInfo.ArgumentList);
    }

    [Fact]
    public void WindowsRoutesToastAndVisibleProfilesThroughNativeBoundary()
    {
        var shell = new RecordingWindowsShell();
        var launcher = new PlatformLauncher(DesktopPlatform.Windows, windowsShell: shell);
        var settings = new AppSettings
        {
            Profiles =
            [
                new ProfileSettings
                {
                    Name = "PowerShell",
                    Guid = "{11111111-1111-1111-1111-111111111111}",
                },
                new ProfileSettings
                {
                    Name = "Hidden",
                    Guid = "{22222222-2222-2222-2222-222222222222}",
                    Hidden = true,
                },
            ],
        };

        var notification = launcher.ShowNotification("Build", "Complete");
        var jumpList = launcher.RefreshJumpList(settings);

        Assert.True(notification.Attempted);
        Assert.True(notification.Succeeded);
        Assert.True(jumpList.Succeeded);
        Assert.Equal("Build", shell.Toast!.Title);
        Assert.Collection(shell.Profiles!, profile => Assert.Equal("PowerShell", profile.Name));
    }

    [Fact]
    public void OpenSurfacesDesktopLaunchFailure()
    {
        var launcher = new PlatformLauncher(
            DesktopPlatform.Linux,
            static _ => null);

        var error = Assert.Throws<InvalidOperationException>(
            () => launcher.Open("/tmp"));

        Assert.Contains("/tmp", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OpenDirectoryRejectsMissingDirectoryBeforeLaunching()
    {
        ProcessStartInfo? captured = null;
        var launcher = new PlatformLauncher(
            DesktopPlatform.Linux,
            startInfo =>
            {
                captured = startInfo;
                return null;
            });

        Assert.Throws<DirectoryNotFoundException>(
            () => launcher.OpenDirectory(
                Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))));
        Assert.Null(captured);
    }

    [Fact]
    public void CapabilityDetectionRequiresSessionBusForPortals()
    {
        var environment = new Dictionary<string, string?>
        {
            ["DBUS_SESSION_BUS_ADDRESS"] = "unix:path=/run/user/1000/bus",
            ["XDG_CURRENT_DESKTOP"] = "GNOME",
        };
        var commands = new HashSet<string>(StringComparer.Ordinal)
        {
            "gdbus", "xdg-open", "notify-send", "xdg-terminal-exec", "xdg-mime",
        };

        var capabilities = LinuxDesktopCapabilities.Detect(
            name => environment.GetValueOrDefault(name),
            commands.Contains);

        Assert.Equal("GNOME", capabilities.DesktopName);
        Assert.True(capabilities.Supports(LinuxDesktopCapability.PortalOpen));
        Assert.True(capabilities.Supports(LinuxDesktopCapability.PortalNotifications));
        Assert.True(capabilities.Supports(LinuxDesktopCapability.XdgOpen));
        Assert.True(capabilities.Supports(LinuxDesktopCapability.NotifySend));
        Assert.True(capabilities.Supports(LinuxDesktopCapability.XdgTerminalExec));
        Assert.True(capabilities.Supports(LinuxDesktopCapability.XdgMime));
        Assert.False(capabilities.Supports(LinuxDesktopCapability.DebianAlternatives));
    }

    [Fact]
    public void PortalOpenFailureFallsBackToXdgOpen()
    {
        var runner = new RecordingRunner(
            DesktopCommandResult.Failure("portal unavailable", 1),
            DesktopCommandResult.Success());
        var launcher = new PlatformLauncher(
            DesktopPlatform.Linux,
            linuxCapabilities: Capabilities(
                LinuxDesktopCapability.PortalOpen | LinuxDesktopCapability.XdgOpen),
            commandRunner: runner);

        launcher.Open("https://example.com/a path?q='quoted'");

        Assert.Collection(
            runner.Commands,
            command =>
            {
                Assert.Equal("gdbus", command.FileName);
                Assert.Contains("https://example.com/a%20path?q='quoted'", command.ArgumentList);
            },
            command =>
            {
                Assert.Equal("xdg-open", command.FileName);
                Assert.Equal(["https://example.com/a path?q='quoted'"], command.ArgumentList);
            });
    }

    [Fact]
    public void PortalNotificationUsesArgumentListAndEscapesGVariantText()
    {
        var startInfo = LinuxDesktopIntegration.CreatePortalNotificationStartInfo(
            "Build's result",
            "line 1\nline 2\\done");

        Assert.Equal("gdbus", startInfo.FileName);
        Assert.False(startInfo.UseShellExecute);
        Assert.Contains(
            "{'title': <'Build\\'s result'>, 'body': <'line 1\\nline 2\\\\done'>, 'priority': <'normal'>}",
            startInfo.ArgumentList);
    }

    [Fact]
    public void NotificationFailureIncludesEveryProviderDiagnostic()
    {
        var runner = new RecordingRunner(
            DesktopCommandResult.Failure("portal rejected the call", 1),
            DesktopCommandResult.Failure("notification daemon unavailable", 1));
        var launcher = new PlatformLauncher(
            DesktopPlatform.Linux,
            linuxCapabilities: Capabilities(
                LinuxDesktopCapability.PortalNotifications | LinuxDesktopCapability.NotifySend),
            commandRunner: runner);

        var result = launcher.ShowNotification("Build", "Failed");

        Assert.True(result.Attempted);
        Assert.False(result.Succeeded);
        Assert.Contains("portal rejected", result.Diagnostic);
        Assert.Contains("notification daemon", result.Diagnostic);
    }

    [Fact]
    public void MissingProvidersProduceActionableDiagnostics()
    {
        var launcher = new PlatformLauncher(
            DesktopPlatform.Linux,
            linuxCapabilities: Capabilities(LinuxDesktopCapability.None));

        var openError = Assert.Throws<InvalidOperationException>(
            () => launcher.Open("https://example.com"));
        var notification = launcher.ShowNotification("Build", "Complete");
        var report = launcher.GetCapabilityReport();

        Assert.Contains("Install xdg-utils", openError.Message);
        Assert.Contains("install notify-send", notification.Diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Open URI/file/directory: unavailable", report);
        Assert.Contains("Global summon/desktop shortcut portal: unsupported", report);
    }

    [Fact]
    public void CommandLocatorIgnoresEmptyPathEntries()
    {
        var separator = Path.PathSeparator.ToString();
        var visited = new List<string>();

        var found = LinuxCommandLocator.IsAvailable(
            "xdg-open",
            $"{separator}/usr/bin{separator}{separator}/bin",
            candidate =>
            {
                visited.Add(candidate);
                return candidate == Path.Combine("/bin", "xdg-open");
            });

        Assert.True(found);
        Assert.DoesNotContain("xdg-open", visited);
    }

    [Theory]
    [InlineData("wt-dotnet:")]
    [InlineData("wt-dotnet:open")]
    [InlineData("wt-dotnet://new-tab")]
    public void LinuxProtocolActivationOpensADefaultTab(string activation)
    {
        var recognized = LinuxDesktopIntegration.TryNormalizeProtocolActivation(
            [activation],
            out var normalized,
            out var error);

        Assert.True(recognized);
        Assert.Empty(normalized);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("wt-dotnet://run?command=calc")]
    [InlineData("wt-dotnet://open#fragment")]
    public void LinuxProtocolActivationRejectsExecutableOrAmbiguousPayloads(string activation)
    {
        var recognized = LinuxDesktopIntegration.TryNormalizeProtocolActivation(
            [activation],
            out _,
            out var error);

        Assert.True(recognized);
        Assert.Contains("Unsupported wt-dotnet URI activation", error);
    }

    private static LinuxDesktopCapabilities Capabilities(
        LinuxDesktopCapability available) =>
        new(available, "test", []);

    private sealed class RecordingRunner(params DesktopCommandResult[] results)
        : IDesktopCommandRunner
    {
        private int _next;

        public List<ProcessStartInfo> Commands { get; } = [];

        public DesktopCommandResult Run(ProcessStartInfo startInfo, TimeSpan timeout)
        {
            Assert.Equal(TimeSpan.FromSeconds(3), timeout);
            Commands.Add(startInfo);
            return results[_next++];
        }
    }

    private sealed class RecordingWindowsShell : IWindowsShellIntegrationService
    {
        public SystemToastRequest? Toast { get; private set; }
        public IReadOnlyList<JumpListProfile>? Profiles { get; private set; }

        public ShellIntegrationResult GetCapabilities() => Success();

        public ShellIntegrationResult RefreshJumpList(IEnumerable<JumpListProfile> profiles)
        {
            Profiles = profiles.ToArray();
            return Success();
        }

        public ShellIntegrationResult PublishToast(SystemToastRequest request)
        {
            Toast = request;
            return Success();
        }

        public ShellIntegrationResult DiagnoseDefaultTerminalDelegation() =>
            ShellIntegrationResult.Unsupported("not bundled");

        private static ShellIntegrationResult Success() =>
            new(ShellIntegrationStatus.Success, "ready");
    }
}
