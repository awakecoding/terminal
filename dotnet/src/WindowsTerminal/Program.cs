using Avalonia;
using WindowsTerminal.Broker;
using WindowsTerminal.Cli;

namespace WindowsTerminal;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var parsed = new CliParser().Parse(args);
        if (parsed.ShouldExit)
        {
            var writer = parsed.ExitCode == 0 ? Console.Out : Console.Error;
            writer.WriteLine(parsed.Message);
            return parsed.ExitCode;
        }

        var invocation = parsed.Invocation!;
        if (invocation.SavedLayout is not null)
        {
            Console.Error.WriteLine("wt: persisted layout activation is not available in this phase.");
            return 4;
        }

        var deferredHandler = new DeferredBrokerHandler();
        var broker = BrokerHost.TryCreate(deferredHandler);
        if (broker is null)
        {
            var response = ForwardToPrimaryAsync(invocation).AsTask().GetAwaiter().GetResult();
            if (!response.IsSuccess)
            {
                Console.Error.WriteLine($"wt: {response.Message}");
                return response.Status == BrokerStatus.WindowNotFound ? 3 : 1;
            }

            return 0;
        }

        if (invocation.TargetWindow.Equals("use-existing", StringComparison.OrdinalIgnoreCase) ||
            (int.TryParse(invocation.TargetWindow, out var requestedWindowId) && requestedWindowId > 0))
        {
            broker.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Console.Error.WriteLine($"wt: terminal window '{invocation.TargetWindow}' was not found.");
            return 3;
        }

        if (invocation.SaveRequest is not null)
        {
            broker.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Console.Error.WriteLine("wt: the save command is not available in this phase.");
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
            broker.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect();
#if DEBUG
        builder = builder.WithDeveloperTools();
#endif
        return builder.LogToTrace();
    }

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
