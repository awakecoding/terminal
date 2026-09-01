using Microsoft.Terminal.Control;
using Xunit;

namespace Terminal.Control.Tests;

public sealed class TermControlActionTests
{
    [Fact]
    public void SelectionActionsUpdateState()
    {
        var control = new TermControl();
        control.Engine.Feed("hello");

        control.SelectAll();
        Assert.True(control.HasSelection);

        control.ClearSelection();
        Assert.False(control.HasSelection);
    }

    [Fact]
    public void FindSelectsVisibleMatch()
    {
        var control = new TermControl();
        control.Engine.Feed("hello terminal");

        Assert.True(control.Find("terminal"));
        Assert.True(control.HasSelection);
        Assert.False(control.Find("missing"));
    }

    [Fact]
    public void FontActionsClampAndReset()
    {
        var control = new TermControl();

        control.AdjustFontSize(100);
        Assert.Equal(72, control.FontSize);

        control.ResetFontSize();
        Assert.Equal(12, control.FontSize);
    }

    [Fact]
    public void ScrollActionsUpdateViewport()
    {
        var control = new TermControl();
        control.Engine.Resize(10, 3);
        control.Engine.Feed("one\r\ntwo\r\nthree\r\nfour\r\nfive");

        control.ScrollToTop();
        Assert.Equal(control.Engine.Buffer.HistoryCount, control.Engine.Buffer.ScrollOffset);

        control.ScrollToBottom();
        Assert.Equal(0, control.Engine.Buffer.ScrollOffset);

        control.ScrollBy(1);
        Assert.Equal(1, control.Engine.Buffer.ScrollOffset);
    }
}
