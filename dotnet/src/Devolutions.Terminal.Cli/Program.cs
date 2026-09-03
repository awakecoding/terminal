using System.Diagnostics;
using Devolutions.Terminal.Broker;

namespace Devolutions.Terminal.Cli;

public static class Program
{
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
        var response = await new BrokerClient().SendAsync(
            invocation.TargetWindow,
            CliInvocationSerializer.Serialize(invocation)).ConfigureAwait(false);
        if (response.Status == BrokerStatus.Unavailable)
        {
            if (RequiresExistingWindow(invocation.TargetWindow))
            {
                await Console.Error.WriteLineAsync(
                    $"dt: terminal window '{invocation.TargetWindow}' was not found.").ConfigureAwait(false);
                return 3;
            }

            if (TryLaunchHost(args))
            {
                return 0;
            }
        }

        if (!response.IsSuccess)
        {
            await Console.Error.WriteLineAsync($"dt: {response.Message}").ConfigureAwait(false);
            return response.Status == BrokerStatus.WindowNotFound ? 3 : 1;
        }

        return 0;
    }

    private static bool RequiresExistingWindow(string target) =>
        target.Equals("use-existing", StringComparison.OrdinalIgnoreCase) ||
        (int.TryParse(target, out var id) && id > 0);

    private static bool TryLaunchHost(IReadOnlyList<string> args)
    {
        var hostPath = Path.Combine(AppContext.BaseDirectory, "Devolutions.Terminal.exe");
        if (!File.Exists(hostPath))
        {
            return false;
        }

        var startInfo = new ProcessStartInfo(hostPath)
        {
            UseShellExecute = false,
        };
        foreach (var argument in args)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return Process.Start(startInfo) is not null;
    }
}
