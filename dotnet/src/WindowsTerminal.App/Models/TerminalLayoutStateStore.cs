using Microsoft.Terminal.Settings;

namespace WindowsTerminal.Models;

public static class TerminalLayoutStateStore
{
    public static WindowLayoutState? ReadWindowState(ApplicationStateStore store, int windowId)
    {
        ArgumentNullException.ThrowIfNull(store);
        var index = Math.Max(0, windowId - 1);
        return index < store.Data.PersistedWindowLayouts.Count
            ? store.Data.PersistedWindowLayouts[index]
            : null;
    }

    public static TerminalWindowLayoutDescriptor? ReadWindow(ApplicationStateStore store, int windowId)
    {
        var saved = ReadWindowState(store, windowId);
        return saved is null ? null : TerminalLayoutSerializer.DeserializeTabs(saved.TabLayout);
    }

    public static void SaveWindow(
        ApplicationStateStore store,
        int windowId,
        TerminalWindowLayoutDescriptor layout,
        string? position,
        WindowSizeState? size,
        LaunchMode launchMode)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(layout);
        var index = Math.Max(0, windowId - 1);
        store.SavePersistedWindowLayout(index, TerminalLayoutSerializer.ToApplicationState(
            layout,
            position,
            size,
            launchMode));
    }
}
