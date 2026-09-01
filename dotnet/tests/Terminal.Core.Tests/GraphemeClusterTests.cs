using Microsoft.Terminal.Core;
using Xunit;

namespace Terminal.Core.Tests;

public sealed class GraphemeClusterTests
{
    [Fact]
    public void EmojiZwjSequenceOccupiesTwoCells()
    {
        var engine = new TerminalEngine(10, 2);

        engine.Feed("👩‍💻");

        Assert.Equal(2, engine.CursorX);
        Assert.Equal("👩‍💻", engine.Buffer.GetCell(0, 0).Text);
        Assert.True(engine.Buffer.GetCell(1, 0).IsWideContinuation);
    }

    [Fact]
    public void EmojiSkinToneModifierDoesNotAdvanceCursor()
    {
        var engine = new TerminalEngine(10, 2);

        engine.Feed("👍🏽");

        Assert.Equal(2, engine.CursorX);
        Assert.Equal("👍🏽", engine.Buffer.GetCell(0, 0).Text);
    }

    [Fact]
    public void RegionalIndicatorPairOccupiesTwoCells()
    {
        var engine = new TerminalEngine(10, 2);

        engine.Feed("🇨🇦");

        Assert.Equal(2, engine.CursorX);
        Assert.Equal("🇨🇦", engine.Buffer.GetCell(0, 0).Text);
    }

    [Fact]
    public void ConsecutiveFlagsRemainSeparateClusters()
    {
        var engine = new TerminalEngine(10, 2);

        engine.Feed("🇨🇦🇺🇸");

        Assert.Equal(4, engine.CursorX);
        Assert.Equal("🇨🇦", engine.Buffer.GetCell(0, 0).Text);
        Assert.Equal("🇺🇸", engine.Buffer.GetCell(2, 0).Text);
    }

    [Fact]
    public void ReflowPreservesJoinedEmojiWidth()
    {
        var engine = new TerminalEngine(8, 2);
        engine.Feed("A👩‍💻B");

        engine.Resize(3, 3);

        Assert.Equal("A👩‍💻", engine.Buffer.GetRow(0)[0].Text + engine.Buffer.GetRow(0)[1].Text);
        Assert.True(engine.Buffer.GetRow(0)[2].IsWideContinuation);
        Assert.Equal("B", engine.Buffer.GetRow(1)[0].Text);
    }

    [Fact]
    public void ZwjDoesNotJoinOrdinaryLetters()
    {
        var engine = new TerminalEngine(10, 2);

        engine.Feed("A\u200DB");

        Assert.Equal(2, engine.CursorX);
        Assert.Equal("A\u200D", engine.Buffer.GetCell(0, 0).Text);
        Assert.Equal("B", engine.Buffer.GetCell(1, 0).Text);
    }

    [Fact]
    public void NarrowEmojiPrefixWidensJoinedCluster()
    {
        var engine = new TerminalEngine(10, 2);

        engine.Feed("❤️‍🔥");

        Assert.Equal(2, engine.CursorX);
        Assert.Equal("❤️‍🔥", engine.Buffer.GetCell(0, 0).Text);
        Assert.True(engine.Buffer.GetCell(1, 0).IsWideContinuation);
    }

    [Fact]
    public void InsertModeAddsOnlyFinalGraphemeWidth()
    {
        var engine = new TerminalEngine(10, 2);
        engine.Feed("AB");
        engine.Feed("\u001b[1;1H\u001b[4h🇨🇦");

        Assert.Equal("🇨🇦", engine.Buffer.GetCell(0, 0).Text);
        Assert.True(engine.Buffer.GetCell(1, 0).IsWideContinuation);
        Assert.Equal("A", engine.Buffer.GetCell(2, 0).Text);
        Assert.Equal("B", engine.Buffer.GetCell(3, 0).Text);
    }
}
