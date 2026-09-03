using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Microsoft.Terminal.Settings;
using WindowsTerminal.Broker;
using WindowsTerminal.Cli;
using WindowsTerminal.Platform;
using WindowsTerminal.Routing;
using WindowsTerminal.Views;

namespace WindowsTerminal;

internal sealed class TerminalWindowRouter : IBrokerRequestHandler, IDisposable
{
    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private readonly Action<MainWindow>? _windowCreated;
    private readonly List<MainWindow> _windows = [];
    private readonly ApplicationStateStore _stateStore;
    private readonly GlobalWindowActionRouter _windowActions;
    private readonly GlobalHotkeyManager _globalHotkeys;
    private int _nextWindowId = 1;

    public TerminalWindowRouter(
        IClassicDesktopStyleApplicationLifetime desktop,
        Action<MainWindow>? windowCreated = null,
        ApplicationStateStore? stateStore = null,
        IGlobalHotkeyBackend? globalHotkeyBackend = null)
    {
        _desktop = desktop;
        _windowCreated = windowCreated;
        _stateStore = stateStore ?? SettingsService.LoadApplicationState();
        _windowActions = new GlobalWindowActionRouter(CreateSummonWindow);
        _globalHotkeys = new GlobalHotkeyManager(
            globalHotkeyBackend ?? GlobalHotkeyBackend.CreateDefault(),
            async args =>
            {
                _ = await OnUiThreadAsync(
                    () => _windowActions.SummonAsync(null, args),
                    CancellationToken.None).ConfigureAwait(false);
            });
    }

    public MainWindow CreateInitial(CliInvocation invocation) =>
        CreateWindow(invocation, WindowNameForNewTarget(invocation.TargetWindow));

    public async ValueTask<BrokerDispatchResult> HandleAsync(
        string targetWindow,
        string payload,
        CancellationToken cancellationToken)
    {
        CliInvocation invocation;
        try
        {
            invocation = CliInvocationSerializer.Deserialize(payload);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
        {
            return new(BrokerStatus.InvalidRequest, $"Invalid activation payload: {ex.Message}");
        }

        return await OnUiThreadAsync(
            () => RouteOnUiThreadAsync(targetWindow, invocation, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<BrokerDispatchResult> RouteOnUiThreadAsync(
        string targetWindow,
        CliInvocation invocation,
        CancellationToken cancellationToken)
    {
        var normalized = string.IsNullOrWhiteSpace(targetWindow)
            ? "use-new"
            : targetWindow.Trim();
        MainWindow? target = normalized.ToLowerInvariant() switch
        {
            "new" or "-1" or "use-new" => null,
            "0" or "use-any" or "use-existing" => _windows.LastOrDefault(),
            _ => FindWindow(normalized),
        };
        if (invocation.SavedLayout is not null)
        {
            target = null;
        }

        if (target is null &&
            invocation.SavedLayout is null &&
            (normalized.Equals("use-existing", StringComparison.OrdinalIgnoreCase) ||
             (int.TryParse(normalized, out var requestedId) && requestedId > 0)))
        {
            return new(BrokerStatus.WindowNotFound, $"Terminal window '{normalized}' was not found.");
        }

        if (target is null && invocation.SaveRequest is not null)
        {
            return new(
                BrokerStatus.WindowNotFound,
                "No terminal window is available to save the requested command.");
        }

        if (target is null)
        {
            target = CreateWindow(invocation, WindowNameForNewTarget(normalized));
            target.Show();
            var startup = await target.InitialActivation.WaitAsync(cancellationToken).ConfigureAwait(true);
            return startup.Succeeded
                ? new(
                    BrokerStatus.Success,
                    "Created and activated a terminal window.",
                    target.WindowId,
                    target.WindowName)
                : new(
                    BrokerStatus.Failed,
                    startup.Message,
                    target.WindowId,
                    target.WindowName);
        }

        var result = await target.ActivateAsync(ToActivation(invocation), cancellationToken).ConfigureAwait(true);
        return result.Succeeded
            ? new(BrokerStatus.Success, result.Message, target.WindowId, target.WindowName)
            : new(BrokerStatus.Failed, result.Message, target.WindowId, target.WindowName);
    }

    private MainWindow CreateWindow(CliInvocation invocation, string name)
        => CreateWindow(PrepareActivation(invocation, name), name);

    private MainWindow CreateWindow(TerminalWindowActivation activation, string name)
    {
        MainWindow? window = null;
        window = new MainWindow(
            _nextWindowId++,
            name,
            activation,
            childActivation =>
            {
                var child = CreateWindow(childActivation, string.Empty);
                child.Show();
            },
            commandLineParser: ParseCommandLine,
            stateStore: _stateStore,
            workspaceRequested: OpenWorkspace,
            windowNameValidator: name => _windows.All(window =>
                !window.WindowName.Equals(name, StringComparison.OrdinalIgnoreCase)),
            windowIdentityProvider: () => _windows.Select(window =>
                    string.IsNullOrWhiteSpace(window.WindowName)
                        ? $"Window {window.WindowId}"
                        : $"{window.WindowName} ({window.WindowId})")
                .ToArray(),
            summonRequested: args => _windowActions.SummonAsync(window, args),
            settingsChanged: settings => TraceHotkeyResults(_globalHotkeys.Apply(settings.ActionMap)));
        _windows.Add(window);
        _windowActions.Add(window);
        _windowCreated?.Invoke(window);
        window.Closed += (_, _) =>
        {
            _windows.Remove(window);
            _windowActions.Remove(window);
            if (ReferenceEquals(_desktop.MainWindow, window))
            {
                _desktop.MainWindow = _windows.FirstOrDefault();
            }
        };
        return window;
    }

    private IGlobalWindowActionTarget CreateSummonWindow(string name)
    {
        var activation = new TerminalWindowActivation(
            null,
            null,
            null,
            null,
            TerminalWindowLaunchMode.Default,
            [new ActionAndArgs(ShortcutAction.NewTab, new NewTabArgs())]);
        return CreateWindow(activation, name);
    }

    private static void TraceHotkeyResults(
        IReadOnlyList<GlobalHotkeyRegistrationResult> results)
    {
        foreach (var result in results.Where(static result =>
                     result.Status != GlobalHotkeyRegistrationStatus.Registered))
        {
            System.Diagnostics.Trace.TraceWarning(result.Diagnostic);
        }
    }

    public void Dispose() => _globalHotkeys.Dispose();

    private void OpenWorkspace(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        if (FindWindow(name) is { } existing)
        {
            existing.Show();
            existing.Activate();
            return;
        }

        var activation = PrepareWorkspaceActivation(name);
        var window = CreateWindow(activation, name);
        window.Show();
    }

    private static TerminalCommandLineParseResult ParseCommandLine(string commandLine)
    {
        var parsed = new CliParser().ParseCommandLine(
            commandLine,
            ensureInitialTab: false);
        if (parsed.ShouldExit || parsed.Invocation is null)
        {
            return new(false, parsed.Message, []);
        }

        if (parsed.Invocation.SavedLayout is not null)
        {
            return new(false, "Saved layouts must be opened as a new window.", []);
        }

        if (!string.IsNullOrWhiteSpace(parsed.Invocation.TargetWindow) ||
            parsed.Invocation.PositionX is not null ||
            parsed.Invocation.PositionY is not null ||
            parsed.Invocation.Columns is not null ||
            parsed.Invocation.Rows is not null ||
            parsed.Invocation.LaunchMode != CliLaunchMode.Default)
        {
            return new(
                false,
                "Window routing, position, size, and launch-mode options are not valid inside the current window's command palette.",
                []);
        }

        return new(
            true,
            "Command line parsed.",
            parsed.Invocation.Actions,
            parsed.Invocation.SaveRequest is { } save
                ? new TerminalSaveRequest(save.Name, save.KeyChord, save.Commandline)
                : null);
    }

    private MainWindow? FindWindow(string target)
    {
        if (int.TryParse(target, out var id))
        {
            return _windows.FirstOrDefault(window => window.WindowId == id);
        }

        return _windows.FirstOrDefault(window =>
            window.WindowName.Equals(target, StringComparison.OrdinalIgnoreCase));
    }

    private static string WindowNameForNewTarget(string target) =>
        string.IsNullOrWhiteSpace(target) ||
        target.Equals("new", StringComparison.OrdinalIgnoreCase) ||
        target.Equals("-1", StringComparison.Ordinal) ||
        target.StartsWith("use-", StringComparison.OrdinalIgnoreCase) ||
        int.TryParse(target, out _)
            ? string.Empty
            : target;

    private static TerminalWindowActivation ToActivation(CliInvocation invocation) =>
        new(
            invocation.PositionX,
            invocation.PositionY,
            invocation.Columns,
            invocation.Rows,
            (TerminalWindowLaunchMode)(int)invocation.LaunchMode,
            invocation.Actions,
            SaveRequest: invocation.SaveRequest is { } save
                ? new TerminalSaveRequest(save.Name, save.KeyChord, save.Commandline)
                : null);

    private TerminalWindowActivation PrepareActivation(CliInvocation invocation, string name)
    {
        var activation = ToActivation(invocation);
        if (invocation.SavedLayout is { } savedIndex)
        {
            return TerminalLayoutActivationResolver.ResolveSavedSlot(
                _stateStore,
                savedIndex,
                activation);
        }

        return string.IsNullOrWhiteSpace(name)
            ? activation
            : PrepareWorkspaceActivation(name, activation);
    }

    private TerminalWindowActivation PrepareWorkspaceActivation(
        string name,
        TerminalWindowActivation? fallback = null)
    {
        var activation = fallback ?? new TerminalWindowActivation(
            null,
            null,
            null,
            null,
            TerminalWindowLaunchMode.Default,
            [new ActionAndArgs(ShortcutAction.NewTab, new NewTabArgs())]);
        return TerminalLayoutActivationResolver.ResolveWorkspace(
            _stateStore,
            name,
            activation);
    }

    private static async ValueTask<T> OnUiThreadAsync<T>(
        Func<ValueTask<T>> action,
        CancellationToken cancellationToken)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            return await action().ConfigureAwait(true);
        }

        var completion = new TaskCompletionSource<T>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                completion.SetResult(await action().ConfigureAwait(true));
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });
        return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }
}

internal sealed class DeferredBrokerHandler : IBrokerRequestHandler
{
    private readonly TaskCompletionSource<IBrokerRequestHandler> _handler =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void SetHandler(IBrokerRequestHandler handler) => _handler.TrySetResult(handler);

    public async ValueTask<BrokerDispatchResult> HandleAsync(
        string targetWindow,
        string payload,
        CancellationToken cancellationToken)
    {
        var handler = await _handler.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        return await handler.HandleAsync(targetWindow, payload, cancellationToken).ConfigureAwait(false);
    }
}
