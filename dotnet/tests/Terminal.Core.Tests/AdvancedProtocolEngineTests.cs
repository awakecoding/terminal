using System.Text;
using Microsoft.Terminal.Core;
using Xunit;

namespace Terminal.Core.Tests;

public sealed class AdvancedProtocolEngineTests
{
    [Fact]
    public void SixelDcsAddsRendererNeutralOverlayAtCursor()
    {
        var engine = new TerminalEngine(80, 24);
        TerminalImageOverlay? raised = null;
        engine.ImageAdded += (_, image) => raised = image;
        engine.Feed("\u001b[3;4H\u001bP7;1q#1;2;100;0;0!2~\u001b\\");

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
        engine.Feed("\u001b[2;1H\u001bP7q~\u001b\\");
        Assert.Equal(1, Assert.Single(engine.CreateSnapshot().Images).AnchorRow);

        engine.Feed("\r\n");

        Assert.Equal(0, Assert.Single(engine.CreateSnapshot().Images).AnchorRow);
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
