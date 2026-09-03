using Devolutions.Terminal.App.Platform;
using Devolutions.Terminal.Settings;
using Xunit;

namespace Devolutions.Terminal.App.Tests;

public sealed class WindowChromeTests
{
    [Fact]
    public void HidesSingleTabWhenAlwaysShowTabsIsFalse()
    {
        var settings = new AppSettings { AlwaysShowTabs = false };
        Assert.False(WindowChrome.ShouldShowTabRow(settings, tabCount: 1, fullscreen: false));
        Assert.True(WindowChrome.ShouldShowTabRow(settings, tabCount: 2, fullscreen: false));
    }

    [Fact]
    public void AlwaysShowTabsKeepsSingleTabVisible()
    {
        var settings = new AppSettings { AlwaysShowTabs = true };
        Assert.True(WindowChrome.ShouldShowTabRow(settings, tabCount: 1, fullscreen: false));
    }

    [Fact]
    public void HidesTabsInFullscreenUnlessEnabled()
    {
        var settings = new AppSettings { AlwaysShowTabs = true, ShowTabsFullscreen = false };
        Assert.False(WindowChrome.ShouldShowTabRow(settings, tabCount: 2, fullscreen: true));
        settings.ShowTabsFullscreen = true;
        Assert.True(WindowChrome.ShouldShowTabRow(settings, tabCount: 2, fullscreen: true));
    }

    [Fact]
    public void EmbeddedWindowsDoNotUseCustomTitlebar()
    {
        var settings = new AppSettings { ShowTabsInTitlebar = true };
        Assert.False(WindowChrome.ShouldUseCustomTitlebar(settings, embedded: true));
        Assert.True(WindowChrome.ShouldUseCustomTitlebar(settings, embedded: false));
        settings.ShowTabsInTitlebar = false;
        Assert.False(WindowChrome.ShouldUseCustomTitlebar(settings, embedded: false));
    }
}
