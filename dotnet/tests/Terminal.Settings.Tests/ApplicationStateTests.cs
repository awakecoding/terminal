using Microsoft.Terminal.Settings;
using Xunit;

namespace Terminal.Settings.Tests;

public sealed class ApplicationStateTests
{
    [Fact]
    public void MissingStateStartsEmpty()
    {
        using var temporary = new TemporaryDirectory();

        var store = new ApplicationStateStore(temporary.Path);

        Assert.Empty(store.Data.GeneratedProfiles);
        Assert.Empty(store.Data.PersistedWorkspaces);
        Assert.Null(store.LastDiagnostic);
    }

    [Fact]
    public void StateRoundTripsAllFieldGroups()
    {
        using var temporary = new TemporaryDirectory();
        var profileId = Guid.NewGuid();
        var store = new ApplicationStateStore(temporary.Path);
        store.Data.SettingsHash = "hash";
        store.Data.GeneratedProfiles.Add(profileId);
        store.Data.RecentCommands.Add("command");
        store.Data.DismissedMessages.Add("message");
        store.Data.AllowedCommandlines.Add("cmd.exe");
        store.Data.DismissedBadges.Add("badge");
        store.Data.SshFolderGenerated = true;
        store.AppendPersistedWindowLayout(new WindowLayoutState { InitialPosition = "10,20" });
        store.SaveWorkspace("workspace", new WindowLayoutState { LaunchMode = LaunchMode.Maximized });

        store.Save();
        var reloaded = new ApplicationStateStore(temporary.Path);

        Assert.Equal("hash", reloaded.Data.SettingsHash);
        Assert.Contains(profileId, reloaded.Data.GeneratedProfiles);
        Assert.Equal(["command"], reloaded.Data.RecentCommands);
        Assert.Equal(["message"], reloaded.Data.DismissedMessages);
        Assert.Equal(["cmd.exe"], reloaded.Data.AllowedCommandlines);
        Assert.Contains("badge", reloaded.Data.DismissedBadges);
        Assert.True(reloaded.Data.SshFolderGenerated);
        Assert.Equal("10,20", Assert.Single(reloaded.Data.PersistedWindowLayouts).InitialPosition);
        Assert.Equal(LaunchMode.Maximized, reloaded.Data.PersistedWorkspaces["workspace"].LaunchMode);
    }

    [Fact]
    public void MalformedStateIsDiscardedWithDiagnostic()
    {
        using var temporary = new TemporaryDirectory();
        File.WriteAllText(System.IO.Path.Combine(temporary.Path, "state.json"), "{ invalid");

        var store = new ApplicationStateStore(temporary.Path);

        Assert.Empty(store.Data.GeneratedProfiles);
        Assert.Equal("InvalidApplicationState", store.LastDiagnostic?.Code);
    }

    [Fact]
    public void NullRequiredCollectionIsDiscardedWithDiagnostic()
    {
        using var temporary = new TemporaryDirectory();
        File.WriteAllText(
            System.IO.Path.Combine(temporary.Path, "state.json"),
            """{ "recentCommands": null }""");

        var store = new ApplicationStateStore(temporary.Path);

        Assert.Empty(store.Data.RecentCommands);
        Assert.Equal("InvalidApplicationState", store.LastDiagnostic?.Code);
    }

    [Fact]
    public void ResetDeletesStateAndClearsMemory()
    {
        using var temporary = new TemporaryDirectory();
        var store = new ApplicationStateStore(temporary.Path);
        store.Data.RecentCommands.Add("command");
        store.Save();

        store.Reset();

        Assert.False(File.Exists(store.StatePath));
        Assert.Empty(store.Data.RecentCommands);
    }

    [Fact]
    public void WorkspaceOperationsMatchUpstreamAtomicSemantics()
    {
        using var temporary = new TemporaryDirectory();
        var store = new ApplicationStateStore(temporary.Path);
        var layout = new WindowLayoutState { InitialPosition = "1,2" };

        store.SaveWorkspace("old", layout);
        Assert.True(store.RenameWorkspace("old", "new"));
        Assert.False(store.RenameWorkspace("missing", "other"));
        Assert.Equal("1,2", store.TakeWorkspace("new")?.InitialPosition);
        Assert.Null(store.TakeWorkspace("new"));
        Assert.False(store.RemoveWorkspace("new"));
    }

    [Fact]
    public void RenameToEmptyRemovesWorkspace()
    {
        using var temporary = new TemporaryDirectory();
        var store = new ApplicationStateStore(temporary.Path);
        store.SaveWorkspace("old", new WindowLayoutState());

        Assert.True(store.RenameWorkspace("old", string.Empty));
        Assert.Empty(store.Data.PersistedWorkspaces);
    }

    [Fact]
    public void WindowLayoutSlotSaveMergesLatestFile()
    {
        using var temporary = new TemporaryDirectory();
        var firstWindow = new ApplicationStateStore(temporary.Path);
        var secondWindow = new ApplicationStateStore(temporary.Path);

        firstWindow.SavePersistedWindowLayout(0, new WindowLayoutState { InitialPosition = "1,1" });
        secondWindow.SavePersistedWindowLayout(1, new WindowLayoutState { InitialPosition = "2,2" });

        var restored = new ApplicationStateStore(temporary.Path);
        Assert.Equal("1,1", restored.Data.PersistedWindowLayouts[0].InitialPosition);
        Assert.Equal("2,2", restored.Data.PersistedWindowLayouts[1].InitialPosition);
    }

    [Fact]
    public async Task ConcurrentWindowLayoutSavesRetainEverySlot()
    {
        using var temporary = new TemporaryDirectory();
        var stores = Enumerable.Range(0, 8)
            .Select(_ => new ApplicationStateStore(temporary.Path))
            .ToArray();

        await Task.WhenAll(stores.Select((store, index) => Task.Run(() =>
            store.SavePersistedWindowLayout(
                index,
                new WindowLayoutState { InitialPosition = $"{index},{index}" }))));

        var restored = new ApplicationStateStore(temporary.Path);
        Assert.Equal(8, restored.Data.PersistedWindowLayouts.Count);
        for (var index = 0; index < stores.Length; index++)
        {
            Assert.Equal($"{index},{index}", restored.Data.PersistedWindowLayouts[index].InitialPosition);
        }
    }

    [Fact]
    public void TransactionalUpdatePreservesConcurrentWindowLayout()
    {
        using var temporary = new TemporaryDirectory();
        var profileStore = new ApplicationStateStore(temporary.Path);
        var windowStore = new ApplicationStateStore(temporary.Path);
        var profileId = Guid.NewGuid();

        windowStore.SavePersistedWindowLayout(
            0,
            new WindowLayoutState { InitialPosition = "5,6" });
        profileStore.Update(data => data.GeneratedProfiles.Add(profileId));

        var restored = new ApplicationStateStore(temporary.Path);
        Assert.Contains(profileId, restored.Data.GeneratedProfiles);
        Assert.Equal("5,6", Assert.Single(restored.Data.PersistedWindowLayouts).InitialPosition);
    }

    [Fact]
    public void WorkspaceWritesMergeLatestFileAndPersistImmediately()
    {
        using var temporary = new TemporaryDirectory();
        var first = new ApplicationStateStore(temporary.Path);
        var second = new ApplicationStateStore(temporary.Path);

        first.SaveWorkspace("zeta", new WindowLayoutState { InitialPosition = "1,1" });
        second.SaveWorkspace("Alpha", new WindowLayoutState { InitialPosition = "2,2" });

        var restored = new ApplicationStateStore(temporary.Path);
        Assert.Equal(["Alpha", "zeta"], restored.GetWorkspaceNames());
        Assert.Equal("1,1", restored.GetWorkspace("zeta")?.InitialPosition);
        Assert.Equal("2,2", restored.GetWorkspace("Alpha")?.InitialPosition);
    }

    [Fact]
    public void RejectedWorkspaceClaimDoesNotDeleteSavedData()
    {
        using var temporary = new TemporaryDirectory();
        var store = new ApplicationStateStore(temporary.Path);
        store.SaveWorkspace("legacy", new WindowLayoutState { InitialPosition = "3,4" });

        Assert.Null(store.TakeWorkspace("legacy", static _ => false));

        var restored = new ApplicationStateStore(temporary.Path);
        Assert.Equal("3,4", restored.GetWorkspace("legacy")?.InitialPosition);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"Terminal.Settings.Tests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
