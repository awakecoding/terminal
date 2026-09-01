using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using WindowsTerminal.Broker;
using WindowsTerminal.Cli;
using WindowsTerminal.Routing;
using WindowsTerminal.Views;

namespace WindowsTerminal;

internal sealed class TerminalWindowRouter(IClassicDesktopStyleApplicationLifetime desktop) : IBrokerRequestHandler
{
    private readonly List<MainWindow> _windows = [];
    private int _nextWindowId = 1;

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

        if (invocation.SaveRequest is not null)
        {
            return new(BrokerStatus.Unsupported, "The save command is not available in this phase.");
        }

        if (invocation.SavedLayout is not null)
        {
            return new(BrokerStatus.Unsupported, "Persisted layout activation is not available in this phase.");
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

        if (target is null &&
            (normalized.Equals("use-existing", StringComparison.OrdinalIgnoreCase) ||
             (int.TryParse(normalized, out var requestedId) && requestedId > 0)))
        {
            return new(BrokerStatus.WindowNotFound, $"Terminal window '{normalized}' was not found.");
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
        => CreateWindow(ToActivation(invocation), name);

    private MainWindow CreateWindow(TerminalWindowActivation activation, string name)
    {
        var window = new MainWindow(
            _nextWindowId++,
            name,
            activation,
            childActivation =>
            {
                var child = CreateWindow(childActivation, string.Empty);
                child.Show();
            });
        _windows.Add(window);
        window.Closed += (_, _) =>
        {
            _windows.Remove(window);
            if (ReferenceEquals(desktop.MainWindow, window))
            {
                desktop.MainWindow = _windows.FirstOrDefault();
            }
        };
        return window;
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
            invocation.Actions);

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
