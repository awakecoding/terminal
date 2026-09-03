using Microsoft.Terminal.Settings;

namespace WindowsTerminal.Models;

public static class TerminalLayoutStateStore
{
    public static bool IsPersistedLayoutPreference(string? preference) =>
        preference is not null &&
        (preference.Equals("persistedLayout", StringComparison.OrdinalIgnoreCase) ||
         preference.Equals("persistedWindowLayout", StringComparison.OrdinalIgnoreCase) ||
         preference.Equals("persistedLayoutAndContent", StringComparison.OrdinalIgnoreCase));

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

    public static WindowLayoutState? ReadSlot(ApplicationStateStore store, int index)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        return index < store.Data.PersistedWindowLayouts.Count
            ? store.Data.PersistedWindowLayouts[index]
            : null;
    }

    public static bool TryRead(
        WindowLayoutState state,
        out TerminalWindowLayoutDescriptor? layout,
        out string? diagnostic)
    {
        ArgumentNullException.ThrowIfNull(state);
        return TerminalLayoutSerializer.TryDeserializeTabs(
            state.TabLayout,
            out layout,
            out diagnostic);
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

    public static bool TrySaveWindow(
        ApplicationStateStore store,
        int windowId,
        TerminalWindowLayoutDescriptor layout,
        string? position,
        WindowSizeState? size,
        LaunchMode launchMode,
        bool blockedByInvalidRestore)
    {
        if (blockedByInvalidRestore)
        {
            return false;
        }

        SaveWindow(store, windowId, layout, position, size, launchMode);
        return true;
    }
}
