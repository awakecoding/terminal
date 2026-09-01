using WindowsTerminal.Models;
using Xunit;

namespace WindowsTerminal.App.Tests;

public sealed class TabCollectionTests
{
    [Fact]
    public void ReorderDuplicateCloseAndRestoreMaintainActiveTab()
    {
        var tabs = new TabCollection<Tab, Snapshot>();
        var one = new Tab("one");
        var two = new Tab("two");
        var three = new Tab("three");
        tabs.Add(one);
        tabs.Add(two);
        tabs.Add(three);

        Assert.True(tabs.Move(three, 0));
        var copy = tabs.Duplicate(one, tab => new Tab($"{tab.Title} copy"));
        Assert.Equal(["three", "one", "one copy", "two"], tabs.Items.Select(static tab => tab.Title));
        Assert.Same(copy, tabs.ActiveTab);

        Assert.True(tabs.Close(copy, SnapshotOf));
        Assert.True(tabs.TryRestore(snapshot => new Tab(snapshot.Title), out var restored));
        Assert.Equal("one copy", restored!.Title);
        Assert.Same(restored, tabs.ActiveTab);
    }

    [Fact]
    public void CloseOthersAndAfterUseStableVisualOrder()
    {
        var tabs = CreateFour();
        var keep = tabs.Items[1];

        var after = tabs.CloseAfter(keep, SnapshotOf);
        Assert.Equal(["three", "four"], after.Select(static tab => tab.Title));
        Assert.Equal(["one", "two"], tabs.Items.Select(static tab => tab.Title));

        var others = tabs.CloseOthers(keep, SnapshotOf);
        Assert.Equal(["one"], others.Select(static tab => tab.Title));
        Assert.Equal(["two"], tabs.Items.Select(static tab => tab.Title));
    }

    [Fact]
    public void MruSwitcherAndSearchUseActivationHistory()
    {
        var tabs = CreateFour();
        var two = tabs.Items[1];
        tabs.Activate(two);

        Assert.True(tabs.SelectRelative(1, mostRecentlyUsed: true));
        Assert.Equal("four", tabs.ActiveTab!.Title);
        Assert.Equal(["two"], tabs.Search("TW", static tab => tab.Title).Select(static tab => tab.Title));
    }

    private static TabCollection<Tab, Snapshot> CreateFour()
    {
        var tabs = new TabCollection<Tab, Snapshot>();
        foreach (var title in new[] { "one", "two", "three", "four" })
        {
            tabs.Add(new Tab(title));
        }

        return tabs;
    }

    private static Snapshot SnapshotOf(Tab tab) => new(tab.Title);
    private sealed record Tab(string Title);
    private sealed record Snapshot(string Title);
}
