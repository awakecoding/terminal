using Avalonia;
using WindowsTerminal.Broker;
using WindowsTerminal.Cli;

namespace WindowsTerminal;

internal static class Program
{
    [STAThread]
    public static async Task<int> Main(string[] args)
    {
        var parsed = new CliParser().Parse(args);
        if (parsed.ShouldExit)
        {
            var writer = parsed.ExitCode == 0 ? Console.Out : Console.Error;
            await writer.WriteLineAsync(parsed.Message).ConfigureAwait(false);
            return parsed.ExitCode;
        }

        var invocation = parsed.Invocation!;
        if (invocation.SavedLayout is not null)
        {
            await Console.Error.WriteLineAsync(
                "wt: persisted layout activation is not available in this phase.").ConfigureAwait(false);
            return 4;
        }

        var deferredHandler = new DeferredBrokerHandler();
        var broker = BrokerHost.TryCreate(deferredHandler);
        if (broker is null)
        {
            var response = await ForwardToPrimaryAsync(invocation).ConfigureAwait(false);
            if (!response.IsSuccess)
            {
                await Console.Error.WriteLineAsync($"wt: {response.Message}").ConfigureAwait(false);
                return response.Status == BrokerStatus.WindowNotFound ? 3 : 1;
            }

            return 0;
        }

        if (invocation.TargetWindow.Equals("use-existing", StringComparison.OrdinalIgnoreCase) ||
            (int.TryParse(invocation.TargetWindow, out var requestedWindowId) && requestedWindowId > 0))
        {
            await broker.DisposeAsync().ConfigureAwait(false);
            await Console.Error.WriteLineAsync(
                $"wt: terminal window '{invocation.TargetWindow}' was not found.").ConfigureAwait(false);
            return 3;
        }

        if (invocation.SaveRequest is not null)
        {
            await broker.DisposeAsync().ConfigureAwait(false);
            await Console.Error.WriteLineAsync(
                "wt: the save command is not available in this phase.").ConfigureAwait(false);
            return 4;
        }

        App.InitialInvocation = invocation;
        App.BrokerHandler = deferredHandler;
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime([]);
            return 0;
        }
        finally
        {
            await broker.DisposeAsync().ConfigureAwait(false);
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

    private static async ValueTask<BrokerResponse> ForwardToPrimaryAsync(CliInvocation invocation)
    {
        var client = new BrokerClient();
        BrokerResponse response = BrokerResponse.Unavailable("Broker endpoint is not ready.");
        for (var attempt = 0; attempt < 10; attempt++)
        {
            response = await client.SendAsync(
                invocation.TargetWindow,
                CliInvocationSerializer.Serialize(invocation),
                TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
            if (response.Status != BrokerStatus.Unavailable)
            {
                return response;
            }

            await Task.Delay(50).ConfigureAwait(false);
        }

        return response;
    }
}
