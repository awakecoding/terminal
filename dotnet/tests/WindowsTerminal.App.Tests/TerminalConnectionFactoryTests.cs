using Microsoft.Terminal.Connection;
using Microsoft.Terminal.Settings;
using System.Runtime.Versioning;
using WindowsTerminal.Connections;
using Xunit;

namespace WindowsTerminal.App.Tests;

[SupportedOSPlatform("windows")]
public sealed class TerminalConnectionFactoryTests
{
    private static readonly AzureCloudShellAuthenticationCallbacks Callbacks = new()
    {
        ShowDeviceCodeAsync = static (_, _) => ValueTask.CompletedTask,
    };

    [Fact]
    public async Task CreatesPlatformConnectionForLocalProfile()
    {
        var factory = new TerminalConnectionFactory(Callbacks);

        await using var connection = factory.Create(ProfileSettings.CreateCmd());

        if (OperatingSystem.IsWindows())
        {
            Assert.IsType<ConPtyConnection>(connection);
        }
        else if (OperatingSystem.IsLinux())
        {
            Assert.IsType<LinuxPtyConnection>(connection);
        }
        else
        {
            Assert.IsAssignableFrom<ITerminalConnection>(connection);
        }
    }

    [Fact]
    public void AzureProfileRequiresConfiguredPublicClient()
    {
        var previous = Environment.GetEnvironmentVariable(
            TerminalConnectionFactory.AzureClientIdEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(
                TerminalConnectionFactory.AzureClientIdEnvironmentVariable,
                null);
            var profile = new ProfileSettings
            {
                Name = "Azure",
                ConnectionType = AzureCloudShellConnection.ConnectionTypeGuid.ToString("B"),
            };

            var error = Assert.Throws<InvalidOperationException>(
                () => new TerminalConnectionFactory(Callbacks).Create(profile));

            Assert.Contains(
                TerminalConnectionFactory.AzureClientIdEnvironmentVariable,
                error.Message,
                StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                TerminalConnectionFactory.AzureClientIdEnvironmentVariable,
                previous);
        }
    }

    [Fact]
    public async Task CreatesAzureConnectionWithConfiguredPublicClient()
    {
        var previous = Environment.GetEnvironmentVariable(
            TerminalConnectionFactory.AzureClientIdEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(
                TerminalConnectionFactory.AzureClientIdEnvironmentVariable,
                "11111111-1111-1111-1111-111111111111");
            var profile = new ProfileSettings
            {
                Name = "Azure",
                ConnectionType = AzureCloudShellConnection.ConnectionTypeGuid.ToString("B"),
            };
            var factory = new TerminalConnectionFactory(Callbacks);

            await using var connection = factory.Create(profile);

            Assert.IsType<AzureCloudShellConnection>(connection);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                TerminalConnectionFactory.AzureClientIdEnvironmentVariable,
                previous);
        }
    }
}
