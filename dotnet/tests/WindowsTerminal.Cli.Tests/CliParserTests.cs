using Microsoft.Terminal.Settings;
using WindowsTerminal.Cli;
using Xunit;

namespace WindowsTerminal.Cli.Tests;

public sealed class CliParserTests
{
    [Fact]
    public void CommandLineTextPreservesQuotedArguments()
    {
        var parsed = new CliParser().ParseCommandLine(
            "new-tab --profile \"Windows PowerShell\" pwsh.exe -NoLogo");

        var invocation = Assert.IsType<CliInvocation>(parsed.Invocation);
        var action = Assert.Single(invocation.Actions);
        var terminal = Assert.IsType<NewTerminalArgs>(
            Assert.IsType<NewTabArgs>(action.Args).ContentArgs);
        Assert.Equal("Windows PowerShell", terminal.Profile);
        Assert.Equal("pwsh.exe -NoLogo", terminal.Commandline);
    }

    [Fact]
    public void InWindowCommandLineDoesNotBootstrapNewTab()
    {
        var parsed = new CliParser().ParseCommandLine(
            "focus-tab --target 2",
            ensureInitialTab: false);

        var invocation = Assert.IsType<CliInvocation>(parsed.Invocation);
        var action = Assert.Single(invocation.Actions);
        Assert.Equal(ShortcutAction.SwitchToTab, action.Action);
    }

    [Theory]
    [InlineData("new-tab")]
    [InlineData("nt")]
    public void NewTabOptionsMatchAppCommandlineArgs(string command)
    {
        var result = Parse(
            command,
            "--profile", "Windows PowerShell",
            "--sessionId", "f38b6f5d-e30d-4c16-bd25-85f02f1f2b9d",
            "--startingDirectory", @"c:\Foo",
            "--title", "Admin",
            "--tabColor", "#009999",
            "--suppressApplicationTitle",
            "--colorScheme", "Campbell",
            "--appendCommandLine",
            "--reloadEnvironment",
            "pwsh.exe", "-NoLogo");

        var action = Assert.Single(result.Actions);
        var terminal = Assert.IsType<NewTerminalArgs>(Assert.IsType<NewTabArgs>(action.Args).ContentArgs);
        Assert.Equal("Windows PowerShell", terminal.Profile);
        Assert.Equal(@"c:\Foo", terminal.StartingDirectory);
        Assert.Equal("Admin", terminal.TabTitle);
        Assert.Equal("#009999", terminal.TabColor);
        Assert.True(terminal.SuppressApplicationTitle);
        Assert.Equal("Campbell", terminal.ColorScheme);
        Assert.True(terminal.AppendCommandLine);
        Assert.True(terminal.ReloadEnvironmentVariables);
        Assert.Equal("pwsh.exe -NoLogo", terminal.Commandline);
    }

    [Fact]
    public void SemicolonCommandsConvertToStartupActions()
    {
        var result = Parse(
            "-w", "use-any",
            "new-tab", "-p", "cmd",
            ";",
            "split-pane", "-H", "-s", "0.3", "pwsh");

        Assert.Equal("use-any", result.TargetWindow);
        Assert.Collection(
            result.Actions,
            action => Assert.Equal(ShortcutAction.NewTab, action.Action),
            action =>
            {
                Assert.Equal(ShortcutAction.SplitPane, action.Action);
                var split = Assert.IsType<SplitPaneArgs>(action.Args);
                Assert.Equal(SplitDirection.Down, split.SplitDirection);
                Assert.Equal(0.3f, split.SplitSize);
            });
    }

    [Fact]
    public void EscapedDelimiterRemainsInCommandTail()
    {
        var result = Parse("new-tab", "pwsh", @"one\;two");
        var terminal = Assert.IsType<NewTerminalArgs>(
            Assert.IsType<NewTabArgs>(Assert.Single(result.Actions).Args).ContentArgs);
        Assert.Equal("pwsh one;two", terminal.Commandline);
    }

    [Fact]
    public void PaneAndTabCommandsMatchGoldenActions()
    {
        var result = Parse(
            "focus-tab", "--target", "2", ";",
            "move-focus", "left", ";",
            "move-pane", "--tab", "1", ";",
            "swap-pane", "right", ";",
            "focus-pane", "--target", "7");

        Assert.Equal(ShortcutAction.NewTab, result.Actions[0].Action);
        Assert.IsType<SwitchToTabArgs>(result.Actions[1].Args);
        Assert.Equal(FocusDirection.Left, Assert.IsType<MoveFocusArgs>(result.Actions[2].Args).FocusDirection);
        Assert.Equal(1u, Assert.IsType<MovePaneArgs>(result.Actions[3].Args).TabIndex);
        Assert.Equal(FocusDirection.Right, Assert.IsType<SwapPaneArgs>(result.Actions[4].Args).Direction);
        Assert.Equal(7u, Assert.IsType<FocusPaneArgs>(result.Actions[5].Args).Id);
    }

    [Fact]
    public void RootLaunchOptionsArePreserved()
    {
        var result = Parse("-Mf", "--pos", "10,20", "--size", "120,40");
        Assert.Equal(10, result.PositionX);
        Assert.Equal(20, result.PositionY);
        Assert.Equal(120, result.Columns);
        Assert.Equal(40, result.Rows);
        Assert.True(result.LaunchMode.HasFlag(CliLaunchMode.Maximized));
        Assert.True(result.LaunchMode.HasFlag(CliLaunchMode.Focus));
    }

    [Fact]
    public void CommandTerminatorStopsTerminalOptionParsing()
    {
        var result = Parse("new-tab", "--", "wsl", "-d", "Ubuntu", "--", "sleep", "10");
        var terminal = Assert.IsType<NewTerminalArgs>(
            Assert.IsType<NewTabArgs>(Assert.Single(result.Actions).Args).ContentArgs);
        Assert.Equal("wsl -d Ubuntu -- sleep 10", terminal.Commandline);
    }

    [Fact]
    public void HelpSwitchAfterTerminatorBelongsToTerminalCommand()
    {
        var result = Parse("new-tab", "--", "cmd.exe", "/?");
        var terminal = Assert.IsType<NewTerminalArgs>(
            Assert.IsType<NewTabArgs>(Assert.Single(result.Actions).Args).ContentArgs);
        Assert.Equal("cmd.exe /?", terminal.Commandline);
    }

    [Fact]
    public void HelpSwitchAfterImplicitCommandBelongsToTerminalCommand()
    {
        var result = Parse("cmd.exe", "/?");
        var terminal = Assert.IsType<NewTerminalArgs>(
            Assert.IsType<NewTabArgs>(Assert.Single(result.Actions).Args).ContentArgs);

        Assert.Equal("cmd.exe /?", terminal.Commandline);
    }

    [Fact]
    public void EmptyAndTrailingBackslashArgumentsAreQuotedLosslessly()
    {
        var result = Parse("new-tab", "--", "tool.exe", "", @"C:\Program Files\");
        var terminal = Assert.IsType<NewTerminalArgs>(
            Assert.IsType<NewTabArgs>(Assert.Single(result.Actions).Args).ContentArgs);
        Assert.Equal("tool.exe \"\" \"C:\\Program Files\\\\\"", terminal.Commandline);
    }

    [Fact]
    public void InvalidOptionHasDeterministicError()
    {
        var result = new CliParser().Parse(["--unknown"]);
        Assert.Equal(2, result.ExitCode);
        Assert.Equal("wt: Unknown command '--unknown'.", result.Message);
    }

    [Fact]
    public void InvocationRoundTripsWithoutReflectionSerialization()
    {
        var expected = Parse("-w", "named", "new-tab", "-p", "cmd", ";", "split-pane", "-V");
        var actual = CliInvocationSerializer.Deserialize(CliInvocationSerializer.Serialize(expected));
        Assert.Equal(expected.TargetWindow, actual.TargetWindow);
        Assert.Equal(
            expected.Actions.Select(ActionJson.Serialize),
            actual.Actions.Select(ActionJson.Serialize));
    }

    [Fact]
    public void SaveCommandProducesVersionedRequest()
    {
        var result = Parse("save", "--name", "Build", "--keychord", "ctrl+b", "dotnet", "build");
        Assert.Empty(result.Actions);
        Assert.Equal(new CliSaveRequest("Build", "ctrl+b", "dotnet build"), result.SaveRequest);
    }

    private static CliInvocation Parse(params string[] args)
    {
        var result = new CliParser().Parse(args);
        Assert.True(result.IsSuccess, result.Message);
        Assert.False(result.ShouldExit);
        return Assert.IsType<CliInvocation>(result.Invocation);
    }
}
