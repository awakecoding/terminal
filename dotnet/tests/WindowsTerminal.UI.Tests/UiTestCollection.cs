using Avalonia.Automation;
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

        Assert.Equal("Windows Terminal", window.Title);
        Assert.True(window.Width >= 640);
        Assert.True(window.Height >= 400);
    }

    [AvaloniaFact]
    public void MainWindowProvidesNamedInteractiveChrome()
    {
        var window = new MainWindow();
        var menu = window.FindControl<Avalonia.Controls.Button>("MenuButton");

        Assert.NotNull(menu);
        Assert.Equal("Menu", AutomationProperties.GetName(menu));
    }
}
