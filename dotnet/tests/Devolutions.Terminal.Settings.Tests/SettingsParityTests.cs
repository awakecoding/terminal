using System.Text.Json.Nodes;
using Devolutions.Terminal.Settings;
using Xunit;

namespace Devolutions.Terminal.Settings.Tests;

public sealed class SettingsParityTests
{
    private const string Defaults = """
        {
            "theme": "system",
            "profiles": {
                "defaults": {
                    "historySize": 9001,
                    "foreground": "#111111",
                    "icon": "default.ico"
                },
                "list": [
                    {
                        "guid": "{11111111-1111-1111-1111-111111111111}",
                        "name": "Inbox",
                        "commandline": "cmd.exe"
                    }
                ]
            },
            "schemes": [
                {
                    "name": "Campbell",
                    "foreground": "#CCCCCC",
                    "background": "#0C0C0C",
                    "cursorColor": "#FFFFFF",
                    "selectionBackground": "#FFFFFF",
                    "black": "#0C0C0C",
                    "red": "#C50F1F",
                    "green": "#13A10E",
                    "yellow": "#C19C00",
                    "blue": "#0037DA",
                    "purple": "#881798",
                    "cyan": "#3A96DD",
                    "white": "#CCCCCC",
                    "brightBlack": "#767676",
                    "brightRed": "#E74856",
                    "brightGreen": "#16C60C",
                    "brightYellow": "#F9F1A5",
                    "brightBlue": "#3B78FF",
                    "brightPurple": "#B4009E",
                    "brightCyan": "#61D6D6",
                    "brightWhite": "#F2F2F2"
                }
            ],
            "themes": [
                { "name": "system", "window": { "applicationTheme": "system" } },
                { "name": "dark", "window": { "applicationTheme": "dark" } },
                { "name": "light", "window": { "applicationTheme": "light" } }
            ]
        }
        """;

    [Fact]
    public void ProjectsEveryGlobalAndWindowSettingsGroup()
    {
        const string user = """
            {
                "language": "fr-FR",
                "warning.inputService": false,
                "firstWindowPreference": "persistedLayout",
                "debugFeatures": true,
                "windowingBehavior": "useAnyExisting",
                "disabledProfileSources": ["A", "B"],
                "compatibility.allowHeadless": true,
                "experimental.enableColorSelection": true,
                "initialRows": 42,
                "initialCols": 132,
                "initialPosition": "10,20",
                "centerOnLaunch": true,
                "experimental.scrollToZoom": false,
                "experimental.scrollToChangeOpacity": false,
                "rendering.graphicsAPI": "software",
                "rendering.disablePartialInvalidation": true,
                "rendering.software": true,
                "compatibility.textMeasurement": "wcswidth",
                "compatibility.ambiguousWidth": "wide",
                "defaultInputScope": "alphanumericHalfWidth",
                "experimental.useBackgroundImageForWindow": true,
                "experimental.detectURLs": false,
                "newTabPosition": "afterCurrentTab",
                "useAcrylicInTabRow": true,
                "warning.largePaste": false,
                "warning.multiLinePaste": "never",
                "autoHideWindow": true,
                "tabSwitcherMode": "mostRecentlyUsed",
                "safeUriSchemes": ["https", "file"],
                "experimental.enableShellCompletionMenu": true,
                "compatibility.enableUnfocusedAcrylic": false,
                "searchWebDefaultQueryUrl": "https://example.test/?q=%s"
            }
            """;

        var settings = SettingsLoader.Load(Defaults, user);

        Assert.Equal("fr-FR", settings.Language);
        Assert.False(settings.InputServiceWarning);
        Assert.Equal("persistedLayout", settings.FirstWindowPreference);
        Assert.True(settings.DebugFeaturesEnabled);
        Assert.Equal("useAnyExisting", settings.WindowingBehavior);
        Assert.Equal(["A", "B"], settings.DisabledProfileSources);
        Assert.True(settings.AllowHeadless);
        Assert.True(settings.EnableColorSelection);
        Assert.Equal(42, settings.InitialRows);
        Assert.Equal(132, settings.InitialCols);
        Assert.Equal("10,20", settings.InitialPosition);
        Assert.True(settings.CenterOnLaunch);
        Assert.False(settings.ScrollToZoom);
        Assert.False(settings.ScrollToChangeOpacity);
        Assert.Equal("software", settings.GraphicsApi);
        Assert.True(settings.DisablePartialInvalidation);
        Assert.True(settings.SoftwareRendering);
        Assert.Equal("wcswidth", settings.TextMeasurement);
        Assert.Equal("wide", settings.AmbiguousWidth);
        Assert.Equal("alphanumericHalfWidth", settings.DefaultInputScope);
        Assert.True(settings.UseBackgroundImageForWindow);
        Assert.False(settings.DetectUrls);
        Assert.Equal("afterCurrentTab", settings.NewTabPosition);
        Assert.True(settings.UseAcrylicInTabRow);
        Assert.False(settings.WarnAboutLargePaste);
        Assert.Equal("never", settings.WarnAboutMultiLinePaste);
        Assert.True(settings.AutoHideWindow);
        Assert.Equal("mostRecentlyUsed", settings.TabSwitcherMode);
        Assert.Equal(["https", "file"], settings.SafeUriSchemes);
        Assert.True(settings.EnableShellCompletionMenu);
        Assert.False(settings.EnableUnfocusedAcrylic);
        Assert.Equal("https://example.test/?q=%s", settings.SearchWebDefaultQueryUrl);
    }

    [Fact]
    public void ProjectsProfileFontAppearanceAndCompatibilityGroups()
    {
        const string user = """
            {
                "profiles": {
                    "list": [
                        {
                            "guid": "{11111111-1111-1111-1111-111111111111}",
                            "answerbackMessage": "ok",
                            "connectionType": "{22222222-2222-2222-2222-222222222222}",
                            "scrollbarState": "hidden",
                            "antialiasingMode": "cleartype",
                            "font": {
                                "face": "Consolas",
                                "size": 14,
                                "weight": "bold",
                                "axes": { "wght": 650 },
                                "features": { "liga": 0 },
                                "builtinGlyphs": false,
                                "colorGlyphs": false,
                                "cellWidth": "1.2",
                                "cellHeight": "1.1"
                            },
                            "bellStyle": ["window", "taskbar"],
                            "bellSound": ["a.wav", null],
                            "rightClickContextMenu": true,
                            "backgroundImageAlignment": "topLeft",
                            "experimental.retroTerminalEffect": true,
                            "experimental.pixelShaderPath": "shader.hlsl",
                            "experimental.pixelShaderImagePath": "noise.png",
                            "intenseTextStyle": "all",
                            "adjustIndistinguishableColors": "indexed",
                            "unfocusedAppearance": { "opacity": 50 },
                            "experimental.repositionCursorWithMouse": true,
                            "experimental.rainbowSuggestions": true,
                            "compatibility.input.forceVT": true,
                            "compatibility.allowDECRQCRA": true,
                            "compatibility.allowDECNKM": true,
                            "dragDropDelimiter": ":",
                            "pathTranslationStyle": "wsl"
                        }
                    ]
                }
            }
            """;

        var profile = Assert.Single(SettingsLoader.Load(Defaults, user).Profiles);

        Assert.Equal("ok", profile.AnswerbackMessage);
        Assert.Equal("{22222222-2222-2222-2222-222222222222}", profile.ConnectionType);
        Assert.Equal("hidden", profile.ScrollbarState);
        Assert.Equal("cleartype", profile.AntialiasingMode);
        Assert.Equal("Consolas", profile.Font.Face);
        Assert.Equal(14, profile.Font.Size);
        Assert.Equal(700, profile.Font.Weight);
        Assert.Equal(650, profile.Font.Axes["wght"]);
        Assert.Equal(0, profile.Font.Features["liga"]);
        Assert.False(profile.Font.BuiltinGlyphs);
        Assert.False(profile.Font.ColorGlyphs);
        Assert.Equal("1.2", profile.Font.CellWidth);
        Assert.Equal("1.1", profile.Font.CellHeight);
        Assert.Equal(BellStyle.Window | BellStyle.Taskbar, profile.BellStyle);
        Assert.Equal(2, profile.BellSound.Count);
        Assert.True(profile.RightClickContextMenu);
        Assert.Equal("topLeft", profile.BackgroundImageAlignment);
        Assert.True(profile.RetroTerminalEffect);
        Assert.Equal("shader.hlsl", profile.PixelShaderPath?.Path);
        Assert.Equal("noise.png", profile.PixelShaderImagePath?.Path);
        Assert.Equal("all", profile.IntenseTextStyle);
        Assert.Equal("indexed", profile.AdjustIndistinguishableColors);
        Assert.Equal(50, profile.UnfocusedAppearance?.Opacity);
        Assert.True(profile.RepositionCursorWithMouse);
        Assert.True(profile.RainbowSuggestions);
        Assert.True(profile.ForceVtInput);
        Assert.True(profile.AllowVtChecksumReport);
        Assert.True(profile.AllowKeypadMode);
        Assert.Equal(":", profile.DragDropDelimiter);
        Assert.Equal("wsl", profile.PathTranslationStyle);
    }

    [Fact]
    public void NullClearsOrdinaryOverrideAndResumesInheritance()
    {
        const string user = """
            {
                "profiles": {
                    "defaults": { "historySize": 42 },
                    "list": [
                        {
                            "guid": "{11111111-1111-1111-1111-111111111111}",
                            "historySize": null,
                            "icon": null
                        }
                    ]
                }
            }
            """;

        var profile = Assert.Single(SettingsLoader.Load(Defaults, user).Profiles);

        Assert.Equal(42, profile.HistorySize);
        Assert.Equal("default.ico", profile.Icon);
    }

    [Fact]
    public void NullableAppearanceNullIsAnExplicitValue()
    {
        const string user = """
            {
                "profiles": {
                    "list": [
                        {
                            "guid": "{11111111-1111-1111-1111-111111111111}",
                            "foreground": null
                        }
                    ]
                }
            }
            """;

        Assert.Null(Assert.Single(SettingsLoader.Load(Defaults, user).Profiles).Foreground);
    }

    [Fact]
    public void ModernKeysWinOverLegacyAliases()
    {
        const string user = """
            {
                "inputServiceWarning": false,
                "warning.inputService": true,
                "largePasteWarning": false,
                "warning.largePaste": true,
                "useTabSwitcher": false,
                "tabSwitcherMode": "inOrder",
                "confirmCloseAllTabs": false,
                "warning.confirmOnClose": "always"
            }
            """;

        var settings = SettingsLoader.Load(Defaults, user);

        Assert.True(settings.InputServiceWarning);
        Assert.True(settings.WarnAboutLargePaste);
        Assert.Equal("inOrder", settings.TabSwitcherMode);
        Assert.Equal(ConfirmOnClose.Always, settings.ConfirmOnClose);
    }

    [Fact]
    public void MigratesLegacyGlobalAndProfileKeys()
    {
        const string user = """
            {
                "compatibility.reloadEnvironmentVariables": false,
                "experimental.input.forceVT": true,
                "profiles": {
                    "defaults": {
                        "experimental.autoMarkPrompts": false,
                        "experimental.showMarksOnScrollbar": true,
                        "experimental.rightClickContextMenu": true
                    }
                }
            }
            """;

        var settings = SettingsLoader.Load(Defaults, user);
        var output = SettingsLoader.SerializeUserDocument(settings);

        Assert.False(settings.ProfileDefaults.ReloadEnvironmentVariables);
        Assert.True(settings.ProfileDefaults.ForceVtInput);
        Assert.False(settings.ProfileDefaults.AutoMarkPrompts);
        Assert.True(settings.ProfileDefaults.ShowMarksOnScrollbar);
        Assert.True(settings.ProfileDefaults.RightClickContextMenu);
        Assert.Contains("\"compatibility.input.forceVT\": true", output, StringComparison.Ordinal);
        Assert.Contains("\"autoMarkPrompts\": false", output, StringComparison.Ordinal);
    }

    [Fact]
    public void FontObjectSuppressesLegacyFontKeys()
    {
        const string user = """
            {
                "profiles": {
                    "list": [
                        {
                            "guid": "{11111111-1111-1111-1111-111111111111}",
                            "fontFace": "Legacy",
                            "fontSize": 99,
                            "font": { "weight": "bold" }
                        }
                    ]
                }
            }
            """;

        var font = Assert.Single(SettingsLoader.Load(Defaults, user).Profiles).Font;

        Assert.Equal("Cascadia Mono", font.Face);
        Assert.Equal(12, font.Size);
        Assert.Equal(700, font.Weight);
    }

    [Fact]
    public void PartialFontObjectInheritsLegacyValuesFromLowerLayer()
    {
        const string defaults = """
            {
                "profiles": {
                    "defaults": {},
                    "list": [
                        {
                            "guid": "{11111111-1111-1111-1111-111111111111}",
                            "name": "Inbox",
                            "fontFace": "Consolas",
                            "fontSize": 14
                        }
                    ]
                }
            }
            """;
        const string user = """
            {
                "profiles": {
                    "list": [
                        {
                            "guid": "{11111111-1111-1111-1111-111111111111}",
                            "font": { "weight": "bold" }
                        }
                    ]
                }
            }
            """;

        var font = Assert.Single(SettingsLoader.Load(defaults, user).Profiles).Font;

        Assert.Equal("Consolas", font.Face);
        Assert.Equal(14, font.Size);
        Assert.Equal(700, font.Weight);
    }

    [Fact]
    public void ThemeAndColorSchemePairsRoundTripSpecialForms()
    {
        const string user = """
            {
                "theme": { "dark": "dark", "light": "light" },
                "profiles": {
                    "list": [
                        {
                            "guid": "{11111111-1111-1111-1111-111111111111}",
                            "colorScheme": { "dark": "Campbell", "light": "Campbell" }
                        }
                    ]
                }
            }
            """;

        var settings = SettingsLoader.Load(Defaults, user);
        var output = SettingsLoader.SerializeUserDocument(settings);

        Assert.Equal("dark", settings.Theme.DarkName);
        Assert.Equal("light", settings.Theme.LightName);
        Assert.Contains("\"dark\": \"dark\"", output, StringComparison.Ordinal);
        Assert.Contains("\"light\": \"light\"", output, StringComparison.Ordinal);
        Assert.Contains("\"colorScheme\"", output, StringComparison.Ordinal);
    }

    [Fact]
    public void UnfocusedAppearancePreservesExplicitNullAndSchemePairChannels()
    {
        const string user = """
            {
                "schemes": [{ "name": "D" }, { "name": "L" }],
                "profiles": {
                    "list": [
                        {
                            "guid": "{11111111-1111-1111-1111-111111111111}",
                            "colorScheme": { "dark": "D", "light": "L" },
                            "unfocusedAppearance": { "foreground": null }
                        }
                    ]
                }
            }
            """;

        var appearance = Assert.Single(
            SettingsLoader.Load(Defaults, user).Profiles).UnfocusedAppearance;

        Assert.NotNull(appearance);
        Assert.Null(appearance.Foreground);
        Assert.Equal("D", appearance.DarkColorScheme);
        Assert.Equal("L", appearance.LightColorScheme);
    }

    [Fact]
    public void InvalidThemePairHalfDoesNotDiscardValidHalf()
    {
        const string user = """{ "theme": { "dark": "dark", "light": "missing" } }""";

        var settings = SettingsLoader.Load(Defaults, user);

        Assert.Equal("dark", settings.Theme.DarkName);
        Assert.Equal("system", settings.Theme.LightName);
        Assert.Contains(settings.Diagnostics, diagnostic => diagnostic.Code == "UnknownTheme");
    }

    [Fact]
    public void ThemeNestedObjectsPreserveMissingVersusNull()
    {
        const string user = """
            {
                "themes": [
                    { "name": "empty" },
                    { "name": "nullColor", "tabRow": { "background": null } }
                ]
            }
            """;

        var settings = SettingsLoader.Load(Defaults, user);
        var empty = Assert.Single(settings.Themes, theme => theme.Name == "empty");
        var nullColor = Assert.Single(settings.Themes, theme => theme.Name == "nullColor");

        Assert.Null(empty.Window);
        Assert.Null(empty.TabRow);
        Assert.NotNull(nullColor.TabRow);
        Assert.Null(nullColor.TabRow.Background);
    }

    [Theory]
    [InlineData("false", CopyFormat.None)]
    [InlineData("true", CopyFormat.All)]
    [InlineData("\"html\"", CopyFormat.Html)]
    [InlineData("\"rtf\"", CopyFormat.Rtf)]
    [InlineData("[\"html\", \"rtf\"]", CopyFormat.All)]
    public void ParsesCopyFormattingCompatibilityForms(string json, CopyFormat expected)
    {
        var settings = SettingsLoader.Load(Defaults, $$"""{ "copyFormatting": {{json}} }""");

        Assert.Equal(expected, settings.CopyFormatFormats);
    }

    [Fact]
    public void TerminalEngineCanBeSelectedGloballyAndPerProfile()
    {
        const string user = """
            {
                "experimental.terminalEngine": "ghostty",
                "profiles": {
                    "list": [
                        {
                            "guid": "{11111111-1111-1111-1111-111111111111}",
                            "name": "Inbox",
                            "experimental.terminalEngine": "builtin"
                        }
                    ]
                }
            }
            """;

        var settings = SettingsLoader.Load(Defaults, user);

        Assert.Equal(TerminalEngineKind.Ghostty, settings.TerminalEngine);
        Assert.Equal(TerminalEngineKind.BuiltIn, Assert.Single(settings.Profiles).TerminalEngine);

        var serialized = SettingsLoader.SerializeUserDocument(settings);
        Assert.Contains("\"experimental.terminalEngine\": \"ghostty\"", serialized);
        Assert.Contains("\"experimental.terminalEngine\": \"builtin\"", serialized);
    }

    [Fact]
    public void InheritedTerminalEngineRemovesProfileOverride()
    {
        const string user = """
            {
                "profiles": {
                    "list": [
                        {
                            "guid": "{11111111-1111-1111-1111-111111111111}",
                            "name": "Inbox",
                            "experimental.terminalEngine": "ghostty"
                        }
                    ]
                }
            }
            """;
        var settings = SettingsLoader.Load(Defaults, user);
        Assert.Single(settings.Profiles).TerminalEngine = null;

        var serialized = SettingsLoader.SerializeUserDocument(settings);

        Assert.DoesNotContain("\"experimental.terminalEngine\": \"ghostty\"", serialized);
    }

    [Fact]
    public void ParsesNewTabMenuAndFiltersUnknownEntries()
    {
        const string user = """
            {
                "newTabMenu": [
                    { "type": "separator" },
                    {
                        "type": "folder",
                        "name": "Tools",
                        "allowEmpty": true,
                        "entries": [
                            { "type": "profile", "profile": "Inbox" },
                            { "type": "action", "action": "Terminal.Copy" }
                        ]
                    },
                    { "type": "futureType" }
                ]
            }
            """;

        var menu = SettingsLoader.Load(Defaults, user).NewTabMenu;

        Assert.Equal(2, menu.Count);
        Assert.Equal(NewTabMenuEntryType.Separator, menu[0].Type);
        Assert.Equal("Tools", menu[1].Name);
        Assert.Equal(2, menu[1].Entries.Count);
        Assert.Equal("Terminal.Copy", menu[1].Entries[1].ActionId);
    }

    [Fact]
    public void FolderWithoutEntriesIsEmpty()
    {
        const string user = """
            {
                "newTabMenu": [
                    { "type": "folder", "name": "Empty", "allowEmpty": true }
                ]
            }
            """;

        var folder = Assert.Single(SettingsLoader.Load(Defaults, user).NewTabMenu);

        Assert.Equal(NewTabMenuEntryType.Folder, folder.Type);
        Assert.Empty(folder.Entries);
    }

    [Fact]
    public void SchemeCollisionRemapsBothPairChannels()
    {
        const string user = """
            {
                "schemes": [{ "name": "Campbell", "background": "#010203" }],
                "profiles": {
                    "list": [
                        {
                            "guid": "{11111111-1111-1111-1111-111111111111}",
                            "colorScheme": { "dark": "Campbell", "light": "Campbell" }
                        }
                    ]
                }
            }
            """;

        var defaults = Defaults.Replace(
            "\"foreground\": \"#CCCCCC\"",
            "\"foreground\": \"#123456\"",
            StringComparison.Ordinal);
        var settings = SettingsLoader.Load(defaults, user);
        var profile = Assert.Single(settings.Profiles);

        Assert.Equal("Campbell (modified)", profile.DarkColorScheme);
        Assert.Equal("Campbell (modified)", profile.LightColorScheme);
        Assert.Equal(
            "#123456",
            settings.Schemes.Single(scheme => scheme.Name == "Campbell (modified)").Foreground);
    }

    [Fact]
    public void ActionsAndKeybindingsRemainLosslessRawJson()
    {
        const string user = """
            {
                "actions": [
                    {
                        "command": { "action": "future.action", "future": { "x": 1 } },
                        "id": "Example.Future"
                    }
                ],
                "keybindings": [
                    { "keys": "ctrl+x", "id": "Example.Future", "future": true }
                ]
            }
            """;

        var settings = SettingsLoader.Load(Defaults, user);
        var output = SettingsLoader.SerializeUserDocument(settings);

        Assert.Single(settings.Actions);
        Assert.Single(settings.Keybindings);
        Assert.Contains("\"future\"", output, StringComparison.Ordinal);
        Assert.Contains("\"ctrl+x\"", output, StringComparison.Ordinal);
    }

    [Fact]
    public void UserProfileOrderPrecedesInheritedProfiles()
    {
        const string defaults = """
            {
                "profiles": [
                    { "guid": "{11111111-1111-1111-1111-111111111111}", "name": "One" },
                    { "guid": "{22222222-2222-2222-2222-222222222222}", "name": "Two" }
                ],
                "schemes": [{ "name": "Campbell" }]
            }
            """;
        const string user = """
            {
                "profiles": {
                    "list": [
                        { "guid": "{22222222-2222-2222-2222-222222222222}" }
                    ]
                }
            }
            """;

        var profiles = SettingsLoader.Load(defaults, user).Profiles;

        Assert.Equal("Two", profiles[0].Name);
        Assert.Equal("One", profiles[1].Name);
    }

    [Fact]
    public void AssignsInboxFragmentAndUserOrigins()
    {
        const string user = """
            {
                "profiles": {
                    "list": [{ "name": "User", "commandline": "user.exe" }]
                }
            }
            """;
        const string fragment = """
            {
                "profiles": [{ "name": "Fragment", "commandline": "fragment.exe" }],
                "schemes": [{ "name": "FragmentScheme" }]
            }
            """;

        var settings = SettingsLoader.Load(
            Defaults,
            user,
            [new SettingsLayer(@"C:\Fragments\Provider\fragment.json", fragment, SettingsLayerKind.Fragment)]);

        Assert.Equal(SettingsOrigin.Inbox, settings.Profiles.Single(profile => profile.Name == "Inbox").Origin);
        Assert.Equal(SettingsOrigin.Fragment, settings.Profiles.Single(profile => profile.Name == "Fragment").Origin);
        Assert.Equal(SettingsOrigin.User, settings.Profiles.Single(profile => profile.Name == "User").Origin);
        Assert.Equal(
            SettingsOrigin.Fragment,
            settings.Schemes.Single(scheme => scheme.Name == "FragmentScheme").Origin);
    }

    [Fact]
    public void EmitsThemeEnvironmentAndMenuWarnings()
    {
        const string user = """
            {
                "theme": "missing",
                "newTabMenu": [
                    { "type": "remainingProfiles" },
                    { "type": "folder", "entries": [{ "type": "remainingProfiles" }] }
                ],
                "profiles": {
                    "list": [
                        {
                            "guid": "{11111111-1111-1111-1111-111111111111}",
                            "environment": { "BAD=NAME": "x" }
                        }
                    ]
                }
            }
            """;

        var diagnostics = SettingsLoader.Load(Defaults, user).Diagnostics;

        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "UnknownTheme");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "InvalidEnvironmentVariable");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "DuplicateRemainingProfilesEntry");
    }

    [Fact]
    public void NullEnvironmentValueOverridesInheritedValue()
    {
        const string defaults = """
            {
                "profiles": {
                    "defaults": {},
                    "list": [
                        {
                            "guid": "{11111111-1111-1111-1111-111111111111}",
                            "name": "Inbox",
                            "environment": { "REMOVE_ME": "inherited" }
                        }
                    ]
                }
            }
            """;
        const string user = """
            {
                "profiles": {
                    "list": [
                        {
                            "guid": "{11111111-1111-1111-1111-111111111111}",
                            "environment": { "REMOVE_ME": null }
                        }
                    ]
                }
            }
            """;

        var environment = Assert.Single(SettingsLoader.Load(defaults, user).Profiles).Environment;

        Assert.True(environment.ContainsKey("REMOVE_ME"));
        Assert.Null(environment["REMOVE_ME"]);
    }
}
