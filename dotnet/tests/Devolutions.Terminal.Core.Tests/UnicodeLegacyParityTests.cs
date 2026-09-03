using System.Text;
using Devolutions.Terminal.Core;
using Xunit;

namespace Devolutions.Terminal.Core.Tests;

public sealed class UnicodeLegacyParityTests
{
    [Theory]
    [InlineData("\u1100\u1161\u11A8")]
    [InlineData("\u0915\u094D\u0937")]
    [InlineData("\u0600A")]
    [InlineData("👩🏻\u200D🚀")]
    [InlineData("1\uFE0F\u20E3")]
    public void GraphemeBoundariesAreInvariantAcrossFeedChunks(string text)
    {
        var expected = FeedAtEveryBoundary(text, chunked: false);
        var actual = FeedAtEveryBoundary(text, chunked: true);

        Assert.Equal(expected.CursorX, actual.CursorX);
        Assert.Equal(expected.Buffer.GetCell(0, 0).Text, actual.Buffer.GetCell(0, 0).Text);
        Assert.Equal(1, actual.Buffer.GetRow(0).Count(cell => !cell.IsBlank && !cell.IsWideContinuation));
    }

    [Fact]
    public void DecLineRenditionsFlowThroughSnapshotsAndClipAddressing()
    {
        var engine = new TerminalEngine(10, 4);
        engine.Feed("\u001b#6ABCDE");
        Assert.Equal(LineRendition.DoubleWidth, engine.Buffer.CurrentLineRendition);
        engine.Feed("\u001b[2;1H\u001b#3top");
        engine.Feed("\u001b[3;1H\u001b#4botto");
        engine.Feed("\u001b[4;1H\u001b#5singl");

        var snapshot = engine.CreateSnapshot(includeHistory: true).Buffer;
        Assert.Equal(LineRendition.DoubleWidth, snapshot.Lines[0].Rendition);
        Assert.Equal(LineRendition.DoubleHeightTop, snapshot.Lines[1].Rendition);
        Assert.Equal(LineRendition.DoubleHeightBottom, snapshot.Lines[2].Rendition);
        Assert.Equal(LineRendition.SingleWidth, snapshot.Lines[3].Rendition);
        Assert.Equal("ABCDE", string.Concat(snapshot.Lines[0].Cells.Select(cell => cell.Text)).TrimEnd());
    }

    [Fact]
    public void ReflowPreservesDoubleWidthLogicalGeometry()
    {
        var engine = new TerminalEngine(10, 3);
        engine.Feed("\u001b#6ABCDE");

        engine.Resize(8, 3);

        var snapshot = engine.CreateSnapshot(includeHistory: true).Buffer;
        Assert.Equal(LineRendition.DoubleWidth, snapshot.Lines[0].Rendition);
        Assert.Equal("ABCD", string.Concat(snapshot.Lines[0].Cells.Select(cell => cell.Text)).TrimEnd());
        Assert.Equal(LineRendition.DoubleWidth, snapshot.Lines[1].Rendition);
        Assert.Equal("E", string.Concat(snapshot.Lines[1].Cells.Select(cell => cell.Text)).TrimEnd());
    }

    [Fact]
    public void ReflowBreaksWrappedParagraphWhenRenditionChanges()
    {
        var engine = new TerminalEngine(10, 3);
        engine.Feed("\u001b#6ABCDEF");

        engine.Resize(8, 3);

        var snapshot = engine.CreateSnapshot(includeHistory: true).Buffer;
        Assert.Equal(LineRendition.DoubleWidth, snapshot.Lines[0].Rendition);
        Assert.Equal("ABCD", string.Concat(snapshot.Lines[0].Cells.Select(cell => cell.Text)).TrimEnd());
        Assert.Equal(LineRendition.DoubleWidth, snapshot.Lines[1].Rendition);
        Assert.Equal("E", string.Concat(snapshot.Lines[1].Cells.Select(cell => cell.Text)).TrimEnd());
        Assert.Equal(LineRendition.SingleWidth, snapshot.Lines[2].Rendition);
        Assert.Equal("F", string.Concat(snapshot.Lines[2].Cells.Select(cell => cell.Text)).TrimEnd());
    }

    [Fact]
    public void DrcsMasksAreBoundedSnapshotResources()
    {
        var engine = new TerminalEngine();
        engine.Feed("\u001bP0;1;0;2;1;2;6;0{ B~\u001b\\");
        engine.Feed("\u001b( B!");

        var snapshot = engine.CreateSnapshot();
        var glyph = Assert.Single(snapshot.DrcsGlyphs).Value;
        Assert.Equal(new Rune(0xEF21), snapshot.Buffer.Lines[0].Cells[0].Rune);
        Assert.Equal(glyph.Width * glyph.Height, glyph.AlphaMask.Length);
        Assert.True(engine.Capabilities.HasFlag(TerminalEngineCapabilities.DrcsGlyphs));
    }

    [Fact]
    public void ExtendedKeyboardModesSetQueryPushPopAndReset()
    {
        var engine = new TerminalEngine();
        var responses = new List<string>();
        engine.ResponseReady += (_, bytes) => responses.Add(Encoding.UTF8.GetString(bytes));

        engine.Feed("\u001b[=3u\u001b[>9u\u001b[?u\u001b[<u\u001b[?u");
        engine.Feed("\u001b[>4;2m\u001b[?4m\u001b[?9001h");

        Assert.Equal("\u001b[?9u", responses[0]);
        Assert.Equal("\u001b[?3u", responses[1]);
        Assert.Equal("\u001b[>4;2m", responses[2]);
        Assert.Equal(
            new TerminalInputMode(
                true,
                false,
                false,
                KittyKeyboardFlags.DisambiguateEscapeCodes | KittyKeyboardFlags.ReportEventTypes,
                2,
                true),
            engine.InputMode);

        engine.Reset();
        Assert.Equal(KittyKeyboardFlags.None, engine.InputMode.KittyFlags);
        Assert.Equal(0, engine.InputMode.ModifyOtherKeys);
        Assert.False(engine.InputMode.Win32InputMode);
    }

    [Fact]
    public void KittyModeHonorsProfileCapabilityGate()
    {
        var engine = new TerminalEngine();
        var response = string.Empty;
        engine.ResponseReady += (_, bytes) => response = Encoding.UTF8.GetString(bytes);
        engine.ConfigureOptionalFeatures(false, false, allowKittyKeyboard: false);

        engine.Feed("\u001b[=3u\u001b[?u");

        Assert.Equal(KittyKeyboardFlags.None, engine.InputMode.KittyFlags);
        Assert.Equal("\u001b[?0u", response);
        Assert.Equal(
            TerminalEngineCapabilities.UnicodeGraphemeClusters |
            TerminalEngineCapabilities.RowRendition |
            TerminalEngineCapabilities.Vt52Keyboard |
            TerminalEngineCapabilities.DrcsGlyphs |
            TerminalEngineCapabilities.KittyKeyboard |
            TerminalEngineCapabilities.ModifyOtherKeys |
            TerminalEngineCapabilities.Win32Input |
            TerminalEngineCapabilities.SixelImages |
            TerminalEngineCapabilities.Iterm2Images |
            TerminalEngineCapabilities.ConEmuImages,
            engine.Capabilities);
    }

    private static TerminalEngine FeedAtEveryBoundary(string text, bool chunked)
    {
        var engine = new TerminalEngine(20, 2);
        var bytes = Encoding.UTF8.GetBytes(text);
        if (!chunked)
        {
            engine.Feed(bytes);
            return engine;
        }

        for (var offset = 0; offset < bytes.Length; offset++)
        {
            engine.Feed(bytes.AsSpan(offset, 1));
        }

        return engine;
    }
}
