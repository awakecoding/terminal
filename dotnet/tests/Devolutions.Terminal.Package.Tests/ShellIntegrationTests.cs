using System.Text;
using Devolutions.Terminal.Interop;
using Devolutions.Terminal.Package;
using Xunit;

namespace Devolutions.Terminal.Package.Tests;

public sealed class ShellIntegrationTests
{
    [Fact]
    public void ToastActivationRoundTripsAndRejectsUntrustedPayloads()
    {
        var encoded = ToastActivationCodec.Create(
            "use-any",
            "00112233445566778899aabbccddeeff");

        Assert.True(ToastActivationCodec.TryParse(encoded, out var payload, out var error), error);
        Assert.Equal(ToastActivationCodec.ProtocolVersion, payload!.ProtocolVersion);
        Assert.Equal("use-any", payload.TargetWindow);
        Assert.Equal("focus", payload.Action);
        Assert.False(ToastActivationCodec.TryParse(encoded + "!", out _, out var invalidError));
        Assert.Contains("invalid", invalidError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HelperResponseRequiresVersionAndMapsCapabilities()
    {
        var diagnostic = Convert.ToBase64String(Encoding.UTF8.GetBytes("ready"))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        var response = WindowsShellIntegrationClient.ParseResponse(
            $"protocol=1\nstatus=success\ndiagnostic={diagnostic}\n" +
            "capability=explorer-command.v1\ncapability=jump-list.v1\n" +
            "capability=toast.v1\nend=1\n");

        Assert.True(response.Succeeded);
        Assert.Equal("ready", response.Diagnostic);
        Assert.Equal(
            ShellIntegrationCapability.ExplorerCommand |
            ShellIntegrationCapability.JumpList |
            ShellIntegrationCapability.SystemToast,
            response.Capabilities);

        var mismatch = WindowsShellIntegrationClient.ParseResponse(
            $"protocol=2\nstatus=success\ndiagnostic={diagnostic}\nend=1\n");
        Assert.Equal(ShellIntegrationStatus.VersionMismatch, mismatch.Status);
    }

    [Fact]
    public void PackageEnvironmentMapsOnlyAdvertisedHelperCapabilities()
    {
        var shell = new ShellIntegrationResult(
            ShellIntegrationStatus.Success,
            "ready",
            ShellIntegrationCapability.ExplorerCommand |
            ShellIntegrationCapability.JumpList |
            ShellIntegrationCapability.SystemToast);

        var environment = PackageEnvironment.FromIdentity(
            PackageIdentity.Packaged("package"),
            shell);

        Assert.True(environment.Supports(PackageCapability.ShellVerb));
        Assert.True(environment.Supports(PackageCapability.JumpList));
        Assert.True(environment.Supports(PackageCapability.Notifications));
        Assert.False(environment.Supports(PackageCapability.DefaultTerminal));
        Assert.Contains("OpenConsole handoff v3", environment.GetUnavailableReason(PackageCapability.DefaultTerminal));
    }

    [Fact]
    public void ClientAuthenticatesOneShotHelperWithoutCommandLineSecrets()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var helperPath = Path.GetTempFileName();
        try
        {
            var runner = new RecordingRunner();
            var client = new WindowsShellIntegrationClient(
                runner,
                helperPath,
                () => PackageIdentity.Packaged("package"),
                () => "Devolutions.Terminal_test",
                _ => null);

            var result = client.GetCapabilities();

            Assert.True(result.Succeeded);
            Assert.NotNull(runner.Invocation);
            Assert.Contains($"auth={runner.Invocation.AuthenticationToken}\n", runner.Invocation.Request);
            Assert.DoesNotContain(runner.Invocation.AuthenticationToken, runner.Invocation.ExecutablePath);
            Assert.DoesNotContain(" --", runner.Invocation.ExecutablePath);
        }
        finally
        {
            File.Delete(helperPath);
        }
    }

    [Fact]
    public void JumpListRejectsInvalidProfilesBeforeStartingHelper()
    {
        var runner = new RecordingRunner();
        var client = new WindowsShellIntegrationClient(
            runner,
            "missing",
            () => PackageIdentity.Packaged("package"),
            () => "family",
            _ => null);

        var result = client.RefreshJumpList([new JumpListProfile("Broken", "not-a-guid")]);

        Assert.Equal(ShellIntegrationStatus.InvalidRequest, result.Status);
        Assert.Null(runner.Invocation);
    }

    private sealed class RecordingRunner : IShellHelperProcessRunner
    {
        public ShellHelperInvocation? Invocation { get; private set; }

        public ShellHelperProcessResult Run(ShellHelperInvocation invocation)
        {
            Invocation = invocation;
            var diagnostic = Convert.ToBase64String(Encoding.UTF8.GetBytes("ready"))
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
            return new(
                true,
                0,
                $"protocol=1\nstatus=success\ndiagnostic={diagnostic}\n" +
                "capability=explorer-command.v1\ncapability=jump-list.v1\n" +
                "capability=toast.v1\nend=1\n",
                string.Empty);
        }
    }
}
