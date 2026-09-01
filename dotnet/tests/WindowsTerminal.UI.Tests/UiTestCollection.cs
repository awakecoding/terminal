using Xunit;

namespace WindowsTerminal.UI.Tests;

public sealed class UiTestCollection
{
    [Fact(Skip = "UI automation harness is introduced with the control accessibility phase.")]
    public void MainWindowAutomationSmoke()
    {
    }
}
