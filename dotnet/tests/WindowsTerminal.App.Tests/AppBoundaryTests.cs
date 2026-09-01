using WindowsTerminal.Views;
using Xunit;

namespace WindowsTerminal.App.Tests;

public sealed class AppBoundaryTests
{
    [Fact]
    public void MainWindowLivesInAppAssembly()
    {
        Assert.Equal("WindowsTerminal.App", typeof(MainWindow).Assembly.GetName().Name);
    }
}
