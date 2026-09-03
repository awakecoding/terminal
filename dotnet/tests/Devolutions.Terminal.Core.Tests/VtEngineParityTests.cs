using System.Text;
using Devolutions.Terminal.Core;
using Xunit;

namespace Devolutions.Terminal.Core.Tests;

public sealed class VtEngineParityTests
{
    [Fact]
    public void EditingCommandsMutateOnlyExpectedCells()
    {
        var engine = new TerminalEngine(8, 3);
        engine.Feed("ABCDE\u001b[3G\u001b[2@xy");
        Assert.Equal("ABxyCDE", RowText(engine, 0));

        engine.Feed("\u001b[3G\u001b[2P");
        Assert.Equal("ABCDE", RowText(engine, 0));

        engine.Feed("\u001b[2G\u001b[2X");
        Assert.Equal("A  DE", RowText(engine, 0));
    }

    [Fact]
    public void InsertAndDeleteLinesRespectScrollMargins()
    {
        var engine = new TerminalEngine(5, 4);
        engine.Feed("111\r\n222\r\n333\r\n444");
        engine.Feed("\u001b[2;4r\u001b[3;1H\u001b[L");

        Assert.Equal("111", RowText(engine, 0));
        Assert.Equal("222", RowText(engine, 1));
        Assert.Equal(string.Empty, RowText(engine, 2));
        Assert.Equal("333", RowText(engine, 3));

        engine.Feed("\u001b[M");
        Assert.Equal("333", RowText(engine, 2));
        Assert.Equal(string.Empty, RowText(engine, 3));
    }

    [Theory]
    [InlineData("\u001b[c", "\u001b[?61;4;6;7;14;21;22;23;24;28;32;42c")]
    [InlineData("\u001b[>c", "\u001b[>0;10;1c")]
    [InlineData("\u001b[5n", "\u001b[0n")]
    [InlineData("\u001b[?6n", "\u001b[?1;1R")]
    public void DeviceReportsAreExact(string request, string expected)
    {
        var engine = new TerminalEngine(80, 24);
        var response = CaptureSingleResponse(engine, request);
        Assert.Equal(expected, response);
    }

    [Fact]
    public void CursorPositionReportHonorsOriginMode()
    {
        var engine = new TerminalEngine(20, 10);
        engine.Feed("\u001b[3;8r\u001b[?6h\u001b[2;4H");

        Assert.Equal("\u001b[2;4R", CaptureSingleResponse(engine, "\u001b[6n"));
    }

    [Fact]
    public void ModeQueriesReportEnabledDisabledAndUnsupported()
    {
        var engine = new TerminalEngine();

        Assert.Equal("\u001b[?7;1$y", CaptureSingleResponse(engine, "\u001b[?7$p"));
        engine.Feed("\u001b[?7l");
        Assert.Equal("\u001b[?7;2$y", CaptureSingleResponse(engine, "\u001b[?7$p"));
        Assert.Equal("\u001b[4;2$y", CaptureSingleResponse(engine, "\u001b[4$p"));
        Assert.Equal("\u001b[999;0$y", CaptureSingleResponse(engine, "\u001b[999$p"));
    }

    [Fact]
    public void SgrSupportsColonTrueColorAndClampsComponents()
    {
        var engine = new TerminalEngine();
        engine.Feed("\u001b[38:2::300:20:10mX");

        var color = engine.Buffer.GetCell(0, 0).Attributes.Foreground;
        Assert.Equal(TermColor.FromRgb(255, 20, 10), color);
    }

    [Fact]
    public void OscColorResourcesSetAndQueryUsingExactXtermFormat()
    {
        var engine = new TerminalEngine();
        engine.Feed("\u001b]4;200;#123456\u001b\\");
        Assert.Equal(0xFF123456u, engine.Scheme.Resolve(200));
        Assert.Equal(
            "\u001b]4;200;rgb:1212/3434/5656\u001b\\",
            CaptureSingleResponse(engine, "\u001b]4;200;?\u001b\\"));

        engine.Feed("\u001b]10;rgb:ffff/8000/0000\u0007");
        Assert.Equal(0xFFFF8000u, engine.Scheme.Foreground);
        Assert.Equal(
            "\u001b]10;rgb:ffff/8080/0000\u001b\\",
            CaptureSingleResponse(engine, "\u001b]10;?\u0007"));
    }

    [Fact]
    public void OscWorkingDirectoryAndHyperlinkMetadataAreRetained()
    {
        var engine = new TerminalEngine();
        engine.Feed("\u001b]7;file:///C:/src\u001b\\");
        engine.Feed("\u001b]8;id=docs;https://example.test/path\u001b\\link\u001b]8;;\u001b\\X");

        Assert.Equal("file:///C:/src", engine.WorkingDirectory);
        Assert.Equal("https://example.test/path", engine.Buffer.GetCell(0, 0).HyperlinkUri);
        Assert.Null(engine.Buffer.GetCell(4, 0).HyperlinkUri);
    }

    [Fact]
    public void OscUtf8AndStringTerminatorCanCrossFeedBoundaries()
    {
        var bytes = Encoding.UTF8.GetBytes("\u001b]2;Ω title\u001b\\");
        for (var split = 1; split < bytes.Length; split++)
        {
            var engine = new TerminalEngine();
            engine.Feed(bytes.AsSpan(0, split));
            engine.Feed(bytes.AsSpan(split));
            Assert.Equal("Ω title", engine.Title);
        }
    }

    private static string RowText(TerminalEngine engine, int row) =>
        string.Concat(engine.Buffer.GetRow(row).Where(cell => !cell.IsWideContinuation).Select(cell => cell.Text)).TrimEnd();

    private static string CaptureSingleResponse(TerminalEngine engine, string request)
    {
        byte[]? response = null;
        EventHandler<byte[]> handler = (_, value) => response = value;
        engine.ResponseReady += handler;
        engine.Feed(request);
        engine.ResponseReady -= handler;
        return Encoding.UTF8.GetString(Assert.IsType<byte[]>(response));
    }
}
