using Devolutions.Terminal.Core;
using Xunit;

namespace Devolutions.Terminal.Core.Tests;

public sealed class TerminalBufferExportTests
{
    [Fact]
    public void PlainTextJoinsSoftWrappedRows()
    {
        var engine = new TerminalEngine(4, 3);
        engine.Feed("abcdef\r\nnext");

        var text = TerminalBufferExport.ToPlainText(engine.CreateSnapshot().Buffer);

        Assert.StartsWith("abcdef", text, StringComparison.Ordinal);
        Assert.Contains(Environment.NewLine + "next", text, StringComparison.Ordinal);
    }

    [Fact]
    public void PlainTextPreservesWideAndCombiningGlyphs()
    {
        var engine = new TerminalEngine(10, 2);
        engine.Feed("界e\u0301");

        var text = TerminalBufferExport.ToPlainText(engine.CreateSnapshot().Buffer);

        Assert.StartsWith("界e\u0301", text, StringComparison.Ordinal);
    }

    [Fact]
    public void PlainTextKeepsSpacesInsideSoftWrappedLogicalLine()
    {
        var engine = new TerminalEngine(4, 2);
        engine.Feed("abc d");

        var text = TerminalBufferExport.ToPlainText(engine.CreateSnapshot().Buffer);

        Assert.StartsWith("abc d", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ShellRangesExposePromptCommandAndOutput()
    {
        var engine = new TerminalEngine(40, 4);
        engine.Feed("\u001b]133;A\u0007PS> ");
        engine.Feed("\u001b]133;B\u0007echo hi");
        engine.Feed("\u001b]133;C\u0007\r\nhi");
        engine.Feed("\u001b]133;D;0\u0007");

        var range = Assert.Single(TerminalBufferExport.GetShellCommandRanges(engine.CreateSnapshot().Buffer));

        Assert.Equal(new BufferPosition(0, 0), range.Prompt?.Start);
        Assert.Equal(new BufferPosition(0, 4), range.Command?.Start);
        Assert.Equal(new BufferPosition(1, 0), range.Output?.Start);
        Assert.Equal(0u, range.Mark.ExitCode);
    }

    [Fact]
    public void ShellRangesRemainSeparateAcrossCommands()
    {
        var engine = new TerminalEngine(20, 4);
        engine.Feed("\u001b]133;A\u0007one\u001b]133;D;0\u0007\r\n");
        engine.Feed("\u001b]133;A\u0007two\u001b]133;D;1\u0007");

        var ranges = TerminalBufferExport.GetShellCommandRanges(engine.CreateSnapshot().Buffer);

        Assert.Equal(2, ranges.Count);
        Assert.Equal(0u, ranges[0].Mark.ExitCode);
        Assert.Equal(1u, ranges[1].Mark.ExitCode);
    }

    [Fact]
    public void ShellRangesRemainSeparateOnSamePhysicalRow()
    {
        var engine = new TerminalEngine(40, 2);
        engine.Feed("\u001b]133;A\u0007P1 ");
        engine.Feed("\u001b]133;B\u0007C1 ");
        engine.Feed("\u001b]133;C\u0007O1 ");
        engine.Feed("\u001b]133;D;0\u0007");
        engine.Feed("\u001b]133;A\u0007P2 ");
        engine.Feed("\u001b]133;B\u0007C2");
        engine.Feed("\u001b]133;D;1\u0007");

        var ranges = TerminalBufferExport.GetShellCommandRanges(engine.CreateSnapshot().Buffer);

        Assert.Equal(2, ranges.Count);
        Assert.Equal(new BufferPosition(0, 0), ranges[0].Prompt?.Start);
        Assert.Equal(new BufferPosition(0, 9), ranges[1].Prompt?.Start);
        Assert.Equal(0u, ranges[0].Mark.ExitCode);
        Assert.Equal(1u, ranges[1].Mark.ExitCode);
    }

    [Fact]
    public void ExtractsCommandHistoryAcrossWrappedCells()
    {
        var engine = new TerminalEngine(8, 4);
        engine.Feed("\u001b]133;A\u0007PS> ");
        engine.Feed("\u001b]133;B\u0007echo 123456");
        engine.Feed("\u001b]133;C\u0007\r\noutput");
        engine.Feed("\u001b]133;D;0\u0007");

        var history = TerminalBufferExport.GetCommandHistory(
            engine.CreateSnapshot(includeHistory: true).Buffer);

        Assert.Equal(["echo 123456"], history);
    }

    [Fact]
    public void RangeTextPreservesHardNewlinesAndJoinsSoftWraps()
    {
        var engine = new TerminalEngine(4, 4);
        engine.Feed("abcdef\r\ngh");
        var snapshot = engine.CreateSnapshot().Buffer;

        var text = TerminalBufferExport.GetRangeText(
            snapshot,
            new BufferRange(new BufferPosition(0, 0), new BufferPosition(2, 2)));

        Assert.Equal($"abcdef{Environment.NewLine}gh", text);
    }
}
