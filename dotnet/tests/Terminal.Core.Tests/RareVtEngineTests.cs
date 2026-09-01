using System.Text;
using Microsoft.Terminal.Core;
using Xunit;

namespace Terminal.Core.Tests;

public sealed class RareVtEngineTests
{
    [Fact]
    public void Vt52ModeImplementsCursorEraseIdentifyAndExit()
    {
        var engine = new TerminalEngine(10, 4);
        var responses = new List<string>();
        engine.ResponseReady += (_, data) => responses.Add(Encoding.ASCII.GetString(data));
        engine.Feed("abc\r\nxyz\u001b[?2l\u001bA\u001bD\u001bK\u001bZ");

        Assert.False(engine.AnsiMode);
        Assert.Equal(0, engine.CursorY);
        Assert.Equal(2, engine.CursorX);
        Assert.True(engine.Buffer.GetCell(2, 0).IsBlank);
        Assert.Equal("\u001b/Z", Assert.Single(responses));

        engine.Feed("\u001b<");
        Assert.True(engine.AnsiMode);
        engine.Feed("\u001b[3;4H");
        Assert.Equal((3, 2), (engine.CursorX, engine.CursorY));
    }

    [Fact]
    public void Vt52DirectAddressWorksAtEveryChunkBoundary()
    {
        var bytes = Encoding.ASCII.GetBytes("\u001b[?2l\u001bY#$X");
        for (var split = 0; split <= bytes.Length; split++)
        {
            var engine = new TerminalEngine(20, 10);
            engine.Feed(bytes.AsSpan(0, split));
            engine.Feed(bytes.AsSpan(split));
            Assert.Equal((5, 3), (engine.CursorX, engine.CursorY));
            Assert.Equal('X', engine.Buffer.GetCell(4, 3).Rune.Value);
        }
    }

    [Fact]
    public void Vt52GraphicsMapsDecSpecialGraphics()
    {
        var engine = new TerminalEngine();
        engine.Feed("\u001b[?2l\u001bFj\u001bGx");

        Assert.Equal(new Rune('┘'), engine.Buffer.GetCell(0, 0).Rune);
        Assert.Equal(new Rune('x'), engine.Buffer.GetCell(1, 0).Rune);
    }

    [Fact]
    public void Vt52DoesNotParseAnsiCsi()
    {
        var engine = new TerminalEngine();
        engine.Feed("\u001b[?2l\u001b[31mX");

        Assert.Equal("31mX", engine.Buffer.GetVisibleText().TrimEnd());
        Assert.Equal(ColorKind.Default, engine.Buffer.GetCell(0, 0).Attributes.Foreground.Kind);
    }

    [Fact]
    public void Vt52IgnoresRawC1AnsiIntroducers()
    {
        var engine = new TerminalEngine();
        engine.Feed("\u001b[?2l");
        engine.Feed([0x9B, (byte)'3', (byte)';', (byte)'4', (byte)'H', (byte)'X']);

        Assert.Equal((5, 0), (engine.CursorX, engine.CursorY));
        Assert.Equal("3;4HX", engine.Buffer.GetVisibleText().TrimEnd());
    }

    [Fact]
    public void DrcsDownloadDesignationAndInvocationUsesPrivateUseGlyph()
    {
        var engine = new TerminalEngine();
        engine.Feed("\u001bP0;1;0;2;1;2;6;0{ B~\u001b\\");
        engine.Feed("\u001b( B!");

        var glyph = Assert.Single(engine.DrcsGlyphs).Value;
        Assert.Equal(0x21, glyph.SourceCharacter);
        Assert.Equal(new Rune(0xEF21), engine.Buffer.GetCell(0, 0).Rune);
        Assert.Equal(5, glyph.Width);
        Assert.Equal(6, glyph.Height);
        Assert.Equal(255, glyph.AlphaMask.Span[0]);
    }

    [Fact]
    public void DrcsCanBeInvokedWithSingleShift()
    {
        var engine = new TerminalEngine();
        engine.Feed("\u001bP0;1;0;2;1;2;6;0{ B~\u001b\\");
        engine.Feed("\u001b* B\u001bN!A");

        Assert.Equal(new Rune(0xEF21), engine.Buffer.GetCell(0, 0).Rune);
        Assert.Equal(new Rune('A'), engine.Buffer.GetCell(1, 0).Rune);
    }

    [Fact]
    public void NinetySixCharacterDrcsCanBeInvokedThroughGr()
    {
        var engine = new TerminalEngine();
        engine.Feed("\u001bP0;0;0;2;1;2;6;1{ B~\u001b\\");
        engine.Feed("\u001b- B\u001b~\u00A0");

        Assert.Equal(new Rune(0xEF20), engine.Buffer.GetCell(0, 0).Rune);
    }

    [Fact]
    public void CancelledOrOversizedDrcsDefinitionIsDiscarded()
    {
        var engine = new TerminalEngine();
        engine.Feed("\u001bP0;1;0;2;1;2;6;0{ B~\u0018");
        engine.Feed("\u001bP0;1;0;17;1;2;6;0{ B~\u001b\\");

        Assert.Empty(engine.DrcsGlyphs);
    }

    [Fact]
    public void MacroTextHexRepeatAndInvocationAreSupported()
    {
        var engine = new TerminalEngine();
        engine.Feed("\u001bP1;0;0!zText\u001b\\");
        engine.Feed("\u001bP2;0;1!z48657820!3;6563686F;21\u001b\\");
        engine.Feed("\u001b[1*z \u001b[2*z");

        Assert.Equal("Text Hex echoechoecho!", engine.Buffer.GetVisibleText().TrimEnd());
        Assert.Equal(2, engine.MacroCount);
    }

    [Fact]
    public void MacroRecursionAndExpansionAreBounded()
    {
        var engine = new TerminalEngine();
        engine.Feed("\u001bP1;0;1!z1B5B312A7A\u001b\\");

        engine.Feed("\u001b[1*zX");

        Assert.Equal("X", engine.Buffer.GetVisibleText().TrimEnd());
    }

    [Fact]
    public void MacroHexRepeatCountZeroEmitsOneCopy()
    {
        var engine = new TerminalEngine();
        engine.Feed("\u001bP1;0;1!z!0;41;\u001b\\\u001b[1*z");

        Assert.Equal("A", engine.Buffer.GetVisibleText().TrimEnd());
    }

    [Fact]
    public void MacroDeleteAndSpaceAndChecksumReportsAreExact()
    {
        var engine = new TerminalEngine();
        var responses = new List<string>();
        engine.ResponseReady += (_, data) => responses.Add(Encoding.ASCII.GetString(data));
        engine.Feed("\u001bP1;0;0!z0123456789ABCDEF\u001b\\");
        engine.Feed("\u001b[?62n\u001b[?63;7n");

        Assert.Equal("\u001b[16383*{", responses[0]);
        Assert.StartsWith("\u001bP7!~", responses[1]);
        Assert.EndsWith("\u001b\\", responses[1]);

        engine.Feed("\u001bP1;1;0!z\u001b\\");
        Assert.Equal(1, engine.MacroCount);
    }

    [Fact]
    public void RectangleFillCopyEraseAndAttributeOperationsWork()
    {
        var engine = new TerminalEngine(8, 4);
        engine.Feed("\u001b[65;2;2;3;4$x");
        Assert.Equal('A', engine.Buffer.GetCell(1, 1).Rune.Value);
        Assert.Equal('A', engine.Buffer.GetCell(3, 2).Rune.Value);

        engine.Feed("\u001b[2;2;3;4;1;1;5;1$v");
        Assert.Equal('A', engine.Buffer.GetCell(4, 0).Rune.Value);
        Assert.Equal('A', engine.Buffer.GetCell(6, 1).Rune.Value);

        engine.Feed("\u001b[2*x\u001b[1;5;2;7;1$r");
        Assert.True((engine.Buffer.GetCell(4, 0).Attributes.Flags & CellFlags.Bold) != 0);
        engine.Feed("\u001b[1;5;2;7;1$t");
        Assert.True((engine.Buffer.GetCell(4, 0).Attributes.Flags & CellFlags.Bold) == 0);

        engine.Feed("\u001b[1;5;2;7$z");
        Assert.True(engine.Buffer.GetCell(4, 0).IsBlank);
    }

    [Fact]
    public void RectangleOperationsHandleMissingAttributesAndUseCurrentFillRendition()
    {
        var engine = new TerminalEngine(8, 2);
        engine.Feed("\u001b[$r\u001b[1$t");
        engine.Feed("\u001b[31;3m\u001b[1\"q\u001b[65;1;1;1;2$x");

        for (var column = 0; column < 2; column++)
        {
            var cell = engine.Buffer.GetCell(column, 0);
            Assert.Equal('A', cell.Rune.Value);
            Assert.Equal(TermColor.FromIndex(1), cell.Attributes.Foreground);
            Assert.True((cell.Attributes.Flags & CellFlags.Italic) != 0);
            Assert.True(cell.IsProtected);
        }
    }

    [Fact]
    public void RectangleCopyClearsWideGlyphsAtDestinationBoundaries()
    {
        var engine = new TerminalEngine(8, 2);
        engine.Feed("界\u001b[1;4HA");
        engine.Feed("\u001b[1;4;1;4;1;1;2;1$v");

        Assert.Equal(' ', engine.Buffer.GetCell(0, 0).Rune.Value);
        Assert.Equal('A', engine.Buffer.GetCell(1, 0).Rune.Value);
        Assert.False(engine.Buffer.GetCell(1, 0).IsWideContinuation);
    }

    [Fact]
    public void RectangleCopyDoesNotExtendPartialWideSourcePastDestination()
    {
        var engine = new TerminalEngine(8, 2);
        engine.Feed("界\u001b[1;5HB");
        engine.Feed("\u001b[1;1;1;1;1;1;4;1$v");

        Assert.Equal(' ', engine.Buffer.GetCell(3, 0).Rune.Value);
        Assert.Equal('B', engine.Buffer.GetCell(4, 0).Rune.Value);
    }

    [Fact]
    public void RectangleAttributeChangesSupportRenditionColorsAndReset()
    {
        var engine = new TerminalEngine(8, 2);
        engine.Feed("A\u001b[1;1;1;1;1;3;4;8;9;38;5;6;48;2;1;2;3$r");

        var changed = engine.Buffer.GetCell(0, 0).Attributes;
        Assert.Equal(TermColor.FromIndex(6), changed.Foreground);
        Assert.Equal(TermColor.FromRgb(1, 2, 3), changed.Background);
        var expectedFlags = CellFlags.Bold | CellFlags.Italic | CellFlags.Underline |
            CellFlags.Invisible | CellFlags.Strikethrough;
        Assert.Equal(expectedFlags, changed.Flags & expectedFlags);

        engine.Feed("\u001b[1;1;1;1;0$r");
        Assert.Equal(CellAttributes.Default, engine.Buffer.GetCell(0, 0).Attributes);
    }

    [Fact]
    public void SelectiveErasePreservesProtectedCellsAndAttributes()
    {
        var engine = new TerminalEngine(8, 2);
        engine.Feed("\u001b[31mA\u001b[1\"qB\u001b[0\"qC\r\u001b[?2K");

        Assert.Equal(' ', engine.Buffer.GetCell(0, 0).Rune.Value);
        Assert.Equal('B', engine.Buffer.GetCell(1, 0).Rune.Value);
        Assert.True(engine.Buffer.GetCell(1, 0).IsProtected);
        Assert.Equal(' ', engine.Buffer.GetCell(2, 0).Rune.Value);
        Assert.Equal(TermColor.FromIndex(1), engine.Buffer.GetCell(2, 0).Attributes.Foreground);
    }

    [Fact]
    public void SelectiveErasePreservesProtectedWideGlyphsAtBoundaries()
    {
        var engine = new TerminalEngine(8, 2);
        engine.Feed("\u001b[1\"q界\u001b[0\"qA\r\u001b[?2K");

        Assert.Equal(new Rune('界'), engine.Buffer.GetCell(0, 0).Rune);
        Assert.True(engine.Buffer.GetCell(0, 0).IsProtected);
        Assert.True(engine.Buffer.GetCell(1, 0).IsProtected);
        Assert.True(engine.Buffer.GetCell(1, 0).IsWideContinuation);
        Assert.Equal(' ', engine.Buffer.GetCell(2, 0).Rune.Value);
    }

    [Fact]
    public void ResizePreservesProtectionOnWideGlyphContinuations()
    {
        var engine = new TerminalEngine(8, 2);
        engine.Feed("\u001b[1\"q界");

        engine.Resize(6, 2);

        Assert.True(engine.Buffer.GetCell(0, 0).IsProtected);
        Assert.True(engine.Buffer.GetCell(1, 0).IsProtected);
        Assert.True(engine.Buffer.GetCell(1, 0).IsWideContinuation);
    }

    [Fact]
    public void RectangleChecksumUsesDecAttributesColorsAndPageZeroSemantics()
    {
        var engine = new TerminalEngine(4, 1);
        engine.Feed("\u001b[31;1;4m\u001b[1\"qA");

        Assert.Equal("\u001bP7!~FF1B\u001b\\", Capture(engine, "\u001b[7;1;1;1;1;1*y"));
        Assert.Equal("\u001bP8!~0000\u001b\\", Capture(engine, "\u001b[8;0;1;1;1;1*y"));
    }

    [Fact]
    public void PresentationCursorAndTabReportsRoundTrip()
    {
        var engine = new TerminalEngine(20, 5);
        engine.Feed("\u001b[3;4H\u001b[1;4m\u001b[1\"q");
        var cursorReport = Capture(engine, "\u001b[1$w");
        Assert.StartsWith("\u001bP1$u3;4;1;C;A;", cursorReport);

        engine.Feed("\u001b[1;1H\u001b[0m\u001b[0\"q");
        engine.Feed(cursorReport.Replace("1$u", "1$t", StringComparison.Ordinal));
        Assert.Equal((3, 2), (engine.CursorX, engine.CursorY));
        Assert.True(engine.Buffer.CurrentProtection);
        Assert.True((engine.Buffer.CurrentAttributes.Flags & CellFlags.Bold) != 0);

        Assert.Equal("\u001bP2$u9/17\u001b\\", Capture(engine, "\u001b[2$w"));
        engine.Feed("\u001bP2$t5/10\u001b\\");
        Assert.Equal("\u001bP2$u5/10\u001b\\", Capture(engine, "\u001b[2$w"));
    }

    [Fact]
    public void PresentationCursorReportRoundTripsMultiByteCharsetDesignators()
    {
        var engine = new TerminalEngine(20, 5);
        engine.Feed("\u001b( B");
        var report = Capture(engine, "\u001b[1$w");

        engine.Feed("\u001b(B");
        engine.Feed(report.Replace("1$u", "1$t", StringComparison.Ordinal));

        Assert.Equal(report, Capture(engine, "\u001b[1$w"));
    }

    [Fact]
    public void PresentationCursorReportRoundTripsPendingSingleShift()
    {
        var engine = new TerminalEngine(20, 5);
        engine.Feed("\u001bP0;1;0;2;1;2;6;0{ B~\u001b\\\u001b* B\u001bN");
        var report = Capture(engine, "\u001b[1$w");

        engine.Feed("A");
        engine.Feed(report.Replace("1$u", "1$t", StringComparison.Ordinal));
        engine.Feed("!");

        Assert.Equal(new Rune(0xEF21), engine.Buffer.GetCell(0, 0).Rune);
    }

    [Fact]
    public void CursorSaveRestorePreservesCharacterProtection()
    {
        var engine = new TerminalEngine();
        engine.Feed("\u001b[1\"q\u001b7\u001b[0\"q\u001b8A\r\u001b[?2K");

        Assert.Equal('A', engine.Buffer.GetCell(0, 0).Rune.Value);
        Assert.True(engine.Buffer.GetCell(0, 0).IsProtected);
    }

    [Fact]
    public void TabRestoreIgnoresLargeExcessInputWithoutLargeResult()
    {
        var engine = new TerminalEngine(20, 5);
        var payload = string.Join('/', Enumerable.Repeat("1", 100_000));

        engine.Feed($"\u001bP2$t{payload}\u001b\\");

        Assert.Equal("\u001bP2$u1\u001b\\", Capture(engine, "\u001b[2$w"));
    }

    [Fact]
    public void ColorTableReportAndRestoreUseRgbPercentages()
    {
        var engine = new TerminalEngine();
        var report = Capture(engine, "\u001b[2;2$u");
        Assert.StartsWith("\u001bP2$s0;2;5;5;5/1;2;77;6;12", report);
        Assert.EndsWith("\u001b\\", report);

        engine.Feed("\u001bP2$p1;2;100;0;0\u001b\\");
        Assert.Equal(0xFFFF0000u, engine.Scheme.Resolve(1));
    }

    private static string Capture(TerminalEngine engine, string request)
    {
        string? response = null;
        EventHandler<byte[]> handler = (_, data) => response = Encoding.ASCII.GetString(data);
        engine.ResponseReady += handler;
        engine.Feed(request);
        engine.ResponseReady -= handler;
        return Assert.IsType<string>(response);
    }
}
