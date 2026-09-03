using System.Text;
using Devolutions.Terminal.Core;
using Xunit;

namespace Devolutions.Terminal.Core.Tests;

public sealed class VtParserBoundaryTests
{
    private const string DailyDriverStream =
        "start Ω界e\u0301\r\n" +
        "\u001b[31mred\u001b[0m" +
        "\u001b[2;3H@\u001b[?25l" +
        "\u001b]8;;https://example.test\u001b\\link\u001b]8;;\u001b\\" +
        "\u001b[?1049halt\u001b[?1049l";

    [Fact]
    public void EveryTwoChunkBoundaryMatchesSingleFeed()
    {
        var bytes = Encoding.UTF8.GetBytes(DailyDriverStream);
        var expected = Run(bytes, bytes.Length).CreateSnapshot(includeHistory: true);

        for (var split = 0; split <= bytes.Length; split++)
        {
            var actualEngine = new TerminalEngine(12, 4, historySize: 8);
            actualEngine.Feed(bytes.AsSpan(0, split));
            actualEngine.Feed(bytes.AsSpan(split));
            AssertSnapshotsEqual(expected, actualEngine.CreateSnapshot(includeHistory: true));
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(7)]
    public void FixedSizeChunkingMatchesSingleFeed(int chunkSize)
    {
        var bytes = Encoding.UTF8.GetBytes(DailyDriverStream);
        var expected = Run(bytes, bytes.Length).CreateSnapshot(includeHistory: true);
        var actual = Run(bytes, chunkSize).CreateSnapshot(includeHistory: true);
        AssertSnapshotsEqual(expected, actual);
    }

    [Fact]
    public void InvalidUtf8EmitsReplacementAndParserRecovers()
    {
        var engine = new TerminalEngine(8, 2);
        engine.Feed([0xF0, 0x28, 0x8C, 0x28, (byte)'X']);

        Assert.Equal(Rune.ReplacementChar, engine.Buffer.GetCell(0, 0).Rune);
        Assert.Equal('(', engine.Buffer.GetCell(1, 0).Rune.Value);
        Assert.Equal(Rune.ReplacementChar, engine.Buffer.GetCell(2, 0).Rune);
        Assert.Equal('(', engine.Buffer.GetCell(3, 0).Rune.Value);
        Assert.Equal('X', engine.Buffer.GetCell(4, 0).Rune.Value);
    }

    [Fact]
    public void CancelAbortsCsiAndReturnsToGround()
    {
        var engine = new TerminalEngine(8, 2);
        engine.Feed("A\u001b[999\u0018B");

        Assert.Equal("AB", engine.Buffer.GetVisibleText().TrimEnd());
    }

    private static TerminalEngine Run(byte[] bytes, int chunkSize)
    {
        var engine = new TerminalEngine(12, 4, historySize: 8);
        for (var offset = 0; offset < bytes.Length; offset += chunkSize)
        {
            engine.Feed(bytes.AsSpan(offset, Math.Min(chunkSize, bytes.Length - offset)));
        }

        return engine;
    }

    private static void AssertSnapshotsEqual(TerminalSnapshot expected, TerminalSnapshot actual)
    {
        Assert.Equal(expected.Title, actual.Title);
        Assert.Equal(expected.WorkingDirectory, actual.WorkingDirectory);
        Assert.Equal(expected.CursorVisible, actual.CursorVisible);
        Assert.Equal(expected.Buffer.Columns, actual.Buffer.Columns);
        Assert.Equal(expected.Buffer.Rows, actual.Buffer.Rows);
        Assert.Equal(expected.Buffer.CursorX, actual.Buffer.CursorX);
        Assert.Equal(expected.Buffer.CursorY, actual.Buffer.CursorY);
        Assert.Equal(expected.Buffer.HistoryCount, actual.Buffer.HistoryCount);
        Assert.Equal(expected.Buffer.Lines.Count, actual.Buffer.Lines.Count);
        for (var y = 0; y < expected.Buffer.Lines.Count; y++)
        {
            Assert.Equal(expected.Buffer.Lines[y].Wrapped, actual.Buffer.Lines[y].Wrapped);
            Assert.Equal(expected.Buffer.Lines[y].Cells, actual.Buffer.Lines[y].Cells);
        }
    }
}
