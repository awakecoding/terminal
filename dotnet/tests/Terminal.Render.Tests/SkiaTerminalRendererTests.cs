using Microsoft.Terminal.Core;
using Microsoft.Terminal.Render;
using SkiaSharp;
using Xunit;

namespace Terminal.Render.Tests;

public sealed class SkiaTerminalRendererTests
{
    [Fact]
    public void ShapesComplexUnicodeAndReusesBoundedCache()
    {
        using var renderer = new SkiaTerminalRenderer(new TerminalRendererSettings
        {
            GlyphCacheCapacity = 8,
        });
        var frame = CreateFrame("e\u0301界 \U0001F469\u200D\U0001F4BB \uE0B0");
        using var bitmap = NewBitmap(renderer, frame);
        using var canvas = new SKCanvas(bitmap);

        Draw(renderer, canvas, frame);
        var first = renderer.CacheStatistics;
        Draw(renderer, canvas, frame);
        var second = renderer.CacheStatistics;

        Assert.InRange(first.Count, 1, first.Capacity);
        Assert.True(second.Hits > first.Hits);
        Assert.Equal(first.Count, second.Count);
    }

    [Fact]
    public void EvictsLeastRecentlyUsedGlyphsAtCapacity()
    {
        using var renderer = new SkiaTerminalRenderer(new TerminalRendererSettings
        {
            GlyphCacheCapacity = 2,
        });
        var frame = CreateFrame("abc");
        using var bitmap = NewBitmap(renderer, frame);
        using var canvas = new SKCanvas(bitmap);

        Draw(renderer, canvas, frame);

        Assert.Equal(2, renderer.CacheStatistics.Count);
        Assert.True(renderer.CacheStatistics.Evictions >= 1);
    }

    [Fact]
    public void StylesHaveIndependentShapingEntries()
    {
        using var renderer = new SkiaTerminalRenderer();
        var frame = CreateFrame("a\u001b[1mb\u001b[3mc");
        using var bitmap = NewBitmap(renderer, frame);
        using var canvas = new SKCanvas(bitmap);

        Draw(renderer, canvas, frame);

        Assert.True(renderer.CacheStatistics.Count >= 3);
    }

    [Fact]
    public void ContextualGlyphPositionsHaveIndependentCacheEntries()
    {
        using var renderer = new SkiaTerminalRenderer();
        var frame = CreateFrame("\u0633\u0633");
        using var bitmap = NewBitmap(renderer, frame);
        using var canvas = new SKCanvas(bitmap);

        Draw(renderer, canvas, frame);

        Assert.True(renderer.CacheStatistics.Count >= 1);
    }

    [Fact]
    public void BuiltinPowerlineSeparatorsAvoidMissingGlyphEntries()
    {
        using var renderer = new SkiaTerminalRenderer(new TerminalRendererSettings
        {
            FontFamily = "A font family that does not exist",
        });
        var frame = CreateFrame("\uE0A0\uE0A1\uE0A2\uE0A3\uE0B0\uE0B1\uE0B2\uE0B3");
        using var bitmap = NewBitmap(renderer, frame);
        using var canvas = new SKCanvas(bitmap);

        Draw(renderer, canvas, frame);

        Assert.Equal(0, renderer.CacheStatistics.Count);
    }

    [Fact]
    public void EmojiUsesTheWindowsColorEmojiFallback()
    {
        using var renderer = new SkiaTerminalRenderer();
        var frame = CreateFrame("\U0001F600");
        using var bitmap = NewBitmap(renderer, frame);
        using var canvas = new SKCanvas(bitmap);

        Draw(renderer, canvas, frame);

        Assert.Equal("Segoe UI Emoji", renderer.LastResolvedFontFamily);
        var hasColorLayer = false;
        for (var y = 8; y < 8 + (int)renderer.CellSize.Height; y++)
        {
            for (var x = 8; x < 8 + ((int)renderer.CellSize.Width * 2); x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.Red > 150 && pixel.Green > 80 && pixel.Blue < 100)
                {
                    hasColorLayer = true;
                }
            }
        }

        Assert.True(hasColorLayer);
    }

    [Fact]
    public void DpiChangeInvalidatesDeviceIndependentGlyphResources()
    {
        using var renderer = new SkiaTerminalRenderer();
        var frame = CreateFrame("abc");
        using var bitmap = NewBitmap(renderer, frame);
        using var canvas = new SKCanvas(bitmap);
        renderer.Resize(new RenderViewport(frame.Columns, frame.Rows, 1));
        Draw(renderer, canvas, frame);
        var generation = renderer.ResourceGeneration;

        renderer.Resize(new RenderViewport(frame.Columns, frame.Rows, 1.5));

        Assert.Equal(0, renderer.CacheStatistics.Count);
        Assert.True(renderer.ResourceGeneration > generation);
    }

    [Theory]
    [InlineData(TerminalCursorStyle.Bar, 0.05, 0.25)]
    [InlineData(TerminalCursorStyle.Underscore, 0.05, 0.25)]
    [InlineData(TerminalCursorStyle.DoubleUnderscore, 0.08, 0.35)]
    [InlineData(TerminalCursorStyle.Vintage, 0.20, 0.35)]
    [InlineData(TerminalCursorStyle.FilledBox, 0.90, 1.0)]
    [InlineData(TerminalCursorStyle.EmptyBox, 0.10, 0.50)]
    public void CursorStylesProduceStableGeometry(
        TerminalCursorStyle style,
        double minimumFill,
        double maximumFill)
    {
        using var renderer = new SkiaTerminalRenderer();
        var frame = CreateFrame("") with
        {
            CursorStyle = style,
            CursorHeightPercentage = 25,
        };
        using var bitmap = NewBitmap(renderer, frame);
        using var canvas = new SKCanvas(bitmap);

        Draw(renderer, canvas, frame);

        var colored = 0;
        var total = (int)(renderer.CellSize.Width * renderer.CellSize.Height);
        for (var y = 8; y < 8 + (int)renderer.CellSize.Height; y++)
        {
            for (var x = 8; x < 8 + (int)renderer.CellSize.Width; x++)
            {
                if (bitmap.GetPixel(x, y) != new SKColor(12, 12, 12))
                {
                    colored++;
                }
            }
        }

        var fill = (double)colored / total;
        Assert.InRange(fill, minimumFill, maximumFill);
    }

    [Fact]
    public void OverlayColorsComposeInSelectionSearchHyperlinkOrder()
    {
        using var renderer = new SkiaTerminalRenderer();
        var frame = CreateFrame("");
        using var bitmap = NewBitmap(renderer, frame);
        using var canvas = new SKCanvas(bitmap);
        var overlays = new TerminalRenderOverlays(
            [new TerminalCellRange(0, 0, 0, 0xFF0000FF)],
            [new TerminalCellRange(0, 0, 0, 0xFF00FF00)],
            [new TerminalCellRange(0, 0, 0, 0xFFFF0000)]);

        renderer.Render(
            canvas,
            frame,
            overlays,
            new SKRect(0, 0, bitmap.Width, bitmap.Height),
            8,
            drawCursor: false);

        Assert.Equal(new SKColor(255, 0, 0), bitmap.GetPixel(10, 10));
    }

    [Fact]
    public void FilledCursorRedrawsCellGlyphWithContrastingColor()
    {
        using var renderer = new SkiaTerminalRenderer();
        var frame = CreateFrame("X\b") with { CursorStyle = TerminalCursorStyle.FilledBox };
        using var bitmap = NewBitmap(renderer, frame);
        using var canvas = new SKCanvas(bitmap);

        Draw(renderer, canvas, frame);

        var hasContrastingPixel = false;
        for (var y = 8; y < 8 + (int)renderer.CellSize.Height; y++)
        {
            for (var x = 8; x < 8 + (int)renderer.CellSize.Width; x++)
            {
                if (bitmap.GetPixel(x, y) == new SKColor(12, 12, 12))
                {
                    hasContrastingPixel = true;
                }
            }
        }

        Assert.True(hasContrastingPixel);
    }

    [Fact]
    public void FilledCursorForcesContrastWhenCellBackgroundMatchesCursor()
    {
        using var renderer = new SkiaTerminalRenderer();
        var frame = CreateFrame("\u001b[107mX\b") with
        {
            CursorStyle = TerminalCursorStyle.FilledBox,
            CursorColor = 0xFFFFFFFF,
        };
        using var bitmap = NewBitmap(renderer, frame);
        using var canvas = new SKCanvas(bitmap);

        Draw(renderer, canvas, frame);

        var hasBlackGlyphPixel = false;
        for (var y = 8; y < 8 + (int)renderer.CellSize.Height; y++)
        {
            for (var x = 8; x < 8 + (int)renderer.CellSize.Width; x++)
            {
                if (bitmap.GetPixel(x, y) == SKColors.Black)
                {
                    hasBlackGlyphPixel = true;
                }
            }
        }

        Assert.True(hasBlackGlyphPixel);
    }

    [Fact]
    public void RendersSixelOverlayAtTerminalAnchor()
    {
        var engine = new TerminalEngine(16, 2);
        engine.Feed("\u001bPq#2;2;100;0;0~\u001b\\");
        var frame = TerminalRenderPlanner.Create(engine.CreateSnapshot(), engine.Scheme);
        using var renderer = new SkiaTerminalRenderer();
        using var bitmap = NewBitmap(renderer, frame);
        using var canvas = new SKCanvas(bitmap);

        renderer.Render(
            canvas,
            frame,
            TerminalRenderOverlays.Empty,
            new SKRect(0, 0, bitmap.Width, bitmap.Height),
            8,
            drawCursor: false);

        Assert.Single(frame.Images);
        var pixel = bitmap.GetPixel(8, 8);
        Assert.True(pixel.Red > 200 && pixel.Green < 30 && pixel.Blue < 30);
    }

    [Fact]
    public void WarmRenderDoesNotAllocatePerCell()
    {
        using var renderer = new SkiaTerminalRenderer();
        var frame = CreateFrame("allocation");
        using var bitmap = NewBitmap(renderer, frame);
        using var canvas = new SKCanvas(bitmap);
        Draw(renderer, canvas, frame);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var iteration = 0; iteration < 100; iteration++)
        {
            Draw(renderer, canvas, frame);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.InRange(allocated, 0, 16_384);
    }

    private static TerminalRenderFrame CreateFrame(string text)
    {
        var engine = new TerminalEngine(16, 2);
        engine.Feed(text);
        return TerminalRenderPlanner.Create(engine.CreateSnapshot(), engine.Scheme) with
        {
            Background = 0xFF0C0C0C,
            CursorColor = 0xFFFFFFFF,
        };
    }

    private static SKBitmap NewBitmap(
        SkiaTerminalRenderer renderer,
        TerminalRenderFrame frame) =>
        new(
            (int)Math.Ceiling((renderer.CellSize.Width * frame.Columns) + 16),
            (int)Math.Ceiling((renderer.CellSize.Height * frame.Rows) + 16));

    private static void Draw(
        SkiaTerminalRenderer renderer,
        SKCanvas canvas,
        TerminalRenderFrame frame)
    {
        renderer.Resize(new RenderViewport(frame.Columns, frame.Rows, 1));
        renderer.Render(
            canvas,
            frame,
            TerminalRenderOverlays.Empty,
            new SKRect(
                canvas.DeviceClipBounds.Left,
                canvas.DeviceClipBounds.Top,
                canvas.DeviceClipBounds.Right,
                canvas.DeviceClipBounds.Bottom),
            8,
            drawCursor: true);
    }
}
