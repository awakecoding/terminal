using Devolutions.Terminal.Settings;
using Devolutions.Terminal.App.Models;
using Devolutions.Terminal.App.Panes;
using Xunit;

namespace Devolutions.Terminal.App.Tests;

public sealed class TabPaneSmokeTests
{
    [Fact]
    public void PracticalMultiTabPaneWorkflowSmoke()
    {
        var left = Session("left");
        var right = Session("right");
        var secondTab = Session("second tab");
        var first = new TabLayoutDescriptor
        {
            Title = "build",
            ActiveSessionId = right.SessionId,
            Root = new()
            {
                Orientation = PaneSplitOrientation.Vertical,
                Ratio = 0.6,
                First = new() { Session = left },
                Second = new() { Session = right },
            },
        };
        var second = new TabLayoutDescriptor
        {
            Title = "tests",
            ActiveSessionId = secondTab.SessionId,
            Root = new() { Session = secondTab },
        };
        var window = new TerminalWindowLayoutDescriptor
        {
            ActiveTabId = first.TabId,
            Tabs = [first, second],
        };

        var persisted = TerminalLayoutSerializer.ToApplicationState(window);
        var restored = Assert.IsType<TerminalWindowLayoutDescriptor>(
            TerminalLayoutSerializer.DeserializeTabs(persisted.TabLayout));
        var tabs = new TabCollection<TabLayoutDescriptor, TabLayoutDescriptor>();
        foreach (var tab in restored.Tabs)
        {
            tabs.Add(tab);
        }

        var restoredFirst = restored.Tabs[0];
        var restoredSecond = restored.Tabs[1];
        tabs.Activate(restoredFirst);
        Assert.True(tabs.Move(restoredSecond, 0));
        Assert.Equal(["tests", "build"], tabs.Items.Select(static tab => tab.Title));
        Assert.Equal(["build"], tabs.Search("uil", static tab => tab.Title).Select(static tab => tab.Title));
        Assert.True(tabs.Close(restoredFirst, static tab => tab));
        Assert.True(tabs.TryRestore(static tab => tab, out var reopened));
        Assert.Same(restoredFirst, reopened);
    }

    private static TerminalSessionDescriptor Session(string name) =>
        new()
        {
            ProfileName = name,
            Commandline = "cmd.exe",
            StartingDirectory = @"C:\",
        };
}
