using WindowsTerminal.Panes;
using Xunit;

namespace WindowsTerminal.App.Tests;

public sealed class PaneTreeTests
{
    [Fact]
    public void StartsWithOneActiveLeaf()
    {
        var tree = new PaneTree<string>("one");

        Assert.Equal("one", tree.ActiveContent);
        Assert.Equal(["one"], tree.Leaves());
        Assert.Equal(1, tree.Count);
    }

    [Fact]
    public void SplitReplacesActiveLeafAndActivatesNewPane()
    {
        var tree = new PaneTree<string>("one");

        Assert.True(tree.SplitActive("two", PaneSplitOrientation.Vertical, 0.6));

        var split = Assert.IsType<PaneSplit<string>>(tree.Root);
        Assert.Equal(0.6, split.Ratio);
        Assert.Equal("two", tree.ActiveContent);
        Assert.Equal(["one", "two"], tree.Leaves());
    }

    [Fact]
    public void SplitCanPlaceNewPaneBeforeActivePane()
    {
        var tree = new PaneTree<string>("one");

        Assert.True(tree.SplitActive("two", PaneSplitOrientation.Vertical, 0.3, newContentFirst: true));

        var split = Assert.IsType<PaneSplit<string>>(tree.Root);
        Assert.Equal(0.3, split.Ratio);
        Assert.Equal(["two", "one"], tree.Leaves());
        Assert.Equal("two", tree.ActiveContent);
    }

    [Fact]
    public void NestedSplitsCalculateNormalizedBounds()
    {
        var tree = new PaneTree<string>("one");
        tree.SplitActive("two", PaneSplitOrientation.Vertical);
        tree.SplitActive("three", PaneSplitOrientation.Horizontal);

        var bounds = tree.CalculateBounds();

        Assert.Equal(new PaneBounds(0, 0, 0.5, 1), bounds["one"]);
        Assert.Equal(new PaneBounds(0.5, 0, 0.5, 0.5), bounds["two"]);
        Assert.Equal(new PaneBounds(0.5, 0.5, 0.5, 0.5), bounds["three"]);
    }

    [Theory]
    [InlineData(PaneDirection.Left, "one")]
    [InlineData(PaneDirection.Up, "two")]
    public void DirectionalFocusChoosesNearestPane(PaneDirection direction, string expected)
    {
        var tree = new PaneTree<string>("one");
        tree.SplitActive("two", PaneSplitOrientation.Vertical);
        tree.SplitActive("three", PaneSplitOrientation.Horizontal);

        Assert.True(tree.MoveFocus(direction));
        Assert.Equal(expected, tree.ActiveContent);
    }

    [Fact]
    public void ClosingPaneCollapsesItsParentSplit()
    {
        var tree = new PaneTree<string>("one");
        tree.SplitActive("two", PaneSplitOrientation.Vertical);
        tree.SplitActive("three", PaneSplitOrientation.Horizontal);

        Assert.True(tree.Close("two"));

        Assert.Equal(["one", "three"], tree.Leaves());
        var root = Assert.IsType<PaneSplit<string>>(tree.Root);
        Assert.IsType<PaneLeaf<string>>(root.Second);
    }

    [Fact]
    public void ClosingNestedActivePaneFocusesCollapsedSibling()
    {
        var tree = new PaneTree<string>("one");
        tree.SplitActive("two", PaneSplitOrientation.Vertical);
        tree.SplitActive("three", PaneSplitOrientation.Horizontal);
        tree.Activate("two");

        tree.Close("two");

        Assert.Equal("three", tree.ActiveContent);
    }

    [Fact]
    public void ClosingLastPaneEmptiesTree()
    {
        var tree = new PaneTree<string>("one");

        Assert.True(tree.Close("one"));

        Assert.Null(tree.Root);
        Assert.Null(tree.ActiveContent);
        Assert.Equal(0, tree.Count);
    }

    [Fact]
    public void ResizeChangesNearestMatchingSplitAndClampsRatio()
    {
        var tree = new PaneTree<string>("one");
        tree.SplitActive("two", PaneSplitOrientation.Vertical);

        Assert.True(tree.ResizeActive(PaneDirection.Left, 1));

        var split = Assert.IsType<PaneSplit<string>>(tree.Root);
        Assert.Equal(0.1, split.Ratio);
    }

    [Fact]
    public void ResizeDirectionDoesNotDependOnFocusedChild()
    {
        var tree = new PaneTree<string>("one");
        tree.SplitActive("two", PaneSplitOrientation.Vertical);

        tree.ResizeActive(PaneDirection.Right, 0.1);

        Assert.Equal(0.6, Assert.IsType<PaneSplit<string>>(tree.Root).Ratio, precision: 5);
    }

    [Fact]
    public void SplitterRatioCanBePersisted()
    {
        var tree = new PaneTree<string>("one");
        tree.SplitActive("two", PaneSplitOrientation.Vertical);
        var split = Assert.IsType<PaneSplit<string>>(tree.Root);

        Assert.True(tree.SetSplitRatio(split, 0.7));

        Assert.Equal(0.7, Assert.IsType<PaneSplit<string>>(tree.Root).Ratio);
    }

    [Fact]
    public void ZoomTogglesOnlyWhenMultiplePanesExist()
    {
        var tree = new PaneTree<string>("one");
        Assert.False(tree.ToggleZoom());
        tree.SplitActive("two", PaneSplitOrientation.Vertical);

        Assert.True(tree.ToggleZoom());
        Assert.Equal("two", tree.ZoomedContent);
        Assert.True(tree.ToggleZoom());
        Assert.Null(tree.ZoomedContent);
    }

    [Fact]
    public void RejectsDuplicateContentAndInvalidActivation()
    {
        var tree = new PaneTree<string>("one");

        Assert.False(tree.SplitActive("one", PaneSplitOrientation.Horizontal));
        Assert.False(tree.Activate("missing"));
        Assert.False(tree.Close("missing"));
    }

    [Fact]
    public void ToggleSplitOrientationChangesNearestActiveSplit()
    {
        var tree = new PaneTree<string>("one");
        tree.SplitActive("two", PaneSplitOrientation.Vertical);
        tree.SplitActive("three", PaneSplitOrientation.Horizontal);

        Assert.True(tree.ToggleActiveSplitOrientation());

        var root = Assert.IsType<PaneSplit<string>>(tree.Root);
        Assert.Equal(PaneSplitOrientation.Vertical, Assert.IsType<PaneSplit<string>>(root.Second).Orientation);
    }

    [Fact]
    public void CloseOthersRetainsOnlyActivePane()
    {
        var tree = new PaneTree<string>("one");
        tree.SplitActive("two", PaneSplitOrientation.Vertical);
        tree.SplitActive("three", PaneSplitOrientation.Horizontal);

        var removed = tree.CloseOthers();

        Assert.Equal(["one", "two"], removed);
        Assert.Equal(["three"], tree.Leaves());
        Assert.Equal("three", tree.ActiveContent);
    }

    [Fact]
    public void FocusCanMoveInLeafOrder()
    {
        var tree = new PaneTree<string>("one");
        tree.SplitActive("two", PaneSplitOrientation.Vertical);
        tree.SplitActive("three", PaneSplitOrientation.Horizontal);

        Assert.True(tree.MoveFocusInOrder(1));
        Assert.Equal("one", tree.ActiveContent);
        Assert.True(tree.MoveFocusInOrder(-1));
        Assert.Equal("three", tree.ActiveContent);
        Assert.True(tree.FocusFirst());
        Assert.Equal("one", tree.ActiveContent);
    }
}
