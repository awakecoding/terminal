using Devolutions.Terminal.Settings;

namespace Devolutions.Terminal.App.Platform;

public static class WindowChrome
{
    public static bool ShouldShowTabRow(
        AppSettings settings,
        int tabCount,
        bool fullscreen)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (fullscreen && !settings.ShowTabsFullscreen)
        {
            return false;
        }

        return settings.AlwaysShowTabs || tabCount > 1;
    }

    public static bool ShouldUseCustomTitlebar(AppSettings settings, bool embedded)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return !embedded && settings.ShowTabsInTitlebar;
    }
}
