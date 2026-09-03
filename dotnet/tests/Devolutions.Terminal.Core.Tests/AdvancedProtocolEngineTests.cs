using System.Text;
using Devolutions.Terminal.Core;
using Xunit;

namespace Devolutions.Terminal.Core.Tests;

public sealed class AdvancedProtocolEngineTests
{
    [Fact]
    public void SixelDcsAddsRendererNeutralOverlayAtCursor()
    {
        var engine = new TerminalEngine(80, 24);
        TerminalImageOverlay? raised = null;
        engine.ImageAdded += (_, image) => raised = image;
        engine.Feed("\u001b[?80l\u001b[3;4H\u001bP7;1q#1;2;100;0;0!2~\u001b\\");

        var overlay = Assert.Single(engine.Images);
        Assert.Same(overlay, raised);
        Assert.Equal(TerminalImageProtocol.Sixel, overlay.Protocol);
        Assert.Equal(3, overlay.AnchorColumn);
        Assert.Equal(2, overlay.AnchorRow);
        Assert.False(overlay.AlternateBuffer);
        Assert.NotNull(overlay.Sixel);
        Assert.Equal(2, overlay.Sixel.Width);
        Assert.Equal(6, overlay.Sixel.Height);
        Assert.Null(overlay.InlineImage);
    }

    [Fact]
    public void DecrqssReportsSgrAndMargins()
    {
        var engine = new TerminalEngine(80, 24);
        var responses = new List<string>();
        engine.ResponseReady += (_, bytes) => responses.Add(Encoding.ASCII.GetString(bytes));
        engine.Feed("\u001b[1;38;2;10;20;30m");
        engine.Feed("\u001b[3;20r");

        engine.Feed("\u001bP$qm\u001b\\");
        engine.Feed("\u001bP$qr\u001b\\");

        Assert.Equal("\u001bP1$r0;1;38:2::10:20:30m\u001b\\", responses[0]);
        Assert.Equal("\u001bP1$r3;20r\u001b\\", responses[1]);
    }

    [Theory]
    [InlineData(" q", "1$r0 q")]
    [InlineData("\"q", "1$r0\"q")]
    [InlineData("*x", "1$r1*x")]
    [InlineData("z", "0$r")]
    public void DecrqssReturnsSupportedOrFailureReport(string request, string expected)
    {
        var engine = new TerminalEngine();
        string? response = null;
        engine.ResponseReady += (_, bytes) => response = Encoding.ASCII.GetString(bytes);

        engine.Feed($"\u001bP$q{request}\u001b\\");

        Assert.Equal($"\u001bP{expected}\u001b\\", response);
    }

    [Fact]
    public void XtgettcapReportsKnownAndUnknownCapabilities()
    {
        var engine = new TerminalEngine();
        var responses = new List<string>();
        engine.ResponseReady += (_, bytes) => responses.Add(Encoding.ASCII.GetString(bytes));

        engine.Feed("\u001bP+q544e;436f;626164\u001b\\");

        Assert.Equal("\u001bP1+r544e=787465726D2D323536636F6C6F72\u001b\\", responses[0]);
        Assert.Equal("\u001bP1+r436f=323536\u001b\\", responses[1]);
        Assert.Equal("\u001bP0+r626164\u001b\\", responses[2]);
    }

    [Fact]
    public void XtgettcapCapsResponseCountAndTokenLength()
    {
        var engine = new TerminalEngine();
        var responses = new List<string>();
        engine.ResponseReady += (_, bytes) => responses.Add(Encoding.ASCII.GetString(bytes));
        var requests = string.Join(';', Enumerable.Repeat("544e", 40));

        engine.Feed($"\u001bP+q{requests}\u001b\\");
        engine.Feed($"\u001bP+q{new string('4', 130)}\u001b\\");

        Assert.Equal(33, responses.Count);
        Assert.Equal("\u001bP0+r\u001b\\", responses[^1]);
    }

    [Fact]
    public void Osc1337ParsesInlineImageMetadataAndPayload()
    {
        var engine = new TerminalEngine();
        var name = Convert.ToBase64String(Encoding.UTF8.GetBytes("diagram.png"));
        var payload = Convert.ToBase64String([1, 2, 3, 4]);

        engine.Feed($"\u001b]1337;File=name={name};size=4;width=10px;height=50%;preserveAspectRatio=0;inline=1:{payload}\u0007");

        var overlay = Assert.Single(engine.Images);
        Assert.Equal(TerminalImageProtocol.Iterm2Inline, overlay.Protocol);
        Assert.Null(overlay.Sixel);
        Assert.NotNull(overlay.InlineImage);
        Assert.Equal("diagram.png", overlay.InlineImage.Metadata.Name);
        Assert.Equal(4, overlay.InlineImage.Metadata.DeclaredSize);
        Assert.Equal(new TerminalImageDimension(TerminalImageDimensionKind.Pixels, 10), overlay.InlineImage.Metadata.Width);
        Assert.Equal(new TerminalImageDimension(TerminalImageDimensionKind.Percent, 50), overlay.InlineImage.Metadata.Height);
        Assert.False(overlay.InlineImage.Metadata.PreserveAspectRatio);
        Assert.Equal([1, 2, 3, 4], overlay.InlineImage.Data.ToArray());
    }

    [Fact]
    public void Osc1337RequiresInlineAndRejectsOversizedDeclaration()
    {
        var engine = new TerminalEngine();
        engine.Feed("\u001b]1337;File=size=9999999;inline=1:AA==\u0007");
        engine.Feed("\u001b]1337;File=inline=0:AA==\u0007");

        Assert.Empty(engine.Images);
    }

    [Fact]
    public void Osc1337NonInlineTransferIsExplicitlyRejectedWithoutIo()
    {
        var engine = new TerminalEngine();
        var diagnostics = new List<TerminalEngineDiagnostic>();
        engine.Diagnostic += (_, value) => diagnostics.Add(value);

        engine.Feed("\u001b]1337;File=name=L2V0Yy9wYXNzd2Q=;inline=0:\u0007");

        Assert.Empty(engine.Images);
        Assert.Contains(diagnostics, value => value.Code == "image.osc1337.non-inline-unsupported");
    }

    [Fact]
    public void Osc1337RejectsDeclaredSizeMismatch()
    {
        var engine = new TerminalEngine();
        TerminalEngineDiagnostic? diagnostic = null;
        engine.Diagnostic += (_, value) => diagnostic = value;

        engine.Feed("\u001b]1337;File=size=2;inline=1:AQ==\u0007");

        Assert.Empty(engine.Images);
        Assert.Equal("image.osc1337.rejected", diagnostic?.Code);
    }

    [Fact]
    public void ConEmuSinglePartImageUsesSharedBoundedOverlay()
    {
        var engine = new TerminalEngine();

        engine.Feed("\u001b]9;4;st=0;sz=4;AQIDBA==\u001b\\");

        var overlay = Assert.Single(engine.Images);
        Assert.Equal(TerminalImageProtocol.ConEmuInline, overlay.Protocol);
        Assert.Equal(4, overlay.InlineImage!.Metadata.DeclaredSize);
        Assert.Equal([1, 2, 3, 4], overlay.InlineImage.Data.ToArray());
    }

    [Theory]
    [InlineData("4;st=1;sz=1;AQ==")]
    [InlineData("4;st=0;sz=2;AQ==")]
    [InlineData("4;st=0;sz=9999999;AQ==")]
    [InlineData("4;st=0;sz=1;not-base64")]
    public void ConEmuRejectsMultipartMalformedAndOversizedImages(string value)
    {
        var engine = new TerminalEngine();
        TerminalEngineDiagnostic? diagnostic = null;
        engine.Diagnostic += (_, value) => diagnostic = value;

        engine.Feed($"\u001b]9;{value}\u0007");

        Assert.Empty(engine.Images);
        Assert.Equal("image.conemu.rejected", diagnostic?.Code);
    }

    [Theory]
    [InlineData("Infinity")]
    [InlineData("5000")]
    [InlineData("101%")]
    public void Osc1337NormalizesUnsafeDimensionsToAuto(string width)
    {
        var engine = new TerminalEngine();
        engine.Feed($"\u001b]1337;File=width={width};inline=1:AA==\u0007");

        var overlay = Assert.Single(engine.Images);
        Assert.Equal(TerminalImageDimension.Auto, overlay.InlineImage!.Metadata.Width);
    }

    [Fact]
    public void ResetClearsImageOverlays()
    {
        var engine = new TerminalEngine();
        engine.Feed("\u001bP7q#5;2;100;0;0~\u001b\\");
        Assert.Single(engine.Images);

        engine.Reset();

        Assert.Empty(engine.Images);
        Assert.Empty(engine.CreateSnapshot().Images);

        engine.Feed("\u001bP7q#5~\u001b\\");
        Assert.NotEqual(0xFFFF0000u, Assert.Single(engine.Images).Sixel!.Palette.Span[5]);
    }

    [Fact]
    public void ImageRetentionEvictsOldestOverlays()
    {
        var engine = new TerminalEngine();
        for (var index = 0; index < TerminalImageLimits.MaximumRetainedImages + 1; index++)
        {
            engine.Feed("\u001bP7q~\u001b\\");
        }

        Assert.Equal(TerminalImageLimits.MaximumRetainedImages, engine.Images.Count);
        Assert.Equal(2, engine.Images[0].Id);
        Assert.Equal(TerminalImageLimits.MaximumRetainedImages + 1, engine.Images[^1].Id);
    }

    [Fact]
    public void ImageIdsRemainMonotonicAcrossReset()
    {
        var engine = new TerminalEngine();
        engine.Feed("\u001bP7q~\u001b\\");
        var firstId = Assert.Single(engine.Images).Id;

        engine.Reset();
        engine.Feed("\u001bP7q~\u001b\\");

        Assert.True(Assert.Single(engine.Images).Id > firstId);
    }

    [Fact]
    public void SnapshotImageAnchorTracksBufferScroll()
    {
        var engine = new TerminalEngine(10, 2, historySize: 10);
        engine.Feed("\u001b[?80l\u001b[2;1H\u001bP7q~\u001b\\");
        Assert.Equal(1, Assert.Single(engine.CreateSnapshot().Images).AnchorRow);

        engine.Feed("\r\n");

        Assert.Equal(0, Assert.Single(engine.CreateSnapshot().Images).AnchorRow);
    }

    [Fact]
    public void ScrollbackNavigationResolvesImageAgainstViewedHistory()
    {
        var engine = new TerminalEngine(10, 2, historySize: 10);
        engine.Feed("\u001b[?80l\u001bP7q~\u001b\\\r\none\r\ntwo");
        Assert.Equal(-1, Assert.Single(engine.CreateSnapshot().Images).AnchorRow);

        engine.SetScrollOffset(engine.HistoryCount);

        Assert.Equal(0, Assert.Single(engine.CreateSnapshot().Images).AnchorRow);
    }

    [Fact]
    public void ImageAnchorRemapsAcrossReflow()
    {
        var engine = new TerminalEngine(6, 3, historySize: 10);
        engine.Feed("abcd\u001b[?80l\u001bP7q~\u001b\\efgh");

        engine.Resize(3, 4, 8, 16);

        var image = Assert.Single(engine.CreateSnapshot(includeHistory: true).Images);
        Assert.Equal(1, image.AnchorRow);
        Assert.Equal(1, image.AnchorColumn);
    }

    [Fact]
    public void ImageAnchorRetainsOffsetAcrossMixedRenditionReflowSegments()
    {
        var engine = new TerminalEngine(10, 4, historySize: 10);
        engine.Feed("\u001b#6ABCDEF\u001b[?80l\u001bP7q~\u001b\\");

        engine.Resize(8, 4, 8, 16);

        var image = Assert.Single(engine.CreateSnapshot(includeHistory: true).Images);
        Assert.Equal(2, image.AnchorRow);
        Assert.Equal(1, image.AnchorColumn);
    }

    [Fact]
    public void BlankCellImageAnchorRemainsOwnedAcrossReflow()
    {
        var engine = new TerminalEngine(6, 3, historySize: 10);
        engine.Feed("\u001b[1;5H\u001b[?80l\u001bP7q~\u001b\\");

        engine.Resize(3, 4);

        var image = Assert.Single(engine.CreateSnapshot(includeHistory: true).Images);
        Assert.Equal(1, image.AnchorRow);
        Assert.Equal(1, image.AnchorColumn);
    }

    [Fact]
    public void ImageIsRemovedWhenOwningMainBufferSegmentIsEvicted()
    {
        var engine = new TerminalEngine(10, 2, historySize: 1);
        engine.Feed("\u001b[?80l\u001bP7q~\u001b\\");

        engine.Feed("\r\none\r\ntwo\r\nthree");

        Assert.Empty(engine.Images);
        Assert.Empty(engine.CreateSnapshot(includeHistory: true).Images);
    }

    [Fact]
    public void ImageIsRemovedWhenOwningAlternateBufferLineIsEvicted()
    {
        var engine = new TerminalEngine(10, 2);
        engine.Feed("\u001b[?1049h\u001b[?80l\u001bP7q~\u001b\\");

        engine.Feed("\r\none\r\ntwo");

        Assert.Empty(engine.Images);
    }

    [Fact]
    public void AlternateBufferImageAnchorRemapsAcrossResize()
    {
        var engine = new TerminalEngine(6, 3);
        engine.Feed("\u001b[?1049habcd\u001b[?80l\u001bP7q~\u001b\\efgh");

        engine.Resize(3, 4);

        var image = Assert.Single(engine.CreateSnapshot(includeHistory: true).Images);
        Assert.True(image.AlternateBuffer);
        Assert.Equal(1, image.AnchorRow);
        Assert.Equal(1, image.AnchorColumn);
    }

    [Fact]
    public void DecsdmDisplayModeUsesHomeAndPreservesCursor()
    {
        var engine = new TerminalEngine(10, 2);
        engine.Resize(10, 2, 8, 16);
        engine.Feed("\u001b[2;4H\u001b[?80h\u001bP7q~-~-~-~-~\u001b\\");

        var image = Assert.Single(engine.CreateSnapshot(includeHistory: true).Images);
        Assert.Equal(0, image.AnchorColumn);
        Assert.Equal(0, image.AnchorRow);
        Assert.Equal(3, engine.CursorX);
        Assert.Equal(1, engine.CursorY);
        Assert.Equal(0, engine.HistoryCount);
        Assert.Equal(new TerminalImageCellGeometry(8, 16), image.CellGeometry);
    }

    [Fact]
    public void DecsdmScrollingModeAdvancesCursorAndScrollsAtMargin()
    {
        var engine = new TerminalEngine(10, 2, historySize: 10);
        engine.Resize(10, 2, 8, 16);
        engine.Feed("\u001b[2;4H\u001b[?80l\u001bP7q~-~-~-~-~\u001b\\");

        var image = Assert.Single(engine.CreateSnapshot(includeHistory: true).Images);
        Assert.Equal(3, image.AnchorColumn);
        Assert.Equal(1, image.AnchorRow);
        Assert.Equal(3, engine.CursorX);
        Assert.Equal(1, engine.CursorY);
        Assert.Equal(1, engine.HistoryCount);
    }

    [Fact]
    public void DecsdmScrollingModeHonorsRasterExtent()
    {
        var engine = new TerminalEngine(10, 2, historySize: 10);
        engine.Feed("\u001b[?80l\u001bP7q\"1;1;1;100~\u001b\\");

        Assert.Equal(3, engine.HistoryCount);
        Assert.Equal(1, engine.CursorY);
        Assert.Equal(0, Assert.Single(engine.CreateSnapshot(includeHistory: true).Images).AnchorRow);
    }

    [Fact]
    public void DecsdmModeReportsTrackSetAndReset()
    {
        var engine = new TerminalEngine();
        var responses = new List<string>();
        engine.ResponseReady += (_, value) => responses.Add(Encoding.ASCII.GetString(value));

        engine.Feed("\u001b[?80$p\u001b[?80l\u001b[?80$p\u001b[?80h\u001b[?80$p");

        Assert.Equal(
            ["\u001b[?80;1$y", "\u001b[?80;2$y", "\u001b[?80;1$y"],
            responses);
    }

    [Fact]
    public void BuiltInEngineAdvertisesEveryImplementedImageProtocol()
    {
        using var engine = new TerminalEngine();

        Assert.True(engine.Capabilities.HasFlag(TerminalEngineCapabilities.SixelImages));
        Assert.True(engine.Capabilities.HasFlag(TerminalEngineCapabilities.Iterm2Images));
        Assert.True(engine.Capabilities.HasFlag(TerminalEngineCapabilities.ConEmuImages));
    }

    [Fact]
    public void EnteringClearedAlternateBufferRemovesOldAlternateImages()
    {
        var engine = new TerminalEngine();
        engine.Feed("\u001b[?1049h\u001bP7q~\u001b\\\u001b[?1049l");
        Assert.Single(engine.Images);

        engine.Feed("\u001b[?1049h");

        Assert.Empty(engine.Images);
        Assert.Empty(engine.CreateSnapshot().Images);
    }
}
