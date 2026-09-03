using Devolutions.Terminal.Core;
using Devolutions.Terminal.Ghostty;
using Devolutions.Terminal.Settings;

namespace Devolutions.Terminal.App.Connections;

public static class TerminalEngineFactory
{
    public static ITerminalEngine Create(AppSettings settings, ProfileSettings profile)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(profile);
        return (profile.TerminalEngine ??
                settings.ProfileDefaults.TerminalEngine ??
                settings.TerminalEngine) switch
        {
            TerminalEngineKind.Ghostty => new GhosttyTerminalEngine(
                historySize: profile.HistorySize),
            _ => new TerminalEngine(historySize: profile.HistorySize),
        };
    }
}
