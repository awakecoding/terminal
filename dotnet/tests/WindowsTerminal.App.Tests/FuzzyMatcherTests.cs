using WindowsTerminal.Actions;
using Xunit;

namespace WindowsTerminal.App.Tests;

public sealed class FuzzyMatcherTests
{
    [Fact]
    public void ContiguousAndBoundaryMatchesRankFirst()
    {
        var items = new[]
        {
            "Close other panes",
            "Color selection",
            "Clear buffer",
        };

        var ranked = FuzzyMatcher.Rank(items, "cb", static item => item);

        Assert.Equal("Clear buffer", ranked[0]);
    }

    [Fact]
    public void MatchingIsCaseInsensitive()
    {
        Assert.NotEqual(int.MinValue, FuzzyMatcher.Score("oS", "Open Settings"));
    }

    [Fact]
    public void NonMatchingItemsAreRemoved()
    {
        var ranked = FuzzyMatcher.Rank(
            new[] { "New tab", "Close tab" },
            "xyz",
            static item => item);

        Assert.Empty(ranked);
    }

    [Fact]
    public void EmptyQueryPreservesInputOrder()
    {
        var items = new[] { "b", "a" };

        Assert.Equal(items, FuzzyMatcher.Rank(items, string.Empty, static item => item));
    }

    [Fact]
    public void EqualScoresPreserveInputOrder()
    {
        var items = new[] { "Alpha one", "Alpha two" };

        Assert.Equal(items, FuzzyMatcher.Rank(items, "alpha", static item => item));
    }
}
