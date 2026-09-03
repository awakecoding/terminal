using Microsoft.Terminal.Settings;
using System.Text.Json.Nodes;
using WindowsTerminal.Models;
using WindowsTerminal.Panes;
using WindowsTerminal.Routing;
using Xunit;

namespace WindowsTerminal.App.Tests;

public sealed class LayoutDescriptorTests
{
    [Theory]
    [InlineData("persistedLayout")]
    [InlineData("persistedWindowLayout")]
    [InlineData("persistedLayoutAndContent")]
    [InlineData("PERSISTEDLAYOUT")]
    public void AllPersistedFirstWindowPreferencesRestoreLayouts(string preference)
    {
        Assert.True(TerminalLayoutStateStore.IsPersistedLayoutPreference(preference));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("defaultProfile")]
    public void NonPersistedFirstWindowPreferencesDoNotRestoreLayouts(string? preference)
    {
        Assert.False(TerminalLayoutStateStore.IsPersistedLayoutPreference(preference));
    }

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
    public void NativeActionArrayIsRejectedWithDiagnosticAndRemainsUnchanged()
    {
        var native = new JsonArray
        {
            new JsonObject
            {
                ["command"] = new JsonObject { ["action"] = "newTab" },
            },
        };
        var before = native.ToJsonString();

        Assert.False(TerminalLayoutSerializer.TryDeserializeTabs(
            native,
            out var layout,
            out var diagnostic));

        Assert.Null(layout);
        Assert.Contains("Native Windows Terminal", diagnostic);
        Assert.Equal(before, native.ToJsonString());
    }

    [Theory]
    [InlineData(LaunchMode.Maximized)]
    [InlineData(LaunchMode.Fullscreen)]
    [InlineData(LaunchMode.Focus)]
    [InlineData(LaunchMode.MaximizedFocus)]
    public void ApplicationStatePreservesGeometryAndLaunchMode(LaunchMode launchMode)
    {
        var state = TerminalLayoutSerializer.ToApplicationState(
            ValidLayout(),
            "25,50",
            new WindowSizeState { Width = 1024, Height = 768 },
            launchMode);

        Assert.Equal("25,50", state.InitialPosition);
        Assert.Equal(1024, state.InitialSize!.Width);
        Assert.Equal(768, state.InitialSize.Height);
        Assert.Equal(launchMode, state.LaunchMode);
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
    public void InvalidDefaultSlotCanSurviveFallbackAndCloseWithoutReplacement()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"TerminalLayoutTests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var store = new ApplicationStateStore(directory);
            var invalid = new WindowLayoutState
            {
                TabLayout =
                [
                    new JsonObject
                    {
                        ["command"] = new JsonObject { ["action"] = "newTab" },
                    },
                ],
            };
            store.SavePersistedWindowLayout(0, invalid);

            var slot = TerminalLayoutStateStore.ReadWindowState(store, 1);
            Assert.NotNull(slot);
            Assert.False(TerminalLayoutStateStore.TryRead(slot, out _, out _));

            var fallback = ValidLayout();
            Assert.False(TerminalLayoutStateStore.TrySaveWindow(
                store,
                1,
                fallback,
                null,
                null,
                LaunchMode.Default,
                blockedByInvalidRestore: true));

            var reloaded = new ApplicationStateStore(directory);
            var preserved = Assert.Single(reloaded.Data.PersistedWindowLayouts);
            Assert.Same(preserved, TerminalLayoutStateStore.ReadWindowState(reloaded, 1));
            Assert.False(TerminalLayoutStateStore.TryRead(preserved, out _, out _));
            Assert.Equal(invalid.TabLayout.ToJsonString(), preserved.TabLayout.ToJsonString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ActivationResolverSelectsSlotsAndConsumesValidWorkspaces()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"TerminalLayoutTests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var store = new ApplicationStateStore(directory);
            var state = TerminalLayoutSerializer.ToApplicationState(ValidLayout());
            store.SavePersistedWindowLayout(2, state);
            store.SaveWorkspace("build", state);
            var fallback = new TerminalWindowActivation(
                null,
                null,
                null,
                null,
                TerminalWindowLaunchMode.Default,
                []);

            var slot = TerminalLayoutActivationResolver.ResolveSavedSlot(store, 2, fallback);
            var workspace = TerminalLayoutActivationResolver.ResolveWorkspace(
                store,
                "build",
                fallback);

            Assert.NotNull(slot.PersistedLayout);
            Assert.True(TerminalLayoutStateStore.TryRead(
                slot.PersistedLayout,
                out var slotLayout,
                out _));
            Assert.Equal(
                TerminalLayoutSerializer.DeserializeTabs(state.TabLayout)!.ActiveTabId,
                slotLayout!.ActiveTabId);
            Assert.NotNull(workspace.PersistedLayout);
            Assert.Equal("build", workspace.WorkspaceName);
            Assert.Null(new ApplicationStateStore(directory).GetWorkspace("build"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ActivationResolverDiagnosesUnsupportedWorkspaceWithoutConsumingIt()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"TerminalLayoutTests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var store = new ApplicationStateStore(directory);
            store.SaveWorkspace("native", new WindowLayoutState
            {
                TabLayout =
                [
                    new JsonObject
                    {
                        ["command"] = new JsonObject { ["action"] = "newTab" },
                    },
                ],
            });
            var fallback = new TerminalWindowActivation(
                null,
                null,
                null,
                null,
                TerminalWindowLaunchMode.Default,
                []);

            var resolved = TerminalLayoutActivationResolver.ResolveWorkspace(
                store,
                "native",
                fallback);

            Assert.Null(resolved.PersistedLayout);
            Assert.Contains("Native Windows Terminal", resolved.PersistedLayoutDiagnostic);
            Assert.NotNull(new ApplicationStateStore(directory).GetWorkspace("native"));
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
