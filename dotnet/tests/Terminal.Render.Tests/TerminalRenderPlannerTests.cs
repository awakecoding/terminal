using Microsoft.Terminal.Core;
using Microsoft.Terminal.Render;
using Xunit;

namespace Terminal.Render.Tests;

public sealed class TerminalRenderPlannerTests
{
    [Fact]
    public void GroupsAdjacentCellsWithSamePaint()
    {
        var engine = new TerminalEngine(8, 2);
        engine.Feed("ab\u001b[31mcd");

        var frame = TerminalRenderPlanner.Create(engine.CreateSnapshot(), engine.Scheme);

        Assert.Equal(3, frame.RowsData[0].Runs.Count);
        Assert.Equal("ab", frame.RowsData[0].Runs[0].Text);
        Assert.Equal("cd", frame.RowsData[0].Runs[1].Text);
        Assert.Equal(2, frame.RowsData[0].Runs[1].CellCount);
    }

    [Fact]
    public void ResolvesInverseAndFaintAttributes()
    {
        var attributes = CellAttributes.Default;
        attributes.Foreground = TermColor.FromIndex(1);
        attributes.Background = TermColor.FromIndex(2);
        attributes.Flags = CellFlags.Inverse | CellFlags.Faint;

        var resolved = TerminalRenderPlanner.Resolve(
            attributes,
            null,
            ColorScheme.Campbell,
            reverseScreen: false);

        Assert.Equal(ColorScheme.Campbell.Resolve(2), resolved.Foreground);
        Assert.Equal(0xFF62070Fu, resolved.Background);
    }

    [Fact]
    public void PreservesCombiningTextAndWideCellCount()
    {
        var engine = new TerminalEngine(8, 2);
        engine.Feed("e\u0301界");

        var frame = TerminalRenderPlanner.Create(engine.CreateSnapshot(), engine.Scheme);
        var run = frame.RowsData[0].Runs[0];

        Assert.StartsWith("e\u0301界", run.Text, StringComparison.Ordinal);
        Assert.Equal(8, run.CellCount);
    }

    [Fact]
    public void TracksHyperlinkBoundaries()
    {
        var engine = new TerminalEngine(12, 2);
        engine.Feed("\u001b]8;;https://example.com\u0007link\u001b]8;;\u0007 plain");

        var frame = TerminalRenderPlanner.Create(engine.CreateSnapshot(), engine.Scheme);

        Assert.Equal("https://example.com", frame.RowsData[0].Runs[0].Attributes.HyperlinkUri);
        Assert.Null(frame.RowsData[0].Runs[1].Attributes.HyperlinkUri);
    }

    [Fact]
    public void AppliesScreenReverseVideo()
    {
        var engine = new TerminalEngine(4, 1);
        engine.Feed("\u001b[?5hA");

        var frame = TerminalRenderPlanner.Create(engine.CreateSnapshot(), engine.Scheme);

        Assert.Equal(engine.Scheme.Foreground, frame.Background);
        Assert.Equal(engine.Scheme.Background, frame.RowsData[0].Runs[0].Attributes.Foreground);
        Assert.Equal(engine.Scheme.Foreground, frame.RowsData[0].Runs[0].Attributes.Background);
    }

    [Fact]
    public void CursorTracksScrolledViewportAndHidesOutsideFrame()
    {
        var engine = new TerminalEngine(4, 2);
        engine.Feed("one\r\ntwo\r\nthree");
        engine.Buffer.ScrollOffset = 1;

        var frame = TerminalRenderPlanner.Create(engine.CreateSnapshot(), engine.Scheme);

        Assert.Equal(engine.CursorY + 1, frame.CursorY);
        Assert.False(frame.CursorVisible);
    }
}
