using Devolutions.Terminal.Core;
using Xunit;

namespace Devolutions.Terminal.Core.Tests;

public sealed class ShellIntegrationTests
{
    [Fact]
    public void Osc133MarksPromptCommandAndOutputCells()
    {
        var engine = new TerminalEngine(40, 4);

        engine.Feed("\u001b]133;A\u0007PS> ");
        engine.Feed("\u001b]133;B\u0007echo hi");
        engine.Feed("\u001b]133;C\u0007\r\nhi");
        engine.Feed("\u001b]133;D;0\u0007");

        var snapshot = engine.CreateSnapshot(includeHistory: true).Buffer;
        Assert.Equal(ShellIntegrationKind.Prompt, snapshot.Lines[0].Cells[0].ShellIntegration);
        Assert.Equal(ShellIntegrationKind.Command, snapshot.Lines[0].Cells[4].ShellIntegration);
        Assert.Equal(ShellIntegrationKind.Output, snapshot.Lines[1].Cells[0].ShellIntegration);
        Assert.Equal(0u, snapshot.Lines[0].Mark?.ExitCode);
    }

    [Fact]
    public void InvalidExitCodeIsRecordedAsFailure()
    {
        var engine = new TerminalEngine(20, 2);
        engine.Feed("\u001b]133;A\u0007prompt");
        engine.Feed("\u001b]133;D;-1\u0007");

        var mark = engine.CreateSnapshot().Buffer.Lines[0].Mark;

        Assert.Equal(uint.MaxValue, mark?.ExitCode);
    }

    [Fact]
    public void OutOfOrderCommandCreatesPromptMark()
    {
        var engine = new TerminalEngine(20, 2);
        engine.Feed("\u001b]133;B\u0007command");

        var line = engine.CreateSnapshot().Buffer.Lines[0];

        Assert.NotNull(line.Mark);
        Assert.Equal(ShellIntegrationKind.Command, line.Cells[0].ShellIntegration);
    }

    [Fact]
    public void ShellIntegrationRaisesChangeEvent()
    {
        var engine = new TerminalEngine(20, 2);
        var changes = 0;
        engine.ShellIntegrationChanged += (_, _) => changes++;

        engine.Feed("\u001b]133;A\u0007");
        engine.Feed("\u001b]133;B\u0007");
        engine.Feed("\u001b]133;C\u0007");
        engine.Feed("\u001b]133;D;1\u0007");

        Assert.Equal(4, changes);
    }

    [Fact]
    public void ResizePreservesPromptMark()
    {
        var engine = new TerminalEngine(8, 3);
        engine.Feed("\u001b]133;A\u0007prompt>");

        engine.Resize(4, 4);

        Assert.NotNull(engine.CreateSnapshot().Buffer.Lines[0].Mark);
    }

    [Fact]
    public void WideContinuationKeepsShellRegionAcrossResize()
    {
        var engine = new TerminalEngine(8, 3);
        engine.Feed("\u001b]133;B\u0007界");

        engine.Resize(4, 3);

        var cells = engine.CreateSnapshot().Buffer.Lines[0].Cells;
        Assert.Equal(ShellIntegrationKind.Command, cells[0].ShellIntegration);
        Assert.Equal(ShellIntegrationKind.Command, cells[1].ShellIntegration);
    }

    [Fact]
    public void OutOfOrderOutputDoesNotOverwriteCompletedCommand()
    {
        var engine = new TerminalEngine(20, 3);
        engine.Feed("\u001b]133;A\u0007first");
        engine.Feed("\u001b]133;C\u0007output");
        engine.Feed("\u001b]133;D;0\u0007\r\n");
        engine.Feed("\u001b]133;C\u0007second");
        engine.Feed("\u001b]133;D;1\u0007");

        var lines = engine.CreateSnapshot().Buffer.Lines;

        Assert.Equal(0u, lines[0].Mark?.ExitCode);
        Assert.Equal(1u, lines[1].Mark?.ExitCode);
    }

    [Fact]
    public void RepeatedPromptDoesNotEraseCompletedExitCode()
    {
        var engine = new TerminalEngine(20, 2);
        engine.Feed("\u001b]133;A\u0007prompt");
        engine.Feed("\u001b]133;D;42\u0007");

        engine.Feed("\u001b]133;A\u0007");

        Assert.Equal(42u, engine.CreateSnapshot().Buffer.Lines[0].Mark?.ExitCode);
    }

    [Fact]
    public void EmptyExitCodeIsInvalidRatherThanAbsent()
    {
        var engine = new TerminalEngine(20, 2);
        engine.Feed("\u001b]133;A\u0007prompt");
        engine.Feed("\u001b]133;D;\u0007");

        Assert.Equal(uint.MaxValue, engine.CreateSnapshot().Buffer.Lines[0].Mark?.ExitCode);
    }
}
