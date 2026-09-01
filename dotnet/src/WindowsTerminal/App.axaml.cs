using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using WindowsTerminal.Cli;

namespace WindowsTerminal;

public partial class App : Application
{
    internal static CliInvocation? InitialInvocation { get; set; }
    internal static DeferredBrokerHandler? BrokerHandler { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var router = new TerminalWindowRouter(desktop);
            BrokerHandler?.SetHandler(router);
            desktop.MainWindow = router.CreateInitial(
                InitialInvocation ?? new CliParser().Parse([]).Invocation!);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
