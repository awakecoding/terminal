using WindowsTerminal.Actions;
using Xunit;

namespace WindowsTerminal.App.Tests;

public sealed class ActionDispatchResultTests
{
    [Theory]
    [InlineData(ActionDispatchStatus.Executed, true)]
    [InlineData(ActionDispatchStatus.Disabled, false)]
    [InlineData(ActionDispatchStatus.Unsupported, true)]
    [InlineData(ActionDispatchStatus.Failed, false)]
    public void HandledReflectsDispatchOutcome(ActionDispatchStatus status, bool handled)
    {
        var result = new ActionDispatchResult(status, ActionScope.Control, "copy");

        Assert.Equal(handled, result.Handled);
    }
}
