using Devolutions.Terminal.Core;
using Xunit;

namespace Devolutions.Terminal.Core.Tests;

public sealed class TextBufferSearchTests
{
    [Fact]
    public void FindsAllCaseInsensitiveMatches()
    {
        var engine = new TerminalEngine(20, 3);
        engine.Feed("Alpha alpha ALPHA");

        var matches = TextBufferSearch.FindAll(engine.CreateSnapshot().Buffer, "alpha");

        Assert.Equal(3, matches.Count);
        Assert.Equal(new BufferPosition(0, 0), matches[0].Start);
        Assert.Equal(new BufferPosition(0, 11), matches[1].End);
    }

    [Fact]
    public void HonorsCaseAndWholeWordOptions()
    {
        var engine = new TerminalEngine(30, 2);
        engine.Feed("cat catalog Cat");
        var snapshot = engine.CreateSnapshot().Buffer;

        var matches = TextBufferSearch.FindAll(
            snapshot,
            "cat",
            new TextSearchOptions(CaseSensitive: true, WholeWord: true));

        Assert.Single(matches);
        Assert.Equal(new BufferRange(
            new BufferPosition(0, 0),
            new BufferPosition(0, 3)), matches[0]);
    }

    [Fact]
    public void MapsWideAndCombiningGlyphsToCellColumns()
    {
        var engine = new TerminalEngine(12, 2);
        engine.Feed("A界e\u0301Z");

        var wide = Assert.Single(TextBufferSearch.FindAll(engine.CreateSnapshot().Buffer, "界"));
        var combining = Assert.Single(TextBufferSearch.FindAll(engine.CreateSnapshot().Buffer, "e\u0301"));

        Assert.Equal(new BufferRange(
            new BufferPosition(0, 1),
            new BufferPosition(0, 3)), wide);
        Assert.Equal(new BufferRange(
            new BufferPosition(0, 3),
            new BufferPosition(0, 4)), combining);
    }

    [Fact]
    public void SearchesHistorySnapshot()
    {
        var engine = new TerminalEngine(8, 2, historySize: 10);
        engine.Feed("first\r\nsecond\r\nthird");

        var matches = TextBufferSearch.FindAll(engine.CreateSnapshot(includeHistory: true).Buffer, "first");

        Assert.Single(matches);
        Assert.Equal(0, matches[0].Start.Line);
    }

    [Fact]
    public void FindsAcrossSoftWrappedRows()
    {
        var engine = new TerminalEngine(3, 3);
        engine.Feed("abcdef");

        var match = Assert.Single(TextBufferSearch.FindAll(engine.CreateSnapshot().Buffer, "abcdef"));

        Assert.Equal(new BufferPosition(0, 0), match.Start);
        Assert.Equal(new BufferPosition(1, 3), match.End);
    }

    [Fact]
    public void WholeWordUsesUnicodeRuneBoundaries()
    {
        var engine = new TerminalEngine(20, 2);
        engine.Feed("𐐀cat e\u0301");
        var snapshot = engine.CreateSnapshot().Buffer;

        Assert.Empty(TextBufferSearch.FindAll(
            snapshot,
            "cat",
            new TextSearchOptions(WholeWord: true)));
        Assert.Empty(TextBufferSearch.FindAll(
            snapshot,
            "e",
            new TextSearchOptions(WholeWord: true)));
    }

    [Fact]
    public void IgnoresWhitespaceOnlyQueries()
    {
        var engine = new TerminalEngine(20, 2);

        Assert.Empty(TextBufferSearch.FindAll(engine.CreateSnapshot().Buffer, " "));
    }

    [Fact]
    public void FindsNextAndPreviousWithWrap()
    {
        var engine = new TerminalEngine(20, 2);
        engine.Feed("one two one");
        var snapshot = engine.CreateSnapshot().Buffer;

        var next = TextBufferSearch.FindNext(snapshot, "one", new BufferPosition(0, 0));
        var wrapped = TextBufferSearch.FindNext(snapshot, "one", new BufferPosition(0, 8));
        var previous = TextBufferSearch.FindNext(snapshot, "one", new BufferPosition(0, 8), reverse: true);

        Assert.Equal(new BufferPosition(0, 8), next!.Value.Start);
        Assert.Equal(new BufferPosition(0, 0), wrapped!.Value.Start);
        Assert.Equal(new BufferPosition(0, 0), previous!.Value.Start);
    }

    [Fact]
    public void NoWrapReturnsNullAtEnd()
    {
        var engine = new TerminalEngine(20, 2);
        engine.Feed("only");

        var result = TextBufferSearch.FindNext(
            engine.CreateSnapshot().Buffer,
            "only",
            new BufferPosition(0, 0),
            options: new TextSearchOptions(Wrap: false));

        Assert.Null(result);
    }
}
