using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Terminal.Settings;
using Xunit;

namespace Terminal.Settings.Tests;

public sealed class ActionMapTests
{
    private static readonly string[] ExpectedActionNames =
    [
        "copy", "paste", "openNewTabDropdown", "duplicateTab", "newTab", "closeWindow",
        "closeTab", "closePane", "nextTab", "prevTab", "sendInput", "splitPane",
        "toggleSplitOrientation", "togglePaneZoom", "switchToTab", "adjustFontSize",
        "resetFontSize", "scrollUp", "scrollDown", "scrollUpPage", "scrollDownPage",
        "scrollToTop", "scrollToBottom", "scrollToMark", "addMark", "clearMark",
        "clearAllMarks", "resizePane", "moveFocus", "movePane", "swapPane", "find",
        "toggleShaderEffects", "toggleFocusMode", "toggleFullscreen", "toggleAlwaysOnTop",
        "openSettings", "setFocusMode", "setFullScreen", "setMaximized", "setColorScheme",
        "setTabColor", "openTabColorPicker", "renameTab", "openTabRenamer", "wt",
        "commandPalette", "closeOtherTabs", "closeTabsAfter", "tabSearch", "moveTab",
        "breakIntoDebugger", "toggleReadOnlyMode", "enableReadOnlyMode",
        "disableReadOnlyMode", "findMatch", "newWindow", "identifyWindow", "identifyWindows",
        "renameWindow", "openWindowRenamer", "debugTerminalCwd", "searchWeb", "globalSummon",
        "quakeMode", "focusPane", "openSystemMenu", "exportBuffer", "clearBuffer",
        "multipleActions", "quit", "adjustOpacity", "restoreLastClosed", "selectAll",
        "selectCommand", "selectOutput", "markMode", "toggleBlockSelection",
        "switchSelectionEndpoint", "showSuggestions", "experimental.colorSelection",
        "showContextMenu", "expandSelectionToWord", "closeOtherPanes", "restartConnection",
        "toggleBroadcastInput", "experimental.openScratchpad", "openAbout", "quickFix",
        "openCWD", "openWorkspace", "workspaces",
    ];

    [Fact]
    public void PublicActionInventoryExactlyMatchesNativeList()
    {
        Assert.Equal(92, ActionCatalog.All.Count);
        Assert.Equal(ExpectedActionNames, ActionCatalog.All.Select(action => action.JsonName));
        Assert.Equal(92, ActionCatalog.All.Select(action => action.Action).Distinct().Count());
        Assert.Equal(93, Enum.GetValues<ShortcutAction>().Length); // Invalid + 92 public actions.
    }

    [Fact]
    public void EveryPublicActionJsonNameParses()
    {
        foreach (var definition in ActionCatalog.All)
        {
            var parsed = ActionJson.Parse(JsonValue.Create(definition.JsonName));
            Assert.Equal(definition.Action, parsed.Action);
            Assert.Equal(definition.JsonName, parsed.ActionName);
            Assert.Equal(
                definition.HasArguments || definition.Action == ShortcutAction.QuakeMode,
                parsed.Args is not null);
        }
    }

    [Fact]
    public void EveryWithArgsActionCreatesItsConcreteArgumentRecord()
    {
        var expected = new Dictionary<ShortcutAction, Type>
        {
            [ShortcutAction.AdjustFontSize] = typeof(AdjustFontSizeArgs),
            [ShortcutAction.CloseOtherTabs] = typeof(CloseOtherTabsArgs),
            [ShortcutAction.CloseTabsAfter] = typeof(CloseTabsAfterArgs),
            [ShortcutAction.CloseTab] = typeof(CloseTabArgs),
            [ShortcutAction.CopyText] = typeof(CopyTextArgs),
            [ShortcutAction.ExecuteCommandline] = typeof(ExecuteCommandlineArgs),
            [ShortcutAction.FindMatch] = typeof(FindMatchArgs),
            [ShortcutAction.SearchForText] = typeof(SearchForTextArgs),
            [ShortcutAction.GlobalSummon] = typeof(GlobalSummonArgs),
            [ShortcutAction.MoveFocus] = typeof(MoveFocusArgs),
            [ShortcutAction.MovePane] = typeof(MovePaneArgs),
            [ShortcutAction.SwapPane] = typeof(SwapPaneArgs),
            [ShortcutAction.MoveTab] = typeof(MoveTabArgs),
            [ShortcutAction.NewTab] = typeof(NewTabArgs),
            [ShortcutAction.NewWindow] = typeof(NewWindowArgs),
            [ShortcutAction.NextTab] = typeof(NextTabArgs),
            [ShortcutAction.OpenSettings] = typeof(OpenSettingsArgs),
            [ShortcutAction.SetFocusMode] = typeof(SetFocusModeArgs),
            [ShortcutAction.SetFullScreen] = typeof(SetFullScreenArgs),
            [ShortcutAction.SetMaximized] = typeof(SetMaximizedArgs),
            [ShortcutAction.PrevTab] = typeof(PrevTabArgs),
            [ShortcutAction.RenameTab] = typeof(RenameTabArgs),
            [ShortcutAction.RenameWindow] = typeof(RenameWindowArgs),
            [ShortcutAction.ResizePane] = typeof(ResizePaneArgs),
            [ShortcutAction.ScrollDown] = typeof(ScrollDownArgs),
            [ShortcutAction.ScrollUp] = typeof(ScrollUpArgs),
            [ShortcutAction.ScrollToMark] = typeof(ScrollToMarkArgs),
            [ShortcutAction.AddMark] = typeof(AddMarkArgs),
            [ShortcutAction.SendInput] = typeof(SendInputArgs),
            [ShortcutAction.SetColorScheme] = typeof(SetColorSchemeArgs),
            [ShortcutAction.SetTabColor] = typeof(SetTabColorArgs),
            [ShortcutAction.SplitPane] = typeof(SplitPaneArgs),
            [ShortcutAction.SwitchToTab] = typeof(SwitchToTabArgs),
            [ShortcutAction.ToggleCommandPalette] = typeof(ToggleCommandPaletteArgs),
            [ShortcutAction.FocusPane] = typeof(FocusPaneArgs),
            [ShortcutAction.ExportBuffer] = typeof(ExportBufferArgs),
            [ShortcutAction.ClearBuffer] = typeof(ClearBufferArgs),
            [ShortcutAction.MultipleActions] = typeof(MultipleActionsArgs),
            [ShortcutAction.AdjustOpacity] = typeof(AdjustOpacityArgs),
            [ShortcutAction.Suggestions] = typeof(SuggestionsArgs),
            [ShortcutAction.SelectCommand] = typeof(SelectCommandArgs),
            [ShortcutAction.SelectOutput] = typeof(SelectOutputArgs),
            [ShortcutAction.ColorSelection] = typeof(ColorSelectionArgs),
            [ShortcutAction.OpenWorkspace] = typeof(OpenWorkspaceArgs),
        };

        Assert.Equal(44, expected.Count);
        foreach (var definition in ActionCatalog.All.Where(action => action.HasArguments))
        {
            var parsed = ActionJson.Parse(JsonValue.Create(definition.JsonName));
            Assert.IsType(expected[definition.Action], parsed.Args);
        }
    }

    [Fact]
    public void ArgumentFieldsAliasesAndDefaultsMatchNativeModel()
    {
        var split = Assert.IsType<SplitPaneArgs>(ActionJson.Parse(
            """{"action":"splitPane","split":"vertical","splitMode":"duplicate","size":0.25,"profile":"pwsh","elevate":true}""").Args);
        Assert.Equal(SplitDirection.Right, split.SplitDirection);
        Assert.Equal(SplitType.Duplicate, split.SplitMode);
        Assert.Equal(0.25f, split.SplitSize);
        var terminal = Assert.IsType<NewTerminalArgs>(split.ContentArgs);
        Assert.Equal("pwsh", terminal.Profile);
        Assert.True(terminal.Elevate);

        var copy = Assert.IsType<CopyTextArgs>(ActionJson.Parse("\"copy\"").Args);
        Assert.True(copy.DismissSelection);
        Assert.False(copy.SingleLine);
        Assert.Null(copy.CopyFormatting);

        var summon = Assert.IsType<GlobalSummonArgs>(ActionJson.Parse("\"globalSummon\"").Args);
        Assert.Equal(DesktopBehavior.ToCurrent, summon.Desktop);
        Assert.Equal(MonitorBehavior.ToMouse, summon.Monitor);
        Assert.True(summon.ToggleVisibility);
    }

    [Fact]
    public void NewTerminalArgumentsOverrideAClonedProfile()
    {
        var profile = ProfileSettings.CreateCmd();
        var result = profile.WithOverrides(new NewTerminalArgs(
            Commandline: "/k echo ready",
            StartingDirectory: @"C:\work",
            TabTitle: "Build",
            TabColor: "#112233",
            AppendCommandLine: true,
            SuppressApplicationTitle: true,
            ColorScheme: "One Half Dark",
            ReloadEnvironmentVariables: false));

        Assert.NotSame(profile, result);
        Assert.Equal($"{profile.Commandline} /k echo ready", result.Commandline);
        Assert.Equal(@"C:\work", result.StartingDirectory);
        Assert.Equal("Build", result.TabTitle);
        Assert.Equal("#112233", result.TabColor);
        Assert.Equal("One Half Dark", result.ColorScheme);
        Assert.True(result.SuppressApplicationTitle);
        Assert.False(result.ReloadEnvironmentVariables);
        Assert.NotEqual(result.Commandline, profile.Commandline);
    }

    [Fact]
    public void EmbeddedActionsAndUserDefaultBindingsResolve()
    {
        var settings = SettingsService.CreateDefault();

        Assert.Equal(ShortcutAction.CopyText, settings.ActionMap.Resolve("ctrl+c")?.ActionAndArgs?.Action);
        Assert.Equal(ShortcutAction.PasteText, settings.ActionMap.Resolve("ctrl+v")?.ActionAndArgs?.Action);
        Assert.Equal(ShortcutAction.SplitPane, settings.ActionMap.Resolve("shift+alt+d")?.ActionAndArgs?.Action);
        Assert.Equal(ShortcutAction.QuakeMode, settings.ActionMap.Resolve("win+backtick")?.ActionAndArgs?.Action);
        Assert.NotNull(settings.ActionMap.GetActionByID("Terminal.OpenSettingsUI"));
        Assert.All(
            settings.ActionMap.BindingIds.Where(static binding => binding.Value.Length > 0),
            binding => Assert.NotNull(settings.ActionMap.GetActionByID(binding.Value)));
    }

    [Fact]
    public void SupportsExplicitAndGeneratedIdsNamesNestingAndMultipleActions()
    {
        var actions = JsonNode.Parse("""
            [
              { "id": "Explicit.Copy", "name": "Copy now", "command": "copy" },
              { "command": { "action": "sendInput", "input": "abc" } },
              {
                "name": "Group",
                "commands": [
                  { "name": "Child", "command": "paste" }
                ]
              },
              {
                "id": "Sequence",
                "command": {
                  "action": "multipleActions",
                  "actions": ["copy", { "action": "sendInput", "input": "x" }]
                }
              }
            ]
            """)!.AsArray();

        var map = ActionMap.FromJson(actions);

        Assert.Equal("Copy now", map.GetActionByID("Explicit.Copy")?.Name);
        var generated = Assert.Single(map.Commands.Values, command => command.Id.StartsWith("User.sendInput.", StringComparison.Ordinal));
        Assert.Equal(generated.Id, generated.ActionAndArgs!.GenerateId());
        Assert.Equal("Send Input: abc", generated.Name);
        var group = Assert.Single(map.AllCommands, command => command.Name == "Group");
        Assert.Equal("Child", Assert.Single(group.NestedCommands).Name);
        var multiple = Assert.IsType<MultipleActionsArgs>(map.GetActionByID("Sequence")!.ActionAndArgs!.Args);
        Assert.Collection(
            multiple.Actions,
            action => Assert.Equal(ShortcutAction.CopyText, action.Action),
            action => Assert.Equal(ShortcutAction.SendInput, action.Action));
    }

    [Fact]
    public void SupportsModernAndLegacyKeybindingDefinitions()
    {
        var actions = JsonNode.Parse("""
            [
              { "id": "Copy.Id", "command": "copy" },
              { "keys": "ctrl+shift+v", "command": "paste" }
            ]
            """)!.AsArray();
        var keybindings = JsonNode.Parse("""
            [
              { "keys": ["ctrl+insert", "ctrl+c"], "id": "Copy.Id" },
              { "keys": "shift+insert", "command": { "action": "sendInput", "input": "legacy" } }
            ]
            """)!.AsArray();

        var map = ActionMap.FromJson(actions, keybindings);

        Assert.Equal(ShortcutAction.CopyText, map.Resolve("control+insert")?.ActionAndArgs?.Action);
        Assert.Equal(ShortcutAction.CopyText, map.Resolve("ctrl+c")?.ActionAndArgs?.Action);
        Assert.Equal(ShortcutAction.PasteText, map.Resolve("SHIFT+CTRL+v")?.ActionAndArgs?.Action);
        Assert.Equal("legacy", Assert.IsType<SendInputArgs>(map.Resolve("shift+insert")!.ActionAndArgs!.Args).Input);
    }

    [Theory]
    [InlineData("SHIFT+control+escape", "ctrl+shift+esc")]
    [InlineData("alt+pageDown", "alt+pgdn")]
    [InlineData("windows+app", "win+menu")]
    [InlineData("ctrl+numpad_1", "ctrl+numpad1")]
    [InlineData("shift+ctrl+alt+f12", "ctrl+alt+shift+f12")]
    [InlineData("ctrl+vk(0x09)", "ctrl+tab")]
    [InlineData("ctrl+,", "ctrl+comma")]
    [InlineData("ctrl+.", "ctrl+period")]
    public void NormalizesChordAliases(string input, string expected)
    {
        Assert.Equal(expected, KeyChord.Normalize(input));
    }

    [Theory]
    [InlineData(SettingsTarget.SettingsUI, "Open Settings: Settings UI")]
    [InlineData(SettingsTarget.SettingsFile, "Open Settings: Settings file")]
    [InlineData(SettingsTarget.DefaultsFile, "Open Settings: Defaults file")]
    [InlineData(SettingsTarget.Directory, "Open Settings: Settings directory")]
    public void SettingsTargetsHaveDistinctGeneratedNames(SettingsTarget target, string expected)
    {
        var action = new ActionAndArgs(ShortcutAction.OpenSettings, new OpenSettingsArgs(target));

        Assert.Equal(expected, action.GenerateName());
    }

    [Fact]
    public void LastBindingWinsAndUnbindingIsExplicit()
    {
        var map = ActionMap.FromJson(
            JsonNode.Parse("""[{ "id": "Copy", "command": "copy" }, { "id": "Paste", "command": "paste" }]""")!.AsArray(),
            JsonNode.Parse("""[{ "keys": "ctrl+x", "id": "Copy" }, { "keys": "control+x", "id": "Paste" }]""")!.AsArray());
        map.Layer(null, JsonNode.Parse("""[{ "keys": "ctrl+x", "command": "unbound" }]""")!.AsArray());

        Assert.Null(map.Resolve("ctrl+x"));
        Assert.True(map.IsKeyChordExplicitlyUnbound("ctrl+x"));
        Assert.Equal(2, map.Conflicts.Count);
        Assert.Equal("Copy", map.Conflicts[0].PreviousCommandId);
        Assert.Equal("Paste", map.Conflicts[0].CommandId);
        Assert.True(map.Conflicts[1].IsUnbinding);

        map.Layer(null, JsonNode.Parse("""[{ "keys": "ctrl+x", "id": "Copy" }]""")!.AsArray());
        Assert.False(map.IsKeyChordExplicitlyUnbound("ctrl+x"));
        Assert.Equal("Copy", map.Resolve("ctrl+x")?.Id);
    }

    [Fact]
    public void UnknownActionsRemainExplicitAndRoundTrip()
    {
        const string json = """{"action":"futureAction","futureArgument":{"enabled":true}}""";
        var parsed = ActionJson.Parse(json);

        Assert.True(parsed.IsUnknown);
        var args = Assert.IsType<UnknownActionArgs>(parsed.Args);
        Assert.Equal("futureAction", args.ActionName);
        Assert.Equal(json, ActionJson.Serialize(parsed));

        var map = ActionMap.FromJson(
            JsonNode.Parse("""[{ "id": "Future", "command": {"action":"futureAction","value":42} }]""")!.AsArray());
        Assert.True(map.GetActionByID("Future")?.ActionAndArgs?.IsUnknown);
    }

    [Fact]
    public void SettingsLoaderKeepsUnknownActionJsonWhileExposingTypedMap()
    {
        const string defaults = """
            {
              "profiles": { "defaults": {}, "list": [{ "name": "Shell" }] },
              "schemes": [{ "name": "Campbell" }],
              "actions": [{ "id": "Known", "command": "copy" }]
            }
            """;
        const string user = """
            {
              "actions": [{ "id": "Future", "command": {"action":"futureAction","value":42} }]
            }
            """;

        var settings = SettingsLoader.Load(defaults, user);
        Assert.True(settings.ActionMap.GetAction("Future")?.IsUnknown);

        var serialized = SettingsLoader.SerializeUserDocument(settings);
        Assert.Contains("\"futureAction\"", serialized, StringComparison.Ordinal);
        Assert.Contains("\"value\": 42", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void SourceGeneratedActionJsonPathHandlesPolymorphism()
    {
        var parsed = JsonSerializer.Deserialize(
            """{"action":"sendInput","input":"hello"}""",
            ActionJsonContext.Default.ActionAndArgs);
        var args = Assert.IsType<SendInputArgs>(parsed!.Args);
        Assert.Equal("hello", args.Input);

        var serialized = JsonSerializer.Serialize(parsed, ActionJsonContext.Default.ActionAndArgs);
        Assert.Equal("""{"action":"sendInput","input":"hello"}""", serialized);
    }
}
