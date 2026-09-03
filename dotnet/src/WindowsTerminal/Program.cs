using Avalonia;
using WindowsTerminal.Broker;
using WindowsTerminal.Cli;
using WindowsTerminal.Package;

namespace WindowsTerminal;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        CliInvocation? directActivation = null;
        if (args is ["--toast-activation", var encodedActivation])
        {
            if (!ToastActivationCodec.TryParse(
                    encodedActivation,
                    out var activation,
                    out var activationError))
            {
                Console.Error.WriteLine($"wt: {activationError}");
                return 2;
            }

            directActivation = new(
                activation!.TargetWindow,
                null,
                null,
                null,
                null,
                CliLaunchMode.Focus,
                null,
                []);
        }
        else if (
            WindowsTerminal.Platform.LinuxDesktopIntegration.TryNormalizeProtocolActivation(
                args,
                out var protocolArgs,
                out var protocolError))
        {
            if (protocolError is not null)
            {
                Console.Error.WriteLine($"wt: {protocolError}");
                return 2;
            }

            args = protocolArgs;
        }

        if (args is ["--diagnose-desktop"])
        {
            Console.Out.WriteLine(new WindowsTerminal.Platform.PlatformLauncher()
                .GetCapabilityReport());
            return 0;
        }

        var parsed = directActivation is null
            ? new CliParser().Parse(args)
            : new CliParseResult(0, "Validated toast activation.", false, directActivation);
        if (parsed.ShouldExit)
        {
            var writer = parsed.ExitCode == 0 ? Console.Out : Console.Error;
            writer.WriteLine(parsed.Message);
            return parsed.ExitCode;
        }

        var invocation = parsed.Invocation!;
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

        if (invocation.SaveRequest is { Commandline.Length: > 0 } saveRequest)
        {
            broker.DisposeAsync().AsTask().GetAwaiter().GetResult();
            try
            {
                var settings = Microsoft.Terminal.Settings.SettingsService.Load();
                Microsoft.Terminal.Settings.SettingsSnippetStore.Add(
                    settings,
                    saveRequest.Name,
                    saveRequest.KeyChord,
                    saveRequest.Commandline);
                Microsoft.Terminal.Settings.SettingsService.Save(settings);
                if (OperatingSystem.IsWindows())
                {
                    var shellResult = new WindowsShellIntegrationClient().RefreshJumpList(
                        settings.Profiles
                            .Where(static profile => !profile.Hidden && !profile.Orphaned)
                            .Select(static profile => new JumpListProfile(
                                profile.Name,
                                profile.Guid ?? string.Empty,
                                profile.Icon)));
                    if (!shellResult.Succeeded)
                    {
                        Console.Error.WriteLine($"wt: settings saved; jump-list refresh unavailable: {shellResult.Diagnostic}");
                    }
                }
                return 0;
            }
            catch (Exception ex) when (ex is
                ArgumentException or
                IOException or
                UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"wt: {ex.Message}");
                return 1;
            }
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
