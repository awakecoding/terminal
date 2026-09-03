using Devolutions.Terminal.App.Views;
using Xunit;

namespace Devolutions.Terminal.App.Tests;

public sealed class AppBoundaryTests
{
    [Fact]
    public void MainWindowLivesInAppAssembly()
    {
        Assert.Equal("Devolutions.Terminal.App", typeof(MainWindow).Assembly.GetName().Name);
    }
}
