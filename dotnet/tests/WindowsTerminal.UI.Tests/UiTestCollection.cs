using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using WindowsTerminal.Views;
using Xunit;

namespace WindowsTerminal.UI.Tests;

public sealed class UiTestCollection
{
    [AvaloniaFact]
    public void MainWindowConstructsFromCompiledXaml()
    {
        var window = new MainWindow();

        Assert.Empty(window.Title ?? string.Empty);
        Assert.True(window.Width >= 640);
        Assert.True(window.Height >= 400);
    }

    [AvaloniaFact]
    public void MainWindowProvidesNamedInteractiveChrome()
    {
        var window = new MainWindow();
        var menu = window.FindControl<Avalonia.Controls.Button>("MenuButton");
        var exitFullscreen = window.FindControl<Avalonia.Controls.Button>("ExitFullscreenButton");
        var about = window.FindControl<Avalonia.Controls.Border>("AboutOverlay");

        Assert.NotNull(menu);
        Assert.Equal("New tab menu", AutomationProperties.GetName(menu));
        Assert.NotNull(exitFullscreen);
        Assert.Equal("Exit full screen", AutomationProperties.GetName(exitFullscreen));
        Assert.NotNull(about);
        Assert.Equal(
            AutomationControlType.Window,
            AutomationProperties.GetControlTypeOverride(about));
    }
}
