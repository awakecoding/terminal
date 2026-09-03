using Avalonia.Headless.XUnit;
using Microsoft.Terminal.Settings;
using WindowsTerminal.Routing;
using WindowsTerminal.Views;
using Xunit;

namespace WindowsTerminal.UI.Tests;

public sealed class WindowActionRegistrationTests
{
    [AvaloniaFact]
    public void RegistersMarkColorAndWindowDialogActions()
    {
        var window = new MainWindow();

        var expected = new[]
        {
            ShortcutAction.ScrollToMark,
            ShortcutAction.AddMark,
            ShortcutAction.ClearMark,
            ShortcutAction.ClearAllMarks,
            ShortcutAction.SetColorScheme,
            ShortcutAction.ColorSelection,
            ShortcutAction.OpenTabColorPicker,
            ShortcutAction.OpenTabRenamer,
            ShortcutAction.ExecuteCommandline,
            ShortcutAction.BreakIntoDebugger,
            ShortcutAction.IdentifyWindows,
            ShortcutAction.RenameWindow,
            ShortcutAction.OpenWindowRenamer,
            ShortcutAction.ShowContextMenu,
            ShortcutAction.OpenWorkspace,
            ShortcutAction.Workspaces,
            ShortcutAction.GlobalSummon,
            ShortcutAction.QuakeMode,
            ShortcutAction.OpenSystemMenu,
            ShortcutAction.ToggleShaderEffects,
        };

        Assert.All(expected, action => Assert.Contains(action, window.RegisteredActions));
    }

    [AvaloniaFact]
    public async Task RenameWindowUpdatesRoutingIdentityAndRejectsDuplicates()
    {
        var window = new MainWindow(
            7,
            "original",
            null,
            windowNameValidator: name => name != "duplicate");

        var renamed = await window.ActivateAsync(new TerminalWindowActivation(
            null,
            null,
            null,
            null,
            TerminalWindowLaunchMode.Default,
            [new ActionAndArgs(
                ShortcutAction.RenameWindow,
                new RenameWindowArgs("development"))]));
        var duplicate = await window.ActivateAsync(new TerminalWindowActivation(
            null,
            null,
            null,
            null,
            TerminalWindowLaunchMode.Default,
            [new ActionAndArgs(
                ShortcutAction.RenameWindow,
                new RenameWindowArgs("duplicate"))]));

        Assert.True(renamed.Succeeded);
        Assert.True(duplicate.Succeeded);
        Assert.Equal("development", window.WindowName);
    }

    [AvaloniaFact]
    public async Task RenameWindowRejectsPersistedWorkspaceCollision()
    {
        using var temporary = new TemporaryDirectory();
        var stateStore = new ApplicationStateStore(temporary.Path);
        stateStore.SaveWorkspace("existing", new WindowLayoutState());
        var window = new MainWindow(9, "original", null, stateStore: stateStore);

        var result = await window.ActivateAsync(new TerminalWindowActivation(
            null,
            null,
            null,
            null,
            TerminalWindowLaunchMode.Default,
            [new ActionAndArgs(
                ShortcutAction.RenameWindow,
                new RenameWindowArgs("existing"))]));

        Assert.True(result.Succeeded);
        Assert.Equal("original", window.WindowName);
        Assert.NotNull(stateStore.GetWorkspace("existing"));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"wt-ui-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    [AvaloniaFact]
    public async Task WorkspaceActionsListAndRequestNamedWorkspace()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"WindowsTerminal.UI.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var store = new ApplicationStateStore(directory);
            store.SaveWorkspace("Beta", new WindowLayoutState());
            store.SaveWorkspace("alpha", new WindowLayoutState());
            string? requested = null;
            var window = new MainWindow(
                1,
                string.Empty,
                null,
                stateStore: store,
                workspaceRequested: name => requested = name);

            Assert.Equal(["alpha", "Beta"], window.WorkspaceNames);

            var open = await window.ActivateAsync(new TerminalWindowActivation(
                null,
                null,
                null,
                null,
                TerminalWindowLaunchMode.Default,
                [new ActionAndArgs(
                    ShortcutAction.OpenWorkspace,
                    new OpenWorkspaceArgs("alpha"))]));
            var list = await window.ActivateAsync(new TerminalWindowActivation(
                null,
                null,
                null,
                null,
                TerminalWindowLaunchMode.Default,
                [new ActionAndArgs(ShortcutAction.Workspaces)]));

            Assert.True(open.Succeeded);
            Assert.True(list.Succeeded);
            Assert.Equal("alpha", requested);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
