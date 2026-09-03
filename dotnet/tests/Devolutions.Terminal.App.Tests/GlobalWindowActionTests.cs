using Devolutions.Terminal.Settings;
using Devolutions.Terminal.App.Platform;
using Devolutions.Terminal.App.Routing;
using Xunit;

namespace Devolutions.Terminal.App.Tests;

public sealed class GlobalWindowActionTests
{
    [Fact]
    public async Task NamedSummonRoutesToExistingWindow()
    {
        var created = new List<FakeTarget>();
        var router = new GlobalWindowActionRouter(name =>
        {
            var target = new FakeTarget(created.Count + 2, name);
            created.Add(target);
            return target;
        });
        var first = new FakeTarget(1, "alpha");
        var second = new FakeTarget(2, "build");
        router.Add(first);
        router.Add(second);

        var result = await router.SummonAsync(
            first,
            new GlobalSummonArgs(Name: "BUILD"));

        Assert.True(result.Succeeded);
        Assert.Equal(0, first.SummonCount);
        Assert.Equal(1, second.SummonCount);
        Assert.Empty(created);
    }

    [Fact]
    public async Task MissingNamedSummonCreatesBrokerTrackedWindow()
    {
        FakeTarget? created = null;
        var router = new GlobalWindowActionRouter(name =>
            created = new FakeTarget(3, name));

        var result = await router.SummonAsync(
            null,
            new GlobalSummonArgs(Name: "_quake", DropdownDuration: 250));

        Assert.True(result.Succeeded);
        Assert.NotNull(created);
        Assert.Equal("_quake", created.WindowName);
        Assert.True(created.LastQuake);
    }

    [Fact]
    public async Task ToggleVisibilityHidesOnlyActiveVisibleWindow()
    {
        var operations = new FakeOperations
        {
            IsWindowVisible = true,
            IsWindowActive = true,
            CurrentBounds = new(25, 25, 600, 400),
        };

        var result = await new WindowSummonController(operations).SummonAsync(
            new GlobalSummonArgs(ToggleVisibility: true),
            quake: false);

        Assert.True(result.Succeeded);
        Assert.True(operations.Hidden);
        Assert.False(operations.Shown);
        Assert.False(operations.Activated);
    }

    [Fact]
    public async Task QuakeUsesMouseMonitorTopEdgeAndBoundedDuration()
    {
        var operations = new FakeOperations
        {
            CurrentBounds = new(-2000, -1000, 900, 700),
            MouseMonitor = new("right", new(1920, 40, 1600, 1000)),
        };

        var result = await new WindowSummonController(operations).SummonAsync(
            new GlobalSummonArgs(
                Monitor: MonitorBehavior.ToMouse,
                DropdownDuration: 9000),
            quake: true);

        Assert.True(result.Succeeded);
        Assert.Equal(MonitorBehavior.ToMouse, operations.RequestedMonitor);
        Assert.Equal(new WindowPixelRect(1920, 40, 1600, 500), operations.ShownBounds);
        Assert.Equal(2000u, operations.Duration);
        Assert.True(operations.Activated);
    }

    [Fact]
    public async Task OnCurrentDoesNotRevealWindowOnOtherDesktop()
    {
        var operations = new FakeOperations
        {
            DesktopPresence = DesktopPresence.Other,
        };

        var result = await new WindowSummonController(operations).SummonAsync(
            new GlobalSummonArgs(Desktop: DesktopBehavior.OnCurrent),
            quake: false);

        Assert.False(result.Succeeded);
        Assert.False(operations.Shown);
        Assert.False(operations.Activated);
    }

    [Fact]
    public void QuakeGeometryIsClampedForSmallMonitor()
    {
        var result = WindowSummonGeometry.Place(
            new(0, 0, 800, 600),
            new MonitorGeometry("small", new(10, 20, 300, 180)),
            quake: true);

        Assert.Equal(new WindowPixelRect(10, 20, 300, 180), result);
    }

    [Fact]
    public void DuplicateGlobalHotkeysAreRejectedBeforePlatformRegistration()
    {
        using var backend = new FakeHotkeyBackend();
        using var manager = new GlobalHotkeyManager(backend, static _ => ValueTask.CompletedTask);
        var chord = KeyChord.Parse("win+backtick");

        var results = manager.Apply(
        [
            new(chord, new GlobalSummonArgs(Name: "one")),
            new(chord, new GlobalSummonArgs(Name: "two")),
        ]);

        var result = Assert.Single(results);
        Assert.Equal(GlobalHotkeyRegistrationStatus.Collision, result.Status);
        Assert.Empty(backend.Registered);
    }

    [Fact]
    public void SettingsChangeReplacesRemovedHotkeysWithoutLeakingRegistration()
    {
        using var backend = new FakeHotkeyBackend();
        using var manager = new GlobalHotkeyManager(backend, static _ => ValueTask.CompletedTask);
        var oldChord = KeyChord.Parse("win+backtick");
        var newChord = KeyChord.Parse("win+f12");

        manager.Apply([new(oldChord, new GlobalSummonArgs(Name: "_quake"))]);
        manager.Apply([new(newChord, new GlobalSummonArgs(Name: "_quake"))]);

        Assert.Equal([oldChord, newChord], backend.Registered);
        Assert.Equal([oldChord], backend.Disposed);
    }

    [Fact]
    public void CollidingSettingsChangeKeepsPreviousWorkingRegistration()
    {
        using var backend = new FakeHotkeyBackend();
        using var manager = new GlobalHotkeyManager(backend, static _ => ValueTask.CompletedTask);
        var oldChord = KeyChord.Parse("win+backtick");
        var collidingChord = KeyChord.Parse("win+f12");
        manager.Apply([new(oldChord, new GlobalSummonArgs(Name: "_quake"))]);
        backend.Failures.Add(collidingChord);

        var results = manager.Apply(
            [new(collidingChord, new GlobalSummonArgs(Name: "_quake"))]);

        Assert.Equal(GlobalHotkeyRegistrationStatus.Collision, Assert.Single(results).Status);
        Assert.DoesNotContain(oldChord, backend.Disposed);
    }

    [Fact]
    public void DispatchIsSafeWhileBindingsAreReappliedAndDisposed()
    {
        using var backend = new FakeHotkeyBackend();
        var names = new System.Collections.Concurrent.ConcurrentBag<string>();
        using var manager = new GlobalHotkeyManager(
            backend,
            args =>
            {
                names.Add(args.Name);
                return ValueTask.CompletedTask;
            });
        var chord = KeyChord.Parse("win+backtick");
        manager.Apply([new(chord, new GlobalSummonArgs(Name: "one"))]);

        Parallel.Invoke(
            () =>
            {
                for (var index = 0; index < 2_000; index++)
                {
                    manager.Apply(
                    [
                        new(chord, new GlobalSummonArgs(
                            Name: (index & 1) == 0 ? "one" : "two")),
                    ]);
                }
            },
            () =>
            {
                for (var index = 0; index < 2_000; index++)
                {
                    backend.Invoke(chord);
                }
            });

        Assert.NotEmpty(names);
        Assert.All(names, name => Assert.Contains(name, new[] { "one", "two" }));
    }

    [Fact]
    public void BackendCallbackDuringRegistrationCannotDeadlockConfiguration()
    {
        using var backend = new FakeHotkeyBackend
        {
            InvokeDuringRegistration = true,
        };
        using var manager = new GlobalHotkeyManager(
            backend,
            static _ => ValueTask.CompletedTask);

        var result = Assert.Single(manager.Apply(
        [
            new(
                KeyChord.Parse("win+backtick"),
                new GlobalSummonArgs(Name: "_quake")),
        ]));

        Assert.Equal(GlobalHotkeyRegistrationStatus.Registered, result.Status);
        Assert.True(backend.RegistrationCallbackCompleted);
    }

    [Fact]
    public void LinuxGlobalShortcutStatusIsExplicitAndActionable()
    {
        using var backend = new UnsupportedGlobalHotkeyBackend(
            "No GlobalShortcuts portal session provider is bundled; invoke wt -w quake.");

        var result = backend.Register(
            KeyChord.Parse("win+backtick"),
            static () => { });

        Assert.Equal(GlobalHotkeyRegistrationStatus.Unsupported, result.Status);
        Assert.Contains("portal", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("wt -w", result.Diagnostic, StringComparison.Ordinal);
    }

    private sealed class FakeTarget(int windowId, string windowName) : IGlobalWindowActionTarget
    {
        public int WindowId { get; } = windowId;
        public string WindowName { get; } = windowName;
        public int SummonCount { get; private set; }
        public bool LastQuake { get; private set; }

        public ValueTask<WindowActionResult> ApplySummonAsync(
            GlobalSummonArgs args,
            bool quake,
            CancellationToken cancellationToken)
        {
            SummonCount++;
            LastQuake = quake;
            return ValueTask.FromResult(WindowActionResult.Success("summoned"));
        }
    }

    private sealed class FakeOperations : IWindowSummonOperations
    {
        public bool IsWindowVisible { get; set; }
        public bool IsWindowActive { get; set; }
        public bool IsWindowMinimized { get; set; }
        public DesktopPresence DesktopPresence { get; set; } = DesktopPresence.Current;
        public WindowPixelRect CurrentBounds { get; set; } = new(0, 0, 800, 600);
        public MonitorGeometry MouseMonitor { get; set; } =
            new("primary", new(0, 0, 1920, 1080));
        public MonitorBehavior RequestedMonitor { get; private set; }
        public bool Hidden { get; private set; }
        public bool Shown { get; private set; }
        public bool Activated { get; private set; }
        public WindowPixelRect ShownBounds { get; private set; }
        public uint Duration { get; private set; }

        public MonitorGeometry GetMonitor(MonitorBehavior behavior)
        {
            RequestedMonitor = behavior;
            return MouseMonitor;
        }

        public WindowActionResult MoveToCurrentDesktop() =>
            WindowActionResult.Success("moved");

        public void HideWindow() => Hidden = true;

        public ValueTask ShowWindowAsync(
            WindowPixelRect bounds,
            uint dropdownDuration,
            CancellationToken cancellationToken)
        {
            Shown = true;
            ShownBounds = bounds;
            Duration = dropdownDuration;
            return ValueTask.CompletedTask;
        }

        public void ActivateWindow() => Activated = true;
    }

    private sealed class FakeHotkeyBackend : IGlobalHotkeyBackend
    {
        private readonly Dictionary<KeyChord, IGlobalHotkeyRegistration> _pending = [];
        private readonly Dictionary<KeyChord, Action> _callbacks = [];
        private readonly object _gate = new();

        public List<KeyChord> Registered { get; } = [];
        public List<KeyChord> Disposed { get; } = [];
        public HashSet<KeyChord> Failures { get; } = [];
        public bool InvokeDuringRegistration { get; init; }
        public bool RegistrationCallbackCompleted { get; private set; }

        public GlobalHotkeyRegistrationResult Register(KeyChord chord, Action activated)
        {
            lock (_gate)
            {
                Registered.Add(chord);
                if (Failures.Contains(chord))
                {
                    return new(
                        chord,
                        GlobalHotkeyRegistrationStatus.Collision,
                        "already registered");
                }
                _pending.Add(chord, new Registration(chord, Disposed));
                _callbacks[chord] = activated;
                if (InvokeDuringRegistration)
                {
                    RegistrationCallbackCompleted = Task.Run(activated)
                        .Wait(TimeSpan.FromSeconds(2));
                }
                return new(
                    chord,
                    GlobalHotkeyRegistrationStatus.Registered,
                    "registered");
            }
        }

        public IGlobalHotkeyRegistration? TakeRegistration(KeyChord chord)
        {
            lock (_gate)
            {
                return _pending.Remove(chord, out var registration) ? registration : null;
            }
        }

        public void Invoke(KeyChord chord)
        {
            Action? callback;
            lock (_gate)
            {
                callback = _callbacks.GetValueOrDefault(chord);
            }
            callback?.Invoke();
        }

        public void Dispose() { }

        private sealed class Registration(
            KeyChord chord,
            ICollection<KeyChord> disposed) : IGlobalHotkeyRegistration
        {
            private bool _disposed;

            public void Dispose()
            {
                if (!_disposed)
                {
                    disposed.Add(chord);
                    _disposed = true;
                }
            }
        }
    }
}
