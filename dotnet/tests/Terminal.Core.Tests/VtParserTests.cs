using System.Text;
using Microsoft.Terminal.Core;
using Xunit;

namespace Terminal.Core.Tests;

public sealed class VtParserTests
{
    [Fact]
    public void PrintsPlainText()
    {
        var engine = new TerminalEngine(80, 24);
        engine.Feed("Hello");
        Assert.Equal("Hello", VisibleTrimmed(engine));
        Assert.Equal(5, engine.CursorX);
    }

    [Fact]
    public void AppliesSgrColors()
    {
        var engine = new TerminalEngine(80, 24);
        engine.Feed("\u001b[31mR\u001b[0mX");
        var red = engine.Buffer.GetCell(0, 0);
        var reset = engine.Buffer.GetCell(1, 0);
        Assert.Equal(ColorKind.Indexed, red.Attributes.Foreground.Kind);
        Assert.Equal(1, red.Attributes.Foreground.Index);
        Assert.Equal(ColorKind.Default, reset.Attributes.Foreground.Kind);
    }

    [Fact]
    public void MovesCursorAndErases()
    {
        var engine = new TerminalEngine(80, 24);
        engine.Feed("ABC\u001b[1;1H\u001b[2K");
        Assert.True(engine.Buffer.GetCell(0, 0).IsBlank);
        Assert.True(engine.Buffer.GetCell(1, 0).IsBlank);
        Assert.True(engine.Buffer.GetCell(2, 0).IsBlank);
    }

    [Fact]
    public void CupIsOneBased()
    {
        var engine = new TerminalEngine(80, 24);
        engine.Feed("\u001b[10;20H");
        Assert.Equal(19, engine.CursorX);
        Assert.Equal(9, engine.CursorY);
    }

    [Fact]
    public void OscSetsTitle()
    {
        var engine = new TerminalEngine(80, 24);
        string? title = null;
        engine.TitleChanged += (_, value) => title = value;
        engine.Feed("\u001b]0;My Title\u0007");
        Assert.Equal("My Title", title);
        Assert.Equal("My Title", engine.Title);
    }

    [Fact]
    public void AltScreenSwapsBuffer()
    {
        var engine = new TerminalEngine(80, 24);
        engine.Feed("main");
        engine.Feed("\u001b[?1049h");
        engine.Feed("alt");
        Assert.Equal("alt", VisibleTrimmed(engine));
        engine.Feed("\u001b[?1049l");
        Assert.Equal("main", VisibleTrimmed(engine));
    }

    [Fact]
    public void TrueColorSgr()
    {
        var engine = new TerminalEngine(80, 24);
        engine.Feed("\u001b[38;2;10;20;30mA");
        var cell = engine.Buffer.GetCell(0, 0);
        Assert.Equal(ColorKind.Rgb, cell.Attributes.Foreground.Kind);
        Assert.Equal(10, cell.Attributes.Foreground.R);
        Assert.Equal(20, cell.Attributes.Foreground.G);
        Assert.Equal(30, cell.Attributes.Foreground.B);
    }

    [Fact]
    public void LineFeedAndCarriageReturn()
    {
        var engine = new TerminalEngine(80, 24);
        engine.Feed("A\r\nB");
        Assert.Equal('A', engine.Buffer.GetCell(0, 0).Rune.Value);
        Assert.Equal('B', engine.Buffer.GetCell(0, 1).Rune.Value);
    }

    [Fact]
    public void WrapAtEndOfLine()
    {
        var engine = new TerminalEngine(4, 3);
        engine.Feed("ABCDE");
        Assert.Equal('A', engine.Buffer.GetCell(0, 0).Rune.Value);
        Assert.Equal('D', engine.Buffer.GetCell(3, 0).Rune.Value);
        Assert.Equal('E', engine.Buffer.GetCell(0, 1).Rune.Value);
    }

    [Fact]
    public void Utf8Prints()
    {
        var engine = new TerminalEngine(80, 24);
        engine.Feed(Encoding.UTF8.GetBytes("Ω"));
        Assert.Equal("Ω", engine.Buffer.GetCell(0, 0).Rune.ToString());
    }

    private static string VisibleTrimmed(TerminalEngine engine) =>
        engine.Buffer.GetVisibleText().Replace("\n", "").TrimEnd();
}
