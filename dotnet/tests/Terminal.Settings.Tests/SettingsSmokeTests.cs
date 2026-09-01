using Microsoft.Terminal.Settings;
using Xunit;

namespace Terminal.Settings.Tests;

public sealed class SettingsSmokeTests
{
    [Fact]
    public void DefaultsContainAProfile()
    {
        var settings = SettingsService.CreateDefault();

        Assert.NotEmpty(settings.Profiles);
        Assert.NotNull(settings.GetDefaultProfile());
    }
}
