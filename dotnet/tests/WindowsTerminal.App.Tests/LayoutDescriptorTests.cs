using Microsoft.Terminal.Settings;
using System.Text.Json.Nodes;
using WindowsTerminal.Models;
using WindowsTerminal.Panes;
using Xunit;

namespace WindowsTerminal.App.Tests;

public sealed class LayoutDescriptorTests
{
    [Fact]
    public void WindowTabPaneLayoutRoundTripsThroughApplicationState()
    {
        var first = Session("one");
        var second = Session("two");
        var layout = new TerminalWindowLayoutDescriptor
        {
            ActiveTabId = Guid.NewGuid(),
            Tabs =
            [
                new()
                {
                    TabId = Guid.NewGuid(),
                    ActiveSessionId = second.SessionId,
                    ZoomedSessionId = second.SessionId,
                    Title = "work",
                    Color = "#123456",
                    Root = new()
                    {
                        Orientation = PaneSplitOrientation.Vertical,
                        Ratio = 0.333333333,
                        First = new() { Session = first },
                        Second = new()
                        {
                            Session = second,
                            Presentation = new()
                            {
                                IsReadOnly = true,
                                HasBellIndicator = true,
                                ProgressState = TerminalProgressState.Normal,
                                Progress = 0.42,
                            },
                        },
                    },
                },
            ],
        };
        layout.ActiveTabId = layout.Tabs[0].TabId;

        var state = TerminalLayoutSerializer.ToApplicationState(
            layout,
            "10,20",
            new WindowSizeState { Width = 1200, Height = 800 },
            LaunchMode.Maximized);
        var restored = TerminalLayoutSerializer.DeserializeTabs(state.TabLayout);

        Assert.NotNull(restored);
        Assert.Equal(layout.ActiveTabId, restored.ActiveTabId);
        var tab = Assert.Single(restored.Tabs);
        Assert.Equal("work", tab.Title);
        Assert.Equal(0.333333, tab.Root.Ratio);
        Assert.True(tab.Root.Second!.Presentation.IsReadOnly);
        Assert.Equal(0.42, tab.Root.Second.Presentation.Progress);
    }

    [Fact]
    public void InvalidOrUnknownLayoutsAreRejected()
    {
        Assert.Null(TerminalLayoutSerializer.DeserializeTabs([]));
        var invalid = ValidLayout();
        invalid.Version++;

        Assert.Throws<InvalidOperationException>(() =>
            TerminalLayoutSerializer.SerializeTabs(invalid));
    }

    [Fact]
    public void ExplicitNullRequiredMembersAreRejectedWithoutThrowing()
    {
        var document = Assert.IsType<JsonObject>(
            TerminalLayoutSerializer.SerializeTabs(ValidLayout())[0]);
        document["tabs"] = null;

        Assert.Null(TerminalLayoutSerializer.DeserializeTabs(
            [Assert.IsType<JsonObject>(document.DeepClone())]));
    }

    [Fact]
    public void DuplicateSessionIdentifiersAreRejected()
    {
        var session = Session("duplicate");
        var layout = ValidLayout();
        layout.Tabs[0].Root = new()
        {
            Orientation = PaneSplitOrientation.Horizontal,
            First = new() { Session = session },
            Second = new() { Session = session },
        };
        layout.Tabs[0].ActiveSessionId = session.SessionId;

        Assert.Throws<InvalidOperationException>(() =>
            TerminalLayoutSerializer.SerializeTabs(layout));
    }

    [Fact]
    public void LayoutPersistsThroughStateJson()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"TerminalLayoutTests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var store = new ApplicationStateStore(directory);
            var layout = ValidLayout();
            TerminalLayoutStateStore.SaveWindow(
                store,
                1,
                layout,
                "40,50",
                new WindowSizeState { Width = 900, Height = 600 },
                LaunchMode.Default);

            var reloaded = new ApplicationStateStore(directory);
            var restored = TerminalLayoutStateStore.ReadWindow(reloaded, 1);

            Assert.Equal(layout.ActiveTabId, restored?.ActiveTabId);
            Assert.Equal("40,50", Assert.Single(reloaded.Data.PersistedWindowLayouts).InitialPosition);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void MultipleWindowLayoutsDoNotOverwriteEachOther()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"TerminalLayoutTests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var store = new ApplicationStateStore(directory);
            var first = ValidLayout();
            var second = ValidLayout();
            TerminalLayoutStateStore.SaveWindow(store, 1, first, null, null, LaunchMode.Default);
            TerminalLayoutStateStore.SaveWindow(store, 2, second, null, null, LaunchMode.Maximized);

            var reloaded = new ApplicationStateStore(directory);

            Assert.Equal(first.ActiveTabId, TerminalLayoutStateStore.ReadWindow(reloaded, 1)?.ActiveTabId);
            Assert.Equal(second.ActiveTabId, TerminalLayoutStateStore.ReadWindow(reloaded, 2)?.ActiveTabId);
            Assert.Equal(2, reloaded.Data.PersistedWindowLayouts.Count);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void TearOffContractCarriesOnlyTransferableState()
    {
        var layout = ValidLayout();
        var request = new TabTearOffRequest(
            Guid.NewGuid(),
            7,
            layout.Tabs[0],
            new PixelPosition(100, 200));

        Assert.Equal(7, request.SourceWindowId);
        Assert.Equal("cmd.exe", request.Tab.Root.Session!.Commandline);
        Assert.Equal(request.TransferId, new TabTransferResult(request.TransferId, true).TransferId);
    }

    private static TerminalWindowLayoutDescriptor ValidLayout()
    {
        var session = Session("one");
        var tab = new TabLayoutDescriptor
        {
            ActiveSessionId = session.SessionId,
            Root = new() { Session = session },
        };
        return new()
        {
            ActiveTabId = tab.TabId,
            Tabs = [tab],
        };
    }

    private static TerminalSessionDescriptor Session(string name) =>
        new()
        {
            ProfileName = name,
            Commandline = "cmd.exe",
            StartingDirectory = @"C:\",
        };
}
