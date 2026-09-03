using Devolutions.Terminal;
using Devolutions.Terminal.Core;
using Xunit;

namespace Devolutions.Terminal.Control.Tests;

public sealed class TerminalSearchSessionTests
{
    [Fact]
    public void UpdateFindsMatchesAndSelectsFirst()
    {
        var engine = new TerminalEngine(20, 2);
        engine.Feed("one two one");
        var search = new TerminalSearchSession(engine);

        search.Update("one");

        Assert.Equal(2, search.Matches.Count);
        Assert.Equal(0, search.CurrentIndex);
        Assert.Equal(new BufferPosition(0, 0), search.Current?.Start);
    }

    [Fact]
    public void NavigationWrapsBothDirections()
    {
        var engine = new TerminalEngine(20, 2);
        engine.Feed("one two one");
        var search = new TerminalSearchSession(engine);
        search.Update("one");

        Assert.True(search.MoveNext(reverse: true));
        Assert.Equal(1, search.CurrentIndex);
        Assert.True(search.MoveNext());
        Assert.Equal(0, search.CurrentIndex);
    }

    [Fact]
    public void HistoricalMatchScrollsIntoView()
    {
        var engine = new TerminalEngine(10, 2, historySize: 10);
        engine.Feed("target\r\nsecond\r\nthird\r\nfourth");
        var search = new TerminalSearchSession(engine);

        search.Update("target");

        Assert.True(engine.Buffer.ScrollOffset > 0);
    }

    [Fact]
    public void EmptyQueryClearsResults()
    {
        var engine = new TerminalEngine(20, 2);
        engine.Feed("text");
        var search = new TerminalSearchSession(engine);
        search.Update("text");

        search.Update(" ");

        Assert.Empty(search.Matches);
        Assert.Null(search.Current);
    }

    [Fact]
    public void RaisesChangedForUpdatesNavigationAndClear()
    {
        var engine = new TerminalEngine(20, 2);
        engine.Feed("one one");
        var search = new TerminalSearchSession(engine);
        var changes = 0;
        search.Changed += (_, _) => changes++;

        search.Update("one");
        search.MoveNext();
        search.Clear();

        Assert.Equal(3, changes);
    }

    [Fact]
    public void RefreshRestoresSelectionAndRaisesOneFinalEvent()
    {
        var engine = new TerminalEngine(20, 2);
        engine.Feed("one two one");
        using var search = new TerminalSearchSession(engine);
        search.Update("one");
        search.MoveNext();
        var changes = 0;
        search.Changed += (_, _) => changes++;

        search.Refresh();

        Assert.Equal(1, search.CurrentIndex);
        Assert.Equal(new BufferPosition(0, 8), search.Current?.Start);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void NavigationRefreshesStaleMatches()
    {
        var engine = new TerminalEngine(20, 2);
        engine.Feed("one one");
        using var search = new TerminalSearchSession(engine);
        search.Update("one");
        engine.Reset();

        Assert.False(search.MoveNext());
        Assert.Empty(search.Matches);
        Assert.Null(search.Current);
    }

    [Fact]
    public void RefreshTracksSelectedLineAcrossHistoryEviction()
    {
        var engine = new TerminalEngine(10, 2, historySize: 3);
        engine.Feed("hit0\r\nhit1\r\nhit2\r\nhit3");
        using var search = new TerminalSearchSession(engine);
        search.Update("hit");
        search.MoveNext();
        search.MoveNext();
        Assert.Equal(2, search.CurrentIndex);

        engine.Feed("\r\nhit4");
        search.Refresh();

        var snapshot = engine.CreateSnapshot(includeHistory: true).Buffer;
        var selectedLine = snapshot.Lines[search.Current!.Value.Start.Line];
        var selectedText = string.Concat(selectedLine.Cells.Where(static cell => !cell.IsWideContinuation).Select(static cell => cell.Text));
        Assert.StartsWith("hit2", selectedText, StringComparison.Ordinal);
    }
}
