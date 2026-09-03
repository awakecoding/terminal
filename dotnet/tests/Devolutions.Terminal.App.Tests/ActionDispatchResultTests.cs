using Devolutions.Terminal.App.Actions;
using Xunit;

namespace Devolutions.Terminal.App.Tests;

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
