using System.Diagnostics;
using Microsoft.Terminal.Settings;

namespace WindowsTerminal.Platform;

public enum GlobalHotkeyRegistrationStatus
{
    Registered,
    Unsupported,
    Collision,
    Invalid,
}

public sealed record GlobalHotkeyRegistrationResult(
    KeyChord Chord,
    GlobalHotkeyRegistrationStatus Status,
    string Diagnostic);

public sealed record GlobalHotkeyBinding(
    KeyChord Chord,
    GlobalSummonArgs Args);

public interface IGlobalHotkeyRegistration : IDisposable;

public interface IGlobalHotkeyBackend : IDisposable
{
    GlobalHotkeyRegistrationResult Register(KeyChord chord, Action activated);
    IGlobalHotkeyRegistration? TakeRegistration(KeyChord chord);
}

public sealed class GlobalHotkeyManager(
    IGlobalHotkeyBackend backend,
    Func<GlobalSummonArgs, ValueTask> activated) : IDisposable
{
    private readonly IGlobalHotkeyBackend _backend =
        backend ?? throw new ArgumentNullException(nameof(backend));
    private readonly Func<GlobalSummonArgs, ValueTask> _activated =
        activated ?? throw new ArgumentNullException(nameof(activated));
    private readonly object _configurationGate = new();
    private readonly object _gate = new();
    private readonly Dictionary<KeyChord, ActiveBinding> _active = [];
    private IReadOnlyList<GlobalHotkeyRegistrationResult> _lastResults = [];
    private bool _disposed;

    public IReadOnlyList<GlobalHotkeyRegistrationResult> LastResults
    {
        get
        {
            lock (_gate)
            {
                return _lastResults;
            }
        }
    }

    public IReadOnlyList<GlobalHotkeyRegistrationResult> Apply(ActionMap actionMap)
    {
        ArgumentNullException.ThrowIfNull(actionMap);
        return Apply(actionMap.KeyBindings
            .Where(static pair => pair.Value.ActionAndArgs?.Action is
                ShortcutAction.GlobalSummon or ShortcutAction.QuakeMode)
            .Select(static pair =>
            {
                var action = pair.Value.ActionAndArgs!;
                var args = action.Args as GlobalSummonArgs ?? new GlobalSummonArgs();
                if (action.Action == ShortcutAction.QuakeMode)
                {
                    args = args with
                    {
                        Name = "_quake",
                        DropdownDuration = args.DropdownDuration == 0 ? 200u : args.DropdownDuration,
                    };
                }
                return new GlobalHotkeyBinding(pair.Key, args);
            }));
    }

    public IReadOnlyList<GlobalHotkeyRegistrationResult> Apply(
        IEnumerable<GlobalHotkeyBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        var requested = bindings.ToArray();
        lock (_configurationGate)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
            }

            return ApplyLocked(requested);
        }
    }

    private IReadOnlyList<GlobalHotkeyRegistrationResult> ApplyLocked(
        GlobalHotkeyBinding[] requested)
    {
        var results = new List<GlobalHotkeyRegistrationResult>();
        var duplicates = requested
            .GroupBy(static binding => binding.Chord)
            .Where(static group => group.Count() > 1)
            .Select(static group => group.Key)
            .ToHashSet();
        foreach (var duplicate in duplicates)
        {
            results.Add(new(
                duplicate,
                GlobalHotkeyRegistrationStatus.Collision,
                $"Global hotkey '{duplicate}' is assigned to more than one summon action."));
        }
        if (duplicates.Count > 0)
        {
            lock (_gate)
            {
                _lastResults = results;
            }

            return results;
        }

        var desired = requested
            .ToDictionary(static binding => binding.Chord);
        Dictionary<KeyChord, ActiveBinding> active;
        lock (_gate)
        {
            active = new Dictionary<KeyChord, ActiveBinding>(_active);
        }

        var staged = new List<(KeyChord Chord, GlobalSummonArgs Args, IGlobalHotkeyRegistration Registration)>();
        var existingUpdates = new List<(ActiveBinding Binding, GlobalSummonArgs Args)>();
        var failed = false;
        foreach (var pair in desired)
        {
            if (active.TryGetValue(pair.Key, out var existing))
            {
                existingUpdates.Add((existing, pair.Value.Args));
                results.Add(new(
                    pair.Key,
                    GlobalHotkeyRegistrationStatus.Registered,
                    $"Global hotkey '{pair.Key}' remains registered."));
                continue;
            }

            var result = _backend.Register(
                pair.Key,
                () => Dispatch(pair.Key));
            results.Add(result);
            if (result.Status == GlobalHotkeyRegistrationStatus.Registered &&
                _backend.TakeRegistration(pair.Key) is { } registration)
            {
                staged.Add((pair.Key, pair.Value.Args, registration));
            }
            else
            {
                failed = true;
            }
        }

        if (failed)
        {
            foreach (var registration in staged)
            {
                registration.Registration.Dispose();
            }
            lock (_gate)
            {
                _lastResults = results;
                return results;
            }
        }

        ActiveBinding[] removedBindings;
        lock (_gate)
        {
            removedBindings = _active
                .Where(pair => !desired.ContainsKey(pair.Key))
                .Select(static pair => pair.Value)
                .ToArray();
            foreach (var removed in _active.Keys.Where(chord => !desired.ContainsKey(chord)).ToArray())
            {
                _active.Remove(removed);
            }
            foreach (var update in existingUpdates)
            {
                update.Binding.Args = update.Args;
            }
            foreach (var registration in staged)
            {
                _active.Add(
                    registration.Chord,
                    new(registration.Args, registration.Registration));
            }

            _lastResults = results;
        }

        foreach (var removed in removedBindings)
        {
            removed.Registration.Dispose();
        }

        return _lastResults;
    }

    private void Dispatch(KeyChord chord)
    {
        GlobalSummonArgs? args;
        lock (_gate)
        {
            args = !_disposed && _active.TryGetValue(chord, out var binding)
                ? binding.Args
                : null;
        }

        if (args is not null)
        {
            _ = DispatchAsync(args);
        }
    }

    private async Task DispatchAsync(GlobalSummonArgs args)
    {
        try
        {
            await _activated(args).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Trace.TraceError($"Global hotkey summon failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        ActiveBinding[] bindings;
        lock (_configurationGate)
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                bindings = _active.Values.ToArray();
                _active.Clear();
            }

            foreach (var binding in bindings)
            {
                binding.Registration.Dispose();
            }
            _backend.Dispose();
        }
    }

    private sealed class ActiveBinding(
        GlobalSummonArgs args,
        IGlobalHotkeyRegistration registration)
    {
        public GlobalSummonArgs Args { get; set; } = args;
        public IGlobalHotkeyRegistration Registration { get; } = registration;
    }
}

public sealed class UnsupportedGlobalHotkeyBackend(string diagnostic) : IGlobalHotkeyBackend
{
    private readonly string _diagnostic =
        string.IsNullOrWhiteSpace(diagnostic)
            ? "Global shortcuts are unavailable on this platform."
            : diagnostic;

    public GlobalHotkeyRegistrationResult Register(KeyChord chord, Action activated) =>
        new(chord, GlobalHotkeyRegistrationStatus.Unsupported, _diagnostic);

    public IGlobalHotkeyRegistration? TakeRegistration(KeyChord chord) => null;
    public void Dispose() { }
}

public static class GlobalHotkeyBackend
{
    public static IGlobalHotkeyBackend CreateDefault() =>
        OperatingSystem.IsWindows()
            ? new WindowsGlobalHotkeyBackend()
            : new UnsupportedGlobalHotkeyBackend(
                OperatingSystem.IsLinux()
                    ? "The freedesktop GlobalShortcuts portal requires an interactive permission session; no reflection-free portal session provider is bundled. Configure a desktop shortcut to invoke 'wt -w <name>' or invoke globalSummon through the broker."
                    : "Global hotkeys are unavailable on this platform; summon remains available through the broker.");
}
