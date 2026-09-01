using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using WindowsTerminal.Cli;
using WindowsTerminal.Views;

namespace WindowsTerminal;

public partial class App : Application
{
    private readonly HashSet<Window> _trayWindows = [];
    private readonly Dictionary<Window, WindowState> _windowStates = [];
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private bool _alwaysShowNotificationIcon;

    internal static CliInvocation? InitialInvocation { get; set; }
    internal static DeferredBrokerHandler? BrokerHandler { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            var router = new TerminalWindowRouter(desktop, ConfigureWindow);
            BrokerHandler?.SetHandler(router);
            desktop.MainWindow = router.CreateInitial(
                InitialInvocation ?? new CliParser().Parse([]).Invocation!);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void ConfigureWindow(Window window)
    {
        if (window is not MainWindow terminalWindow)
        {
            return;
        }

        _alwaysShowNotificationIcon |= terminalWindow.AlwaysShowNotificationIcon;
        _windowStates[window] = window.WindowState;
        window.PropertyChanged += (_, args) =>
        {
            if (args.Property != Window.WindowStateProperty ||
                !terminalWindow.MinimizeToNotificationArea)
            {
                return;
            }

            if (window.WindowState == WindowState.Minimized)
            {
                window.ShowInTaskbar = false;
                _trayWindows.Add(window);
            }
            else
            {
                window.ShowInTaskbar = true;
                _trayWindows.Remove(window);
                _windowStates[window] = window.WindowState;
            }

            RefreshNotificationIcon();
        };
        window.Closed += (_, _) =>
        {
            _trayWindows.Remove(window);
            _windowStates.Remove(window);
            RefreshNotificationIcon();
        };
        RefreshNotificationIcon();
    }

    private void RefreshNotificationIcon()
    {
        var notificationIcon = TrayIcon.GetIcons(this)?.FirstOrDefault();
        if (notificationIcon is not null)
        {
            notificationIcon.IsVisible = _alwaysShowNotificationIcon || _trayWindows.Count > 0;
        }
    }

    private void ShowWindows_OnClick(object? sender, EventArgs e)
    {
        if (_desktop is null)
        {
            return;
        }

        foreach (var window in _trayWindows.OfType<MainWindow>().ToArray())
        {
            window.ShowInTaskbar = true;
            window.WindowState = _windowStates.GetValueOrDefault(window, WindowState.Normal);
            window.Show();
            window.Activate();
            _trayWindows.Remove(window);
        }

        RefreshNotificationIcon();
    }

    private void Exit_OnClick(object? sender, EventArgs e) => _desktop?.Shutdown();
}
