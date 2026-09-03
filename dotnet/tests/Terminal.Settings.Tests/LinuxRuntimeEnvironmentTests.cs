using Microsoft.Terminal.Settings;
using Xunit;

namespace Terminal.Settings.Tests;

public sealed class LinuxRuntimeEnvironmentTests
{
    [Fact]
    public void SettingsPathsUseLinuxXdgDirectories()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var configRoot = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var stateRoot = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        configRoot = string.IsNullOrWhiteSpace(configRoot)
            ? Path.Combine(home, ".config")
            : configRoot;
        stateRoot = string.IsNullOrWhiteSpace(stateRoot)
            ? Path.Combine(home, ".local", "state")
            : stateRoot;

        Assert.Equal(
            Path.Combine(configRoot, "windows-terminal-dotnet"),
            SettingsService.SettingsDirectory);
        Assert.Equal(
            Path.Combine(stateRoot, "windows-terminal-dotnet"),
            SettingsService.StateDirectory);
    }

    [Fact]
    public async Task NativeLinuxProfileDiscoveryFindsExecutableShells()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var result = await DynamicProfileManager.CreateDefault().GenerateAsync(
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEmpty(result.Profiles);
        Assert.All(result.Profiles, profile =>
        {
            Assert.Equal(DynamicProfileSource.Linux, profile.Source);
            Assert.True(File.Exists(profile.Commandline), profile.Commandline);
        });
    }
}
