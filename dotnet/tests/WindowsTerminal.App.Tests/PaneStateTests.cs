using WindowsTerminal.Models;
using Xunit;

namespace WindowsTerminal.App.Tests;

public sealed class PaneStateTests
{
    [Fact]
    public void ReadOnlyPaneNeverReceivesCoordinatedInput()
    {
        var active = new InputTarget();
        var readOnly = new InputTarget { IsReadOnly = true };
        var peer = new InputTarget();
        var coordinator = new BroadcastInputCoordinator();
        coordinator.SetEnabled(true);

        var targets = coordinator.WriteInput(active, [active, readOnly, peer], "echo");

        Assert.Equal([active, peer], targets);
        Assert.Equal(["echo"], active.Input);
        Assert.Empty(readOnly.Input);
        Assert.Equal(["echo"], peer.Input);
    }

    [Fact]
    public void DisabledBroadcastTargetsOnlyActivePane()
    {
        var active = new InputTarget();
        var peer = new InputTarget();
        var coordinator = new BroadcastInputCoordinator();

        coordinator.WriteInput(active, [active, peer], "input");

        Assert.Equal(["input"], active.Input);
        Assert.Empty(peer.Input);
    }

    [Theory]
    [InlineData(double.NaN, 0)]
    [InlineData(-1, 0)]
    [InlineData(2, 1)]
    public void ProgressIsFiniteAndClamped(double value, double expected)
    {
        var state = new PanePresentationState();

        state.SetProgress(TerminalProgressState.Normal, value);

        Assert.Equal(expected, state.Progress);
    }

    private sealed class InputTarget : ITerminalInputTarget
    {
        public bool IsReadOnly { get; init; }
        public List<string> Input { get; } = [];
        public void WriteInput(string input) => Input.Add(input);
    }
}
