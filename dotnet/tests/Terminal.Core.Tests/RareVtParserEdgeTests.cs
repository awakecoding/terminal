using System.Text;
using Microsoft.Terminal.Core;
using Xunit;

namespace Terminal.Core.Tests;

public sealed class RareVtParserEdgeTests
{
    private const string RareStream =
        "\u001bP1;0;1!z4D\u001b\\" +
        "\u001b[1*z" +
        "\u001bP0;1;0;2;1;2;6;0{ B~\u001b\\" +
        "\u001b( B!" +
        "\u001b[?2l\u001bY#$X\u001b<" +
        "\u001b[65;1;1;1;2$x";

    [Fact]
    public void EveryChunkBoundaryMatchesSingleFeedForRareSequences()
    {
        var bytes = Encoding.ASCII.GetBytes(RareStream);
        var expected = Run(bytes, bytes.Length).CreateSnapshot();
        for (var split = 0; split <= bytes.Length; split++)
        {
            var engine = new TerminalEngine(20, 10);
            engine.Feed(bytes.AsSpan(0, split));
            engine.Feed(bytes.AsSpan(split));
            AssertBuffersEqual(expected.Buffer, engine.CreateSnapshot().Buffer);
            Assert.Equal(expected.Buffer.CursorX, engine.CursorX);
            Assert.Equal(expected.Buffer.CursorY, engine.CursorY);
            Assert.Equal(1, engine.MacroCount);
            Assert.Single(engine.DrcsGlyphs);
        }
    }

    [Fact]
    public void DeterministicRandomChunkingMatchesSingleFeed()
    {
        var bytes = Encoding.ASCII.GetBytes(RareStream);
        var expected = Run(bytes, bytes.Length).CreateSnapshot();
        for (var seed = 0; seed < 128; seed++)
        {
            var random = new Random(seed);
            var engine = new TerminalEngine(20, 10);
            for (var offset = 0; offset < bytes.Length;)
            {
                var count = Math.Min(random.Next(1, 9), bytes.Length - offset);
                engine.Feed(bytes.AsSpan(offset, count));
                offset += count;
            }

            AssertBuffersEqual(expected.Buffer, engine.CreateSnapshot().Buffer);
        }
    }

    [Theory]
    [InlineData("\u001b]2;ignored\u001b\u0018OK")]
    [InlineData("\u001bXignored\u0018OK")]
    [InlineData("\u001bXignored\u001b\u001aOK")]
    public void CancelAbortsStringStatesAndRecovers(string input)
    {
        var engine = new TerminalEngine();
        engine.Feed(input);
        Assert.Equal("OK", engine.Buffer.GetVisibleText().TrimEnd());
    }

    [Fact]
    public void DeterministicMalformedInputFuzzAlwaysCancelsAndRecovers()
    {
        for (var seed = 0; seed < 256; seed++)
        {
            var random = new Random(seed);
            var bytes = new byte[256];
            random.NextBytes(bytes);
            var engine = new TerminalEngine(20, 4);
            for (var offset = 0; offset < bytes.Length;)
            {
                var count = Math.Min(random.Next(1, 17), bytes.Length - offset);
                engine.Feed(bytes.AsSpan(offset, count));
                offset += count;
            }

            engine.Feed("\u0018OK");
            Assert.Contains("OK", engine.Buffer.GetVisibleText());
        }
    }

    private static TerminalEngine Run(byte[] bytes, int chunkSize)
    {
        var engine = new TerminalEngine(20, 10);
        for (var offset = 0; offset < bytes.Length; offset += chunkSize)
        {
            engine.Feed(bytes.AsSpan(offset, Math.Min(chunkSize, bytes.Length - offset)));
        }

        return engine;
    }

    private static void AssertBuffersEqual(TextBufferSnapshot expected, TextBufferSnapshot actual)
    {
        Assert.Equal(expected.Lines.Count, actual.Lines.Count);
        for (var row = 0; row < expected.Lines.Count; row++)
        {
            Assert.Equal(expected.Lines[row].Wrapped, actual.Lines[row].Wrapped);
            Assert.Equal(expected.Lines[row].Cells, actual.Lines[row].Cells);
        }
    }
}
