namespace Devolutions.Terminal.App.Actions;

public static class ActionScopeCatalog
{
    public static ActionScope GetScope(string action) => action switch
    {
        "globalSummon" or
        "identifyWindows" or
        "quakeMode" or
        "quit" => ActionScope.Application,

        "adjustOpacity" or
        "breakIntoDebugger" or
        "closeWindow" or
        "commandPalette" or
        "executeCommandline" or
        "identifyWindow" or
        "newWindow" or
        "openAbout" or
        "openNewTabDropdown" or
        "openSettings" or
        "openSystemMenu" or
        "openWindowRenamer" or
        "openWorkspace" or
        "renameWindow" or
        "setFocusMode" or
        "setFullScreen" or
        "setMaximized" or
        "tabSearch" or
        "toggleAlwaysOnTop" or
        "toggleFocusMode" or
        "toggleFullscreen" or
        "workspaces" => ActionScope.Window,

        "closeOtherTabs" or
        "closeTab" or
        "closeTabsAfter" or
        "duplicateTab" or
        "moveTab" or
        "newTab" or
        "nextTab" or
        "openTabColorPicker" or
        "openTabRenamer" or
        "prevTab" or
        "renameTab" or
        "restoreLastClosed" or
        "setTabColor" or
        "switchToTab" => ActionScope.Tab,

        "closeOtherPanes" or
        "closePane" or
        "disableReadOnlyMode" or
        "enableReadOnlyMode" or
        "focusPane" or
        "moveFocus" or
        "movePane" or
        "resizePane" or
        "restartConnection" or
        "splitPane" or
        "swapPane" or
        "toggleBroadcastInput" or
        "togglePaneZoom" or
        "toggleReadOnlyMode" or
        "toggleSplitOrientation" => ActionScope.Pane,

        _ => ActionScope.Control,
    };
}
