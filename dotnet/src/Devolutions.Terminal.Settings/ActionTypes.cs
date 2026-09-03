using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace Devolutions.Terminal.Settings;

public enum ShortcutAction
{
    Invalid,
    CopyText,
    PasteText,
    OpenNewTabDropdown,
    DuplicateTab,
    NewTab,
    CloseWindow,
    CloseTab,
    ClosePane,
    NextTab,
    PrevTab,
    SendInput,
    SplitPane,
    ToggleSplitOrientation,
    TogglePaneZoom,
    SwitchToTab,
    AdjustFontSize,
    ResetFontSize,
    ScrollUp,
    ScrollDown,
    ScrollUpPage,
    ScrollDownPage,
    ScrollToTop,
    ScrollToBottom,
    ScrollToMark,
    AddMark,
    ClearMark,
    ClearAllMarks,
    ResizePane,
    MoveFocus,
    MovePane,
    SwapPane,
    Find,
    ToggleShaderEffects,
    ToggleFocusMode,
    ToggleFullscreen,
    ToggleAlwaysOnTop,
    OpenSettings,
    SetFocusMode,
    SetFullScreen,
    SetMaximized,
    SetColorScheme,
    SetTabColor,
    OpenTabColorPicker,
    RenameTab,
    OpenTabRenamer,
    ExecuteCommandline,
    ToggleCommandPalette,
    CloseOtherTabs,
    CloseTabsAfter,
    TabSearch,
    MoveTab,
    BreakIntoDebugger,
    TogglePaneReadOnly,
    EnablePaneReadOnly,
    DisablePaneReadOnly,
    FindMatch,
    NewWindow,
    IdentifyWindow,
    IdentifyWindows,
    RenameWindow,
    OpenWindowRenamer,
    DisplayWorkingDirectory,
    SearchForText,
    GlobalSummon,
    QuakeMode,
    FocusPane,
    OpenSystemMenu,
    ExportBuffer,
    ClearBuffer,
    MultipleActions,
    Quit,
    AdjustOpacity,
    RestoreLastClosed,
    SelectAll,
    SelectCommand,
    SelectOutput,
    MarkMode,
    ToggleBlockSelection,
    SwitchSelectionEndpoint,
    Suggestions,
    ColorSelection,
    ShowContextMenu,
    ExpandSelectionToWord,
    CloseOtherPanes,
    RestartConnection,
    ToggleBroadcastInput,
    OpenScratchpad,
    OpenAbout,
    QuickFix,
    OpenCWD,
    OpenWorkspace,
    Workspaces,
}

public sealed record ActionDefinition(ShortcutAction Action, string JsonName, bool HasArguments);

public static class ActionCatalog
{
    private static readonly ActionDefinition[] Definitions =
    [
        new(ShortcutAction.CopyText, "copy", true),
        new(ShortcutAction.PasteText, "paste", false),
        new(ShortcutAction.OpenNewTabDropdown, "openNewTabDropdown", false),
        new(ShortcutAction.DuplicateTab, "duplicateTab", false),
        new(ShortcutAction.NewTab, "newTab", true),
        new(ShortcutAction.CloseWindow, "closeWindow", false),
        new(ShortcutAction.CloseTab, "closeTab", true),
        new(ShortcutAction.ClosePane, "closePane", false),
        new(ShortcutAction.NextTab, "nextTab", true),
        new(ShortcutAction.PrevTab, "prevTab", true),
        new(ShortcutAction.SendInput, "sendInput", true),
        new(ShortcutAction.SplitPane, "splitPane", true),
        new(ShortcutAction.ToggleSplitOrientation, "toggleSplitOrientation", false),
        new(ShortcutAction.TogglePaneZoom, "togglePaneZoom", false),
        new(ShortcutAction.SwitchToTab, "switchToTab", true),
        new(ShortcutAction.AdjustFontSize, "adjustFontSize", true),
        new(ShortcutAction.ResetFontSize, "resetFontSize", false),
        new(ShortcutAction.ScrollUp, "scrollUp", true),
        new(ShortcutAction.ScrollDown, "scrollDown", true),
        new(ShortcutAction.ScrollUpPage, "scrollUpPage", false),
        new(ShortcutAction.ScrollDownPage, "scrollDownPage", false),
        new(ShortcutAction.ScrollToTop, "scrollToTop", false),
        new(ShortcutAction.ScrollToBottom, "scrollToBottom", false),
        new(ShortcutAction.ScrollToMark, "scrollToMark", true),
        new(ShortcutAction.AddMark, "addMark", true),
        new(ShortcutAction.ClearMark, "clearMark", false),
        new(ShortcutAction.ClearAllMarks, "clearAllMarks", false),
        new(ShortcutAction.ResizePane, "resizePane", true),
        new(ShortcutAction.MoveFocus, "moveFocus", true),
        new(ShortcutAction.MovePane, "movePane", true),
        new(ShortcutAction.SwapPane, "swapPane", true),
        new(ShortcutAction.Find, "find", false),
        new(ShortcutAction.ToggleShaderEffects, "toggleShaderEffects", false),
        new(ShortcutAction.ToggleFocusMode, "toggleFocusMode", false),
        new(ShortcutAction.ToggleFullscreen, "toggleFullscreen", false),
        new(ShortcutAction.ToggleAlwaysOnTop, "toggleAlwaysOnTop", false),
        new(ShortcutAction.OpenSettings, "openSettings", true),
        new(ShortcutAction.SetFocusMode, "setFocusMode", true),
        new(ShortcutAction.SetFullScreen, "setFullScreen", true),
        new(ShortcutAction.SetMaximized, "setMaximized", true),
        new(ShortcutAction.SetColorScheme, "setColorScheme", true),
        new(ShortcutAction.SetTabColor, "setTabColor", true),
        new(ShortcutAction.OpenTabColorPicker, "openTabColorPicker", false),
        new(ShortcutAction.RenameTab, "renameTab", true),
        new(ShortcutAction.OpenTabRenamer, "openTabRenamer", false),
        new(ShortcutAction.ExecuteCommandline, "wt", true),
        new(ShortcutAction.ToggleCommandPalette, "commandPalette", true),
        new(ShortcutAction.CloseOtherTabs, "closeOtherTabs", true),
        new(ShortcutAction.CloseTabsAfter, "closeTabsAfter", true),
        new(ShortcutAction.TabSearch, "tabSearch", false),
        new(ShortcutAction.MoveTab, "moveTab", true),
        new(ShortcutAction.BreakIntoDebugger, "breakIntoDebugger", false),
        new(ShortcutAction.TogglePaneReadOnly, "toggleReadOnlyMode", false),
        new(ShortcutAction.EnablePaneReadOnly, "enableReadOnlyMode", false),
        new(ShortcutAction.DisablePaneReadOnly, "disableReadOnlyMode", false),
        new(ShortcutAction.FindMatch, "findMatch", true),
        new(ShortcutAction.NewWindow, "newWindow", true),
        new(ShortcutAction.IdentifyWindow, "identifyWindow", false),
        new(ShortcutAction.IdentifyWindows, "identifyWindows", false),
        new(ShortcutAction.RenameWindow, "renameWindow", true),
        new(ShortcutAction.OpenWindowRenamer, "openWindowRenamer", false),
        new(ShortcutAction.DisplayWorkingDirectory, "debugTerminalCwd", false),
        new(ShortcutAction.SearchForText, "searchWeb", true),
        new(ShortcutAction.GlobalSummon, "globalSummon", true),
        new(ShortcutAction.QuakeMode, "quakeMode", false),
        new(ShortcutAction.FocusPane, "focusPane", true),
        new(ShortcutAction.OpenSystemMenu, "openSystemMenu", false),
        new(ShortcutAction.ExportBuffer, "exportBuffer", true),
        new(ShortcutAction.ClearBuffer, "clearBuffer", true),
        new(ShortcutAction.MultipleActions, "multipleActions", true),
        new(ShortcutAction.Quit, "quit", false),
        new(ShortcutAction.AdjustOpacity, "adjustOpacity", true),
        new(ShortcutAction.RestoreLastClosed, "restoreLastClosed", false),
        new(ShortcutAction.SelectAll, "selectAll", false),
        new(ShortcutAction.SelectCommand, "selectCommand", true),
        new(ShortcutAction.SelectOutput, "selectOutput", true),
        new(ShortcutAction.MarkMode, "markMode", false),
        new(ShortcutAction.ToggleBlockSelection, "toggleBlockSelection", false),
        new(ShortcutAction.SwitchSelectionEndpoint, "switchSelectionEndpoint", false),
        new(ShortcutAction.Suggestions, "showSuggestions", true),
        new(ShortcutAction.ColorSelection, "experimental.colorSelection", true),
        new(ShortcutAction.ShowContextMenu, "showContextMenu", false),
        new(ShortcutAction.ExpandSelectionToWord, "expandSelectionToWord", false),
        new(ShortcutAction.CloseOtherPanes, "closeOtherPanes", false),
        new(ShortcutAction.RestartConnection, "restartConnection", false),
        new(ShortcutAction.ToggleBroadcastInput, "toggleBroadcastInput", false),
        new(ShortcutAction.OpenScratchpad, "experimental.openScratchpad", false),
        new(ShortcutAction.OpenAbout, "openAbout", false),
        new(ShortcutAction.QuickFix, "quickFix", false),
        new(ShortcutAction.OpenCWD, "openCWD", false),
        new(ShortcutAction.OpenWorkspace, "openWorkspace", true),
        new(ShortcutAction.Workspaces, "workspaces", false),
    ];

    private static readonly IReadOnlyDictionary<string, ActionDefinition> ByJsonNameValue =
        new ReadOnlyDictionary<string, ActionDefinition>(
            Definitions.ToDictionary(definition => definition.JsonName, StringComparer.Ordinal));

    private static readonly IReadOnlyDictionary<ShortcutAction, ActionDefinition> ByActionValue =
        new ReadOnlyDictionary<ShortcutAction, ActionDefinition>(
            Definitions.ToDictionary(definition => definition.Action));

    public static IReadOnlyList<ActionDefinition> All { get; } = Array.AsReadOnly(Definitions);
    public static IReadOnlyDictionary<string, ActionDefinition> ByJsonName => ByJsonNameValue;
    public static IReadOnlyDictionary<ShortcutAction, ActionDefinition> ByAction => ByActionValue;

    public static bool TryGet(string jsonName, out ActionDefinition definition) =>
        ByJsonNameValue.TryGetValue(jsonName, out definition!);

    public static string GetJsonName(ShortcutAction action) =>
        ByActionValue.TryGetValue(action, out var definition) ? definition.JsonName : string.Empty;
}

public interface IActionArgs;

public interface INewContentArgs
{
    string Type { get; }
}

public sealed record BaseContentArgs(string Type) : INewContentArgs;

public sealed record NewTerminalArgs(
    string Commandline = "",
    string StartingDirectory = "",
    string TabTitle = "",
    string? TabColor = null,
    [property: JsonPropertyName("index")] int? ProfileIndex = null,
    string Profile = "",
    Guid SessionId = default,
    bool AppendCommandLine = false,
    bool? SuppressApplicationTitle = null,
    string ColorScheme = "",
    bool? Elevate = null,
    bool? ReloadEnvironmentVariables = null,
    [property: JsonPropertyName("__content")] ulong ContentId = 0) : INewContentArgs
{
    [JsonIgnore]
    public string Type => string.Empty;
}

public enum ResizeDirection { None, Left, Right, Up, Down }
public enum FocusDirection { None, Left, Right, Up, Down, Previous, PreviousInOrder, NextInOrder, First, Parent, Child }
public enum SplitDirection { Automatic, Up, Right, Down, Left }
public enum SplitType { Manual, Duplicate }
public enum SettingsTarget { SettingsFile, DefaultsFile, AllFiles, SettingsUI, Directory }
public enum MoveTabDirection { None, Forward, Backward }
public enum FindMatchDirection { None, Next, Previous }
public enum SelectOutputDirection { Previous, Next }
public enum CommandPaletteLaunchMode { Action, CommandLine }
public enum TabSwitcherMode { MostRecentlyUsed, InOrder, Disabled }
public enum DesktopBehavior { Any, ToCurrent, OnCurrent }
public enum MonitorBehavior { Any, ToCurrent, ToMouse }
public enum ScrollToMarkDirection { Previous, Next, First, Last }
public enum ClearBufferType { Screen, Scrollback, All }
public enum MatchMode { None, All }

[Flags]
public enum SuggestionsSource : uint
{
    None = 0,
    Tasks = 0x1,
    CommandHistory = 0x2,
    DirectoryHistory = 0x4,
    QuickFixes = 0x8,
    All = uint.MaxValue,
}

public sealed record SelectionColor(string Value);

public sealed record AdjustFontSizeArgs(float Delta = 0) : IActionArgs;
public sealed record CloseOtherTabsArgs(uint? Index = null) : IActionArgs;
public sealed record CloseTabsAfterArgs(uint? Index = null) : IActionArgs;
public sealed record CloseTabArgs(uint? Index = null) : IActionArgs;
public sealed record CopyTextArgs(
    bool DismissSelection = true,
    bool SingleLine = false,
    bool WithControlSequences = false,
    CopyFormat? CopyFormatting = null) : IActionArgs;
public sealed record ExecuteCommandlineArgs(string Commandline = "") : IActionArgs;
public sealed record FindMatchArgs(FindMatchDirection Direction = FindMatchDirection.None) : IActionArgs;
public sealed record SearchForTextArgs(string QueryUrl = "") : IActionArgs;
public sealed record GlobalSummonArgs(
    string Name = "",
    DesktopBehavior Desktop = DesktopBehavior.ToCurrent,
    MonitorBehavior Monitor = MonitorBehavior.ToMouse,
    bool ToggleVisibility = true,
    uint DropdownDuration = 0) : IActionArgs;
public sealed record MoveFocusArgs([property: JsonPropertyName("direction")] FocusDirection FocusDirection = FocusDirection.None) : IActionArgs;
public sealed record MovePaneArgs([property: JsonPropertyName("index")] uint TabIndex = 0, string Window = "") : IActionArgs;
public sealed record SwapPaneArgs(FocusDirection Direction = FocusDirection.None) : IActionArgs;
public sealed record MoveTabArgs(string Window = "", MoveTabDirection Direction = MoveTabDirection.None) : IActionArgs;
public sealed record NewTabArgs(INewContentArgs ContentArgs) : IActionArgs
{
    public NewTabArgs() : this(new NewTerminalArgs()) { }
}
public sealed record NewWindowArgs(INewContentArgs ContentArgs) : IActionArgs
{
    public NewWindowArgs() : this(new NewTerminalArgs()) { }
}
public sealed record NextTabArgs(TabSwitcherMode? SwitcherMode = null) : IActionArgs;
public sealed record OpenSettingsArgs(SettingsTarget Target = SettingsTarget.SettingsFile) : IActionArgs;
public sealed record SetFocusModeArgs(bool IsFocusMode = false) : IActionArgs;
public sealed record SetFullScreenArgs(bool IsFullScreen = false) : IActionArgs;
public sealed record SetMaximizedArgs(bool IsMaximized = false) : IActionArgs;
public sealed record PrevTabArgs(TabSwitcherMode? SwitcherMode = null) : IActionArgs;
public sealed record RenameTabArgs(string Title = "") : IActionArgs;
public sealed record RenameWindowArgs(string Name = "") : IActionArgs;
public sealed record ResizePaneArgs([property: JsonPropertyName("direction")] ResizeDirection ResizeDirection = ResizeDirection.None) : IActionArgs;
public sealed record ScrollDownArgs(uint? RowsToScroll = null) : IActionArgs;
public sealed record ScrollUpArgs(uint? RowsToScroll = null) : IActionArgs;
public sealed record ScrollToMarkArgs(ScrollToMarkDirection Direction = ScrollToMarkDirection.Previous) : IActionArgs;
public sealed record AddMarkArgs(string? Color = null) : IActionArgs;
public sealed record SendInputArgs(string Input = "") : IActionArgs;
public sealed record SetColorSchemeArgs([property: JsonPropertyName("colorScheme")] string SchemeName = "") : IActionArgs;
public sealed record SetTabColorArgs([property: JsonPropertyName("color")] string? TabColor = null) : IActionArgs;
public sealed record SplitPaneArgs(
    [property: JsonPropertyName("split")] SplitDirection SplitDirection = SplitDirection.Automatic,
    SplitType SplitMode = SplitType.Manual,
    [property: JsonPropertyName("size")] float SplitSize = 0.5f,
    INewContentArgs? ContentArgs = null) : IActionArgs;
public sealed record SwitchToTabArgs([property: JsonPropertyName("index")] uint TabIndex = 0) : IActionArgs;
public sealed record ToggleCommandPaletteArgs(CommandPaletteLaunchMode LaunchMode = CommandPaletteLaunchMode.Action) : IActionArgs;
public sealed record FocusPaneArgs(uint Id = 0) : IActionArgs;
public sealed record ExportBufferArgs(string Path = "") : IActionArgs;
public sealed record ClearBufferArgs(ClearBufferType Clear = ClearBufferType.All) : IActionArgs;
public sealed record MultipleActionsArgs(IReadOnlyList<ActionAndArgs> Actions) : IActionArgs
{
    public MultipleActionsArgs() : this([]) { }
}
public sealed record AdjustOpacityArgs(int Opacity = 0, bool Relative = true) : IActionArgs;
public sealed record SuggestionsArgs(SuggestionsSource Source = SuggestionsSource.Tasks, bool UseCommandline = false) : IActionArgs;
public sealed record SelectCommandArgs(SelectOutputDirection Direction = SelectOutputDirection.Previous) : IActionArgs;
public sealed record SelectOutputArgs(SelectOutputDirection Direction = SelectOutputDirection.Previous) : IActionArgs;
public sealed record ColorSelectionArgs(
    SelectionColor? Foreground = null,
    SelectionColor? Background = null,
    MatchMode MatchMode = MatchMode.None) : IActionArgs;
public sealed record OpenWorkspaceArgs(string Name = "") : IActionArgs;
public sealed record UnknownActionArgs(string ActionName, System.Text.Json.Nodes.JsonObject Raw) : IActionArgs;
