using Devolutions.Terminal.Core;
using Devolutions.Terminal.Render;
using Xunit;

namespace Devolutions.Terminal.Render.Tests;

public sealed class TerminalFrameDifferTests
{
    [Fact]
    public void ReportsOnlyChangedAndCursorRows()
    {
        var engine = new TerminalEngine(6, 3);
        engine.Feed("first");
        var before = TerminalRenderPlanner.Create(engine.CreateSnapshot(), engine.Scheme);

        engine.Feed("\r\nx");
        var after = TerminalRenderPlanner.Create(engine.CreateSnapshot(), engine.Scheme);

        Assert.Equal([0, 1], TerminalFrameDiffer.GetDirtyRows(before, after));
    }

    [Fact]
    public void ReportsFullViewportWhenGeometryChanges()
    {
        var engine = new TerminalEngine(4, 2);
        var before = TerminalRenderPlanner.Create(engine.CreateSnapshot(), engine.Scheme);
        engine.Resize(5, 3);
        var after = TerminalRenderPlanner.Create(engine.CreateSnapshot(), engine.Scheme);

        Assert.Equal([0, 1, 2], TerminalFrameDiffer.GetDirtyRows(before, after));
    }

    [Fact]
    public void SelectionPlannerNormalizesMultilineRanges()
    {
        var ranges = TerminalOverlayPlanner.CreateSelection(4, 2, 1, 0, 6, 3, 0x80FFFFFF);

        Assert.Equal(3, ranges.Count);
        Assert.Equal(new TerminalCellRange(0, 1, 5, 0x80FFFFFF), ranges[0]);
        Assert.Equal(new TerminalCellRange(2, 0, 4, 0x80FFFFFF), ranges[2]);
    }

    [Fact]
    public void ImageIdentityChangeInvalidatesFullViewport()
    {
        var engine = new TerminalEngine(4, 2);
        var before = TerminalRenderPlanner.Create(engine.CreateSnapshot(), engine.Scheme);
        var after = before with
        {
            Images =
            [
                new TerminalImageOverlay(
                    1,
                    TerminalImageProtocol.Sixel,
                    false,
                    0,
                    0,
                    null,
                    null),
            ],
        };

        Assert.Equal([0, 1], TerminalFrameDiffer.GetDirtyRows(before, after));
    }
}
