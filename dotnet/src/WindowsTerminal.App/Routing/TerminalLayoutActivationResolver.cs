using Microsoft.Terminal.Settings;
using WindowsTerminal.Models;

namespace WindowsTerminal.Routing;

public static class TerminalLayoutActivationResolver
{
    public static TerminalWindowActivation ResolveSavedSlot(
        ApplicationStateStore store,
        int index,
        TerminalWindowActivation fallback)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(fallback);
        var state = TerminalLayoutStateStore.ReadSlot(store, index);
        if (state is null)
        {
            return fallback with
            {
                PersistedLayoutDiagnostic =
                    $"Persisted layout slot '{index}' does not exist.",
            };
        }

        return TerminalLayoutStateStore.TryRead(state, out _, out var diagnostic)
            ? fallback with { PersistedLayout = state }
            : fallback with { PersistedLayoutDiagnostic = diagnostic };
    }

    public static TerminalWindowActivation ResolveWorkspace(
        ApplicationStateStore store,
        string name,
        TerminalWindowActivation fallback)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(fallback);
        fallback = fallback with { WorkspaceName = name };
        var saved = store.GetWorkspace(name);
        if (saved is null)
        {
            return fallback;
        }

        if (!TerminalLayoutStateStore.TryRead(saved, out _, out var diagnostic))
        {
            return fallback with { PersistedLayoutDiagnostic = diagnostic };
        }

        var claimed = store.TakeWorkspace(
            name,
            state => TerminalLayoutStateStore.TryRead(state, out _, out _));
        return claimed is null
            ? fallback
            : fallback with { PersistedLayout = claimed };
    }
}
