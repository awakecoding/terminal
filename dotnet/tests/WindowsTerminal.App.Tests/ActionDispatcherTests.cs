using Avalonia.Input;
using Microsoft.Terminal.Settings;
using WindowsTerminal.Actions;
using Xunit;

namespace WindowsTerminal.App.Tests;

public sealed class ActionDispatcherTests
{
    [Fact]
    public async Task EveryUnregisteredActionReturnsExplicitUnsupportedResult()
    {
        var dispatcher = new ActionDispatcher();

        foreach (var definition in ActionCatalog.All)
        {
            var result = await dispatcher.DispatchAsync(new ActionAndArgs(definition.Action));

            Assert.Equal(ActionDispatchStatus.Unsupported, result.Status);
            Assert.Equal(definition.JsonName, result.Action);
        }
    }

    [Fact]
    public async Task RegisteredActionReportsScopeAndExecution()
    {
        var dispatcher = new ActionDispatcher();
        var executed = false;
        dispatcher.Register(
            ShortcutAction.CopyText,
            ActionScope.Control,
            _ => true,
            _ =>
            {
                executed = true;
                return Task.CompletedTask;
            });

        var result = await dispatcher.DispatchAsync(new ActionAndArgs(ShortcutAction.CopyText));

        Assert.True(executed);
        Assert.Equal(ActionDispatchStatus.Executed, result.Status);
        Assert.Equal(ActionScope.Control, result.Scope);
        Assert.True(dispatcher.CanExecute(new ActionAndArgs(ShortcutAction.CopyText)));
    }

    [Fact]
    public async Task DisabledActionDoesNotExecute()
    {
        var dispatcher = new ActionDispatcher();
        dispatcher.Register(
            ShortcutAction.ClosePane,
            ActionScope.Pane,
            _ => false,
            _ => throw new InvalidOperationException("must not run"));

        var result = await dispatcher.DispatchAsync(new ActionAndArgs(ShortcutAction.ClosePane));

        Assert.Equal(ActionDispatchStatus.Disabled, result.Status);
        Assert.False(dispatcher.CanExecute(new ActionAndArgs(ShortcutAction.ClosePane)));
    }

    [Fact]
    public async Task ExecutionFailureReturnsFailedResult()
    {
        var dispatcher = new ActionDispatcher();
        dispatcher.Register(
            ShortcutAction.PasteText,
            ActionScope.Control,
            _ => throw new InvalidOperationException("clipboard unavailable"));

        var result = await dispatcher.DispatchAsync(new ActionAndArgs(ShortcutAction.PasteText));

        Assert.Equal(ActionDispatchStatus.Failed, result.Status);
        Assert.Contains("clipboard unavailable", result.Message);
    }

    [Fact]
    public async Task MultipleActionsExecuteInOrderAndReturnFirstNonExecution()
    {
        var dispatcher = new ActionDispatcher();
        var executed = new List<ShortcutAction>();
        dispatcher.Register(
            ShortcutAction.CopyText,
            ActionScope.Control,
            action =>
            {
                executed.Add(action.Action);
                return Task.CompletedTask;
            });

        var multiple = new ActionAndArgs(
            ShortcutAction.MultipleActions,
            new MultipleActionsArgs(
            [
                new ActionAndArgs(ShortcutAction.CopyText),
                new ActionAndArgs(ShortcutAction.OpenAbout),
                new ActionAndArgs(ShortcutAction.CopyText),
            ]));

        var result = await dispatcher.DispatchAsync(multiple);

        Assert.Equal([ShortcutAction.CopyText], executed);
        Assert.Equal(ActionDispatchStatus.Unsupported, result.Status);
        Assert.Equal("openAbout", result.Action);
        Assert.False(dispatcher.CanExecute(multiple));
    }

    [Theory]
    [InlineData(Key.A, "a")]
    [InlineData(Key.D7, "7")]
    [InlineData(Key.NumPad3, "numpad3")]
    [InlineData(Key.F12, "f12")]
    [InlineData(Key.Escape, "esc")]
    [InlineData(Key.PageDown, "pagedown")]
    [InlineData(Key.OemPlus, "plus")]
    [InlineData(Key.OemOpenBrackets, "open_bracket")]
    [InlineData(Key.OemTilde, "backtick")]
    [InlineData(Key.Apps, "menu")]
    public void AvaloniaKeysUseActionMapNames(Key key, string expected) =>
        Assert.Equal(expected, AvaloniaKeyChord.GetKeyName(key));
}
