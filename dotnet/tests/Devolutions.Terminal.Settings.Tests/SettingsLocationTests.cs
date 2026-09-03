using Devolutions.Terminal.Settings;
using Xunit;

namespace Devolutions.Terminal.Settings.Tests;

public sealed class SettingsLocationTests
{
    [Fact]
    public void WtBaseSettingsPathOverridesDirectoryOnAllPlatforms()
    {
        var previousBase = Environment.GetEnvironmentVariable(SettingsService.BaseSettingsPathVariable);
        var previousFile = Environment.GetEnvironmentVariable("DTERM_SETTINGS_PATH");
        var previousAlias = Environment.GetEnvironmentVariable("WT_DOTNET_SETTINGS_PATH");
        var root = Path.Combine(Path.GetTempPath(), "dterm-wt-base-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Environment.SetEnvironmentVariable("DTERM_SETTINGS_PATH", null);
            Environment.SetEnvironmentVariable("WT_DOTNET_SETTINGS_PATH", null);
            Environment.SetEnvironmentVariable(SettingsService.BaseSettingsPathVariable, root);
            Assert.Equal(Path.GetFullPath(root), SettingsService.SettingsDirectory);
            Assert.Equal(SettingsService.SettingsDirectory, SettingsService.StateDirectory);
            Assert.Equal(
                Path.Combine(Path.GetFullPath(root), "settings.json"),
                SettingsService.SettingsPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable(SettingsService.BaseSettingsPathVariable, previousBase);
            Environment.SetEnvironmentVariable("DTERM_SETTINGS_PATH", previousFile);
            Environment.SetEnvironmentVariable("WT_DOTNET_SETTINGS_PATH", previousAlias);
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
