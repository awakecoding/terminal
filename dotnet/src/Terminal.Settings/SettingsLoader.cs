using System.Diagnostics.CodeAnalysis;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Microsoft.Terminal.Settings;

public enum SettingsLayerKind
{
    Defaults,
    Generated,
    Fragment,
    User,
}

public sealed record SettingsLayer(string Source, string Json, SettingsLayerKind Kind);

public static class SettingsLoader
{
    private const string OriginKey = "$terminalOrigin";
    private const string SourceKey = "$terminalSource";

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = 128,
    };

    public static string ReadEmbeddedDefaults()
        => ReadEmbeddedResource("Microsoft.Terminal.Settings.defaults.json");

    public static string ReadEmbeddedUserDefaults()
        => ReadEmbeddedResource("Microsoft.Terminal.Settings.userDefaults.json");

    private static string ReadEmbeddedResource(string name)
    {
        using var stream = typeof(SettingsLoader).Assembly.GetManifestResourceStream(name);
        if (stream is null)
        {
            throw new InvalidOperationException($"Embedded settings resource '{name}' was not found.");
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static AppSettings Load(
        string defaultsJson,
        string? userJson = null,
        IEnumerable<SettingsLayer>? fragments = null,
        string userSource = "settings.json")
    {
        var diagnostics = new List<SettingsDiagnostic>();
        var pendingFragmentUpdates = new List<JsonObject>();
        var defaults = ParseObject(defaultsJson, "defaults.json", required: true, diagnostics)!;
        MigrateLegacyAliases(defaults);
        var merged = (JsonObject)defaults.DeepClone();
        var actionMap = new ActionMap();
        actionMap.Layer(
            defaults["actions"] as JsonArray,
            defaults["keybindings"] as JsonArray,
            SettingsOrigin.Inbox);

        // These bindings are part of the product defaults, but intentionally live
        // in userDefaults.json in the native settings model.
        var userDefaults = ParseObject(
            ReadEmbeddedUserDefaults(),
            "userDefaults.json",
            required: true,
            diagnostics)!;
        actionMap.Layer(
            userDefaults["actions"] as JsonArray,
            userDefaults["keybindings"] as JsonArray,
            SettingsOrigin.Generated);

        if (fragments is not null)
        {
            foreach (var fragment in fragments)
            {
                var fragmentObject = ParseObject(fragment.Json, fragment.Source, required: false, diagnostics);
                if (fragmentObject is not null)
                {
                    MigrateLegacyAliases(fragmentObject);
                    if (fragment.Kind == SettingsLayerKind.Generated)
                    {
                        PrepareGeneratedProfiles(fragmentObject, fragment.Source);
                    }
                    else if (fragment.Kind == SettingsLayerKind.Fragment)
                    {
                        PrepareFragment(fragmentObject, FragmentProvider(fragment.Source));
                        pendingFragmentUpdates.AddRange(ExtractFragmentUpdates(fragmentObject));
                    }

                    actionMap.Layer(
                        fragmentObject["actions"] as JsonArray,
                        fragmentObject["keybindings"] as JsonArray,
                        SettingsOrigin.Fragment);
                    MergeRoot(merged, fragmentObject);
                }
            }
        }

        var inheritedProfileIds = ProfileIds(merged);
        JsonObject? userDocument = null;
        if (!string.IsNullOrWhiteSpace(userJson))
        {
            userDocument = ParseObject(userJson, userSource, required: false, diagnostics);
            if (userDocument is not null)
            {
                MigrateLegacyAliases(userDocument);
                TagNamedEntries(userDocument["schemes"] as JsonArray, SettingsOrigin.User, userSource);
                TagNamedEntries(userDocument["themes"] as JsonArray, SettingsOrigin.User, userSource);
                HandleUserSchemeCollisions(merged, userDocument, diagnostics);
                actionMap.Layer(
                    userDocument["actions"] as JsonArray,
                    userDocument["keybindings"] as JsonArray,
                    SettingsOrigin.User);
                MergeRoot(merged, userDocument);
            }
        }

        ApplyFragmentUpdates(merged, pendingFragmentUpdates);
        var settings = Resolve(merged, userDocument, inheritedProfileIds, actionMap);
        settings.InheritedProfileIds = inheritedProfileIds;
        settings.UserDocument = userDocument is null ? null : (JsonObject)userDocument.DeepClone();
        settings.Diagnostics.AddRange(diagnostics);
        Validate(settings);
        settings.ResolvedSnapshot = SerializeResolvedSettings(settings);
        return settings;
    }

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "The diff operates only on JsonNode instances and does not serialize runtime types.")]
    [UnconditionalSuppressMessage(
        "AOT",
        "IL3050",
        Justification = "The diff operates only on JsonNode instances and does not generate runtime code.")]
    public static string SerializeUserDocument(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var document = settings.UserDocument is null
            ? ParseObject(
                ReadEmbeddedUserDefaults(),
                "userDefaults.json",
                required: true,
                diagnostics: [])!
            : (JsonObject)settings.UserDocument.DeepClone();
        document["$help"] = "https://aka.ms/terminal-documentation";
        document["$schema"] = "https://aka.ms/terminal-profiles-schema";

        var current = SerializeResolvedSettings(settings);
        var baseline = settings.ResolvedSnapshot ?? new JsonObject();
        ApplyResolvedChanges(document, baseline, current, settings.InheritedProfileIds);

        return document.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }) + Environment.NewLine;
    }

    private static AppSettings Resolve(
        JsonObject root,
        JsonObject? userDocument,
        IReadOnlySet<string> inheritedProfileIds,
        ActionMap actionMap)
    {
        var copyFormats = CopyFormats(root);
        var settings = new AppSettings
        {
            Language = String(root, "language"),
            InputServiceWarning = Bool(root, "warning.inputService", true),
            FirstWindowPreference = String(root, "firstWindowPreference") ?? "defaultProfile",
            DebugFeaturesEnabled = Bool(root, "debugFeatures"),
            WindowingBehavior = String(root, "windowingBehavior") ?? "useNew",
            AlwaysShowNotificationIcon = Bool(root, "alwaysShowNotificationIcon"),
            DisabledProfileSources = StringList(root["disabledProfileSources"]),
            AllowHeadless = Bool(root, "compatibility.allowHeadless"),
            EnableColorSelection = Bool(root, "experimental.enableColorSelection"),
            DefaultProfile = String(root, "defaultProfile"),
            InitialCols = Int(root, "initialCols", 80, minimum: 1),
            InitialRows = Int(root, "initialRows", 30, minimum: 1),
            InitialPosition = String(root, "initialPosition"),
            CenterOnLaunch = Bool(root, "centerOnLaunch"),
            LaunchMode = LaunchModeValue(root, "launchMode"),
            AlwaysOnTop = Bool(root, "alwaysOnTop"),
            AutoHideWindow = Bool(root, "autoHideWindow"),
            AlwaysShowTabs = Bool(root, "alwaysShowTabs", true),
            ShowTabsInTitlebar = Bool(root, "showTabsInTitlebar", true),
            ShowTerminalTitleInTitlebar = Bool(root, "showTerminalTitleInTitlebar", true),
            ShowTabsFullscreen = Bool(root, "showTabsFullscreen"),
            CopyOnSelect = Bool(root, "copyOnSelect"),
            CopyFormatting = copyFormats != CopyFormat.None,
            CopyFormatFormats = copyFormats,
            TrimBlockSelection = Bool(root, "trimBlockSelection", true),
            TrimPaste = Bool(root, "trimPaste", true),
            FocusFollowMouse = Bool(root, "focusFollowMouse"),
            ScrollToZoom = Bool(root, "experimental.scrollToZoom", true),
            ScrollToChangeOpacity = Bool(root, "experimental.scrollToChangeOpacity", true),
            GraphicsApi = String(root, "rendering.graphicsAPI") ?? "automatic",
            DisablePartialInvalidation = Bool(root, "rendering.disablePartialInvalidation"),
            SoftwareRendering = Bool(root, "rendering.software"),
            TextMeasurement = String(root, "compatibility.textMeasurement") ?? "graphemes",
            AmbiguousWidth = String(root, "compatibility.ambiguousWidth") ?? "narrow",
            DefaultInputScope = String(root, "defaultInputScope") ?? "default",
            UseBackgroundImageForWindow = Bool(root, "experimental.useBackgroundImageForWindow"),
            DetectUrls = Bool(root, "experimental.detectURLs", true),
            NewTabPosition = String(root, "newTabPosition") ?? "afterLastTab",
            SnapToGridOnResize = Bool(root, "snapToGridOnResize", true),
            DisableAnimations = Bool(root, "disableAnimations"),
            MinimizeToNotificationArea = Bool(root, "minimizeToNotificationArea"),
            ShowAdminShield = Bool(root, "showAdminShield", true),
            Theme = ThemePairValue(root["theme"]),
            StartupActions = String(root, "startupActions") ?? string.Empty,
            WordDelimiters = String(root, "wordDelimiters") ?? " /\\()\"'-.,:;<>~!@#$%^&*|+=[]{}~?\u2502",
            TabWidthMode = TabWidthModeValue(root, "tabWidthMode"),
            ConfirmOnClose = ConfirmOnCloseValue(root, "warning.confirmOnClose"),
            UseAcrylicInTabRow = Bool(root, "useAcrylicInTabRow"),
            WarnAboutLargePaste = Bool(root, "warning.largePaste", true),
            WarnAboutMultiLinePaste = String(root, "warning.multiLinePaste") ?? "automatic",
            TabSwitcherMode = String(root, "tabSwitcherMode") ?? "inOrder",
            SafeUriSchemes = StringList(root["safeUriSchemes"]),
            EnableShellCompletionMenu = Bool(root, "experimental.enableShellCompletionMenu"),
            EnableUnfocusedAcrylic = Bool(root, "compatibility.enableUnfocusedAcrylic", true),
            NewTabMenu = ResolveNewTabMenu(root["newTabMenu"]),
            SearchWebDefaultQueryUrl = String(root, "searchWebDefaultQueryUrl")
                ?? "https://www.bing.com/search?q=%22%s%22",
            Actions = CloneArray(root["actions"]),
            Keybindings = CloneArray(root["keybindings"]),
            ActionMap = actionMap,
        };

        var profilesNode = NormalizeProfiles(root["profiles"]);
        var userProfilesNode = NormalizeProfiles(userDocument?["profiles"]);
        var defaultsNode = profilesNode["defaults"] as JsonObject ?? new JsonObject();
        settings.ProfileDefaults = ResolveProfile(defaultsNode, generateGuid: false);
        settings.ProfileDefaults.Origin = SettingsOrigin.ProfilesDefaults;
        var inheritableDefaults = (JsonObject)defaultsNode.DeepClone();
        inheritableDefaults.Remove("guid");
        inheritableDefaults.Remove("name");
        inheritableDefaults.Remove("source");
        inheritableDefaults.Remove("commandline");
        var userDefaults = userProfilesNode["defaults"] as JsonObject;
        var inheritableUserDefaults = userDefaults is null
            ? null
            : (JsonObject)userDefaults.DeepClone();
        inheritableUserDefaults?.Remove("guid");
        inheritableUserDefaults?.Remove("name");
        inheritableUserDefaults?.Remove("source");
        inheritableUserDefaults?.Remove("commandline");

        if (profilesNode["list"] is JsonArray profiles)
        {
            var profileIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var profileNode in OrderProfiles(profiles, userProfilesNode["list"] as JsonArray))
            {
                var effective = (JsonObject)inheritableDefaults.DeepClone();
                MergeObject(effective, profileNode);
                if (inheritableUserDefaults is not null)
                {
                    MergeObject(effective, inheritableUserDefaults);
                }

                var userProfile = FindMatchingProfile(userProfilesNode["list"] as JsonArray, profileNode);
                if (userProfile is not null)
                {
                    MergeObject(effective, userProfile);
                }

                var profile = ResolveProfile(effective);
                var profileId = EffectiveProfileGuid(profileNode);
                profile.Origin = profileId is not null && inheritedProfileIds.Contains(profileId)
                    ? Origin(profileNode, SettingsOrigin.Inbox)
                    : SettingsOrigin.User;
                profile.SourcePath = String(profileNode, SourceKey);
                if (profile.Guid is not null && !profileIds.Add(profile.Guid))
                {
                    settings.Diagnostics.Add(new SettingsDiagnostic(
                        SettingsDiagnosticSeverity.Warning,
                        "DuplicateProfile",
                        $"A duplicate profile with GUID '{profile.Guid}' was ignored."));
                    continue;
                }

                profile.SourceDocument = CleanSourceDocument(profileNode);
                settings.Profiles.Add(profile);
            }
        }

        if (root["schemes"] is JsonArray schemes)
        {
            foreach (var schemeNode in schemes.OfType<JsonObject>())
            {
                settings.Schemes.Add(ResolveScheme(schemeNode));
            }
        }

        if (root["themes"] is JsonArray themes)
        {
            foreach (var themeNode in themes.OfType<JsonObject>())
            {
                settings.Themes.Add(ResolveTheme(themeNode));
            }
        }

        return settings;
    }

    private static ProfileSettings ResolveProfile(JsonObject profile, bool generateGuid = true)
    {
        var font = profile["font"] as JsonObject;
        var unfocused = profile["unfocusedAppearance"] as JsonObject;
        var name = String(profile, "name") ?? "Unnamed profile";
        var source = String(profile, "source");
        var guid = CanonicalGuid(String(profile, "guid"));
        if (generateGuid && string.IsNullOrWhiteSpace(guid) && !string.IsNullOrWhiteSpace(name))
        {
            guid = ProfileGuid.Create(name, source).ToString("B");
        }

        return new ProfileSettings
        {
            Guid = guid,
            Name = name,
            Source = source,
            Hidden = Bool(profile, "hidden"),
            Commandline = String(profile, "commandline") ?? @"%SystemRoot%\System32\cmd.exe",
            StartingDirectory = String(profile, "startingDirectory") ?? "%USERPROFILE%",
            Icon = String(profile, "icon"),
            ConnectionType = CanonicalGuid(String(profile, "connectionType")),
            DarkColorScheme = ColorSchemeNames(profile).DarkName ?? "Campbell",
            LightColorScheme = ColorSchemeNames(profile).LightName
                ?? ColorSchemeNames(profile).DarkName
                ?? "Campbell",
            Font = ResolveFont(profile, font),
            HistorySize = Int(profile, "historySize", 9001, minimum: 0),
            Padding = Padding(profile),
            CursorShape = String(profile, "cursorShape") ?? "bar",
            CursorHeight = Int(profile, "cursorHeight", 25, minimum: 1, maximum: 100),
            CloseOnExit = CloseOnExitValue(profile),
            TabTitle = String(profile, "tabTitle"),
            TabColor = String(profile, "tabColor"),
            SuppressApplicationTitle = Bool(profile, "suppressApplicationTitle"),
            UseAcrylic = Bool(profile, "useAcrylic"),
            Opacity = Int(profile, "opacity", AcrylicOpacity(profile), minimum: 0, maximum: 100),
            Foreground = String(profile, "foreground"),
            Background = String(profile, "background"),
            SelectionBackground = String(profile, "selectionBackground"),
            CursorColor = String(profile, "cursorColor"),
            BackgroundImage = String(profile, "backgroundImage"),
            BackgroundImageOpacity = Double(profile, "backgroundImageOpacity", 1, minimum: 0, maximum: 1),
            BackgroundImageStretchMode = String(profile, "backgroundImageStretchMode") ?? "uniformToFill",
            BackgroundImageAlignment = String(profile, "backgroundImageAlignment") ?? "center",
            RetroTerminalEffect = Bool(profile, "experimental.retroTerminalEffect"),
            PixelShaderPath = Resource(profile, "experimental.pixelShaderPath"),
            PixelShaderImagePath = Resource(profile, "experimental.pixelShaderImagePath"),
            IntenseTextStyle = String(profile, "intenseTextStyle") ?? "bright",
            AdjustIndistinguishableColors =
                String(profile, "adjustIndistinguishableColors") ?? "automatic",
            UnfocusedAppearance = unfocused is null ? null : ResolveAppearance(unfocused, profile),
            SnapOnInput = Bool(profile, "snapOnInput", true),
            AltGrAliasing = Bool(profile, "altGrAliasing", true),
            AnswerbackMessage = String(profile, "answerbackMessage"),
            ScrollbarState = String(profile, "scrollbarState") ?? "visible",
            AntialiasingMode = String(profile, "antialiasingMode") ?? "grayscale",
            BellStyle = BellStyleValue(profile["bellStyle"]),
            BellSound = Resources(profile["bellSound"]),
            RightClickContextMenu = Bool(profile, "rightClickContextMenu"),
            Elevate = Bool(profile, "elevate"),
            AutoMarkPrompts = Bool(profile, "autoMarkPrompts", true),
            ShowMarksOnScrollbar = Bool(profile, "showMarksOnScrollbar"),
            RepositionCursorWithMouse = Bool(profile, "experimental.repositionCursorWithMouse"),
            ReloadEnvironmentVariables = Bool(profile, "compatibility.reloadEnvironmentVariables", true),
            RainbowSuggestions = Bool(profile, "experimental.rainbowSuggestions"),
            ForceVtInput = Bool(profile, "compatibility.input.forceVT"),
            AllowKittyKeyboardMode = Bool(profile, "compatibility.kittyKeyboardMode", true),
            AllowVtChecksumReport = Bool(profile, "compatibility.allowDECRQCRA"),
            AllowVtClipboardWrite = Bool(profile, "compatibility.allowOSC52", true),
            AllowOscNotifications = Bool(profile, "compatibility.allowOSC777"),
            AllowKeypadMode = Bool(profile, "compatibility.allowDECNKM"),
            DragDropDelimiter = String(profile, "dragDropDelimiter") ?? " ",
            PathTranslationStyle = String(profile, "pathTranslationStyle") ?? "none",
            Environment = StringMap(profile["environment"]),
        };
    }

    private static SchemeSettings ResolveScheme(JsonObject scheme) => new()
    {
        Name = String(scheme, "name") ?? "Unnamed scheme",
        Origin = Origin(scheme, SettingsOrigin.Inbox),
        SourcePath = String(scheme, SourceKey),
        Foreground = String(scheme, "foreground") ?? "#CCCCCC",
        Background = String(scheme, "background") ?? "#0C0C0C",
        CursorColor = String(scheme, "cursorColor") ?? "#FFFFFF",
        SelectionBackground = String(scheme, "selectionBackground") ?? "#FFFFFF",
        Black = String(scheme, "black") ?? "#0C0C0C",
        Red = String(scheme, "red") ?? "#C50F1F",
        Green = String(scheme, "green") ?? "#13A10E",
        Yellow = String(scheme, "yellow") ?? "#C19C00",
        Blue = String(scheme, "blue") ?? "#0037DA",
        Purple = String(scheme, "purple") ?? "#881798",
        Cyan = String(scheme, "cyan") ?? "#3A96DD",
        White = String(scheme, "white") ?? "#CCCCCC",
        BrightBlack = String(scheme, "brightBlack") ?? "#767676",
        BrightRed = String(scheme, "brightRed") ?? "#E74856",
        BrightGreen = String(scheme, "brightGreen") ?? "#16C60C",
        BrightYellow = String(scheme, "brightYellow") ?? "#F9F1A5",
        BrightBlue = String(scheme, "brightBlue") ?? "#3B78FF",
        BrightPurple = String(scheme, "brightPurple") ?? "#B4009E",
        BrightCyan = String(scheme, "brightCyan") ?? "#61D6D6",
        BrightWhite = String(scheme, "brightWhite") ?? "#F2F2F2",
        SourceDocument = CleanSourceDocument(scheme),
    };

    private static ThemeSettings ResolveTheme(JsonObject theme)
    {
        var window = theme["window"] as JsonObject;
        var settings = theme["settings"] as JsonObject;
        var tabRow = theme["tabRow"] as JsonObject;
        var tab = theme["tab"] as JsonObject;
        return new ThemeSettings
        {
            Name = String(theme, "name") ?? "unnamed",
            Origin = Origin(theme, SettingsOrigin.Inbox),
            SourcePath = String(theme, SourceKey),
            Window = window is null ? null : new WindowThemeSettings
            {
                ApplicationTheme = String(window, "applicationTheme") ?? "system",
                Frame = ThemeColorValue(window["frame"]),
                UnfocusedFrame = ThemeColorValue(window["unfocusedFrame"]),
                RainbowFrame = Bool(window, "experimental.rainbowFrame"),
                UseMica = Bool(window, "useMica"),
                ShowWorkspacesButton = Bool(window, "showWorkspacesButton", true),
            },
            Settings = settings is null ? null : new SettingsThemeSettings
            {
                Theme = String(settings, "theme") ?? "system",
            },
            TabRow = tabRow is null ? null : new TabRowThemeSettings
            {
                Background = ThemeColorValue(tabRow["background"]),
                UnfocusedBackground = ThemeColorValue(tabRow["unfocusedBackground"]),
            },
            Tab = tab is null ? null : new TabThemeSettings
            {
                Background = ThemeColorValue(tab["background"]),
                UnfocusedBackground = ThemeColorValue(tab["unfocusedBackground"]),
                IconStyle = String(tab, "iconStyle") ?? "default",
                ShowCloseButton = String(tab, "showCloseButton") ?? "always",
            },
            SourceDocument = CleanSourceDocument(theme),
        };
    }

    private static void Validate(AppSettings settings)
    {
        if (settings.Profiles.Count == 0)
        {
            settings.Diagnostics.Add(new SettingsDiagnostic(
                SettingsDiagnosticSeverity.Error,
                "NoProfiles",
                "The settings did not define any terminal profiles."));
        }

        if (settings.Profiles.Count > 0 && settings.Profiles.All(static profile => profile.Hidden))
        {
            settings.Diagnostics.Add(new SettingsDiagnostic(
                SettingsDiagnosticSeverity.Error,
                "AllProfilesHidden",
                "All terminal profiles are hidden."));
        }

        if (!string.IsNullOrWhiteSpace(settings.DefaultProfile) &&
            !settings.Profiles.Any(profile =>
                GuidsEqual(profile.Guid, settings.DefaultProfile) ||
                string.Equals(profile.Name, settings.DefaultProfile, StringComparison.OrdinalIgnoreCase)))
        {
            settings.Diagnostics.Add(new SettingsDiagnostic(
                SettingsDiagnosticSeverity.Warning,
                "MissingDefaultProfile",
                $"The default profile '{settings.DefaultProfile}' was not found."));
        }

        var schemeNames = settings.Schemes
            .Select(static scheme => scheme.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in settings.Profiles)
        {
            if (!schemeNames.Contains(profile.DarkColorScheme) ||
                !schemeNames.Contains(profile.LightColorScheme))
            {
                settings.Diagnostics.Add(new SettingsDiagnostic(
                    SettingsDiagnosticSeverity.Warning,
                    "UnknownColorScheme",
                    $"Profile '{profile.Name}' references an unknown color scheme."));
                if (!schemeNames.Contains(profile.DarkColorScheme))
                {
                    profile.DarkColorScheme = "Campbell";
                }
                if (!schemeNames.Contains(profile.LightColorScheme))
                {
                    profile.LightColorScheme = "Campbell";
                }
            }

            foreach (var name in profile.Environment.Keys.Where(
                static name => string.IsNullOrWhiteSpace(name) || name.Contains('=')))
            {
                settings.Diagnostics.Add(new SettingsDiagnostic(
                    SettingsDiagnosticSeverity.Warning,
                    "InvalidEnvironmentVariable",
                    $"Profile '{profile.Name}' contains invalid environment variable name '{name}'."));
            }
        }

        var themeNames = settings.Themes
            .Select(static theme => theme.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missingDarkTheme = themeNames.Count > 0 && !themeNames.Contains(settings.Theme.DarkName);
        var missingLightTheme = themeNames.Count > 0 && !themeNames.Contains(settings.Theme.LightName);
        if (missingDarkTheme || missingLightTheme)
        {
            settings.Diagnostics.Add(new SettingsDiagnostic(
                SettingsDiagnosticSeverity.Warning,
                "UnknownTheme",
                $"Theme '{settings.Theme}' was not found; the system theme will be used."));
            if (missingDarkTheme)
            {
                settings.Theme.DarkName = "system";
            }
            if (missingLightTheme)
            {
                settings.Theme.LightName = "system";
            }
        }

        if (CountMenuEntries(settings.NewTabMenu, NewTabMenuEntryType.RemainingProfiles) > 1)
        {
            settings.Diagnostics.Add(new SettingsDiagnostic(
                SettingsDiagnosticSeverity.Warning,
                "DuplicateRemainingProfilesEntry",
                "Only one new-tab menu entry may have type 'remainingProfiles'."));
        }
    }

    private static int CountMenuEntries(
        IEnumerable<NewTabMenuEntry> entries,
        NewTabMenuEntryType type) =>
        entries.Sum(entry =>
            (entry.Type == type ? 1 : 0) + CountMenuEntries(entry.Entries, type));

    private static JsonObject? ParseObject(
        string json,
        string source,
        bool required,
        ICollection<SettingsDiagnostic> diagnostics)
    {
        try
        {
            var node = JsonNode.Parse(json, documentOptions: DocumentOptions);
            if (node is JsonObject result)
            {
                return result;
            }

            var diagnostic = new SettingsDiagnostic(
                SettingsDiagnosticSeverity.Error,
                "InvalidRoot",
                $"Settings root in '{source}' must be a JSON object.",
                source);
            if (required)
            {
                throw new SettingsLoadException(diagnostic);
            }

            diagnostics.Add(diagnostic);
            return null;
        }
        catch (JsonException ex)
        {
            var diagnostic = new SettingsDiagnostic(
                SettingsDiagnosticSeverity.Error,
                "InvalidJson",
                $"Could not parse settings from '{source}': {ex.Message}",
                source,
                ex.LineNumber,
                ex.BytePositionInLine);
            if (required)
            {
                throw new SettingsLoadException(diagnostic, ex);
            }

            diagnostics.Add(diagnostic);
            return null;
        }
    }

    private static void MergeRoot(JsonObject target, JsonObject layer)
    {
        foreach (var pair in layer)
        {
            if (pair.Value is null)
            {
                // An ordinary null clears the setting at this layer and resumes
                // inheritance from the already-merged parent layer.
                continue;
            }

            if (pair.Key == "profiles")
            {
                MergeProfiles(target, pair.Value);
            }
            else if (pair.Key is "schemes" or "themes")
            {
                MergeNamedArray(target, pair.Key, pair.Value as JsonArray);
            }
            else
            {
                target[pair.Key] = pair.Value?.DeepClone();
            }
        }
    }

    private static void MergeProfiles(JsonObject root, JsonNode? layerNode)
    {
        var targetProfiles = NormalizeProfiles(root["profiles"]);
        var layerProfiles = NormalizeProfiles(layerNode);
        root["profiles"] = targetProfiles;

        if (layerProfiles["defaults"] is JsonObject layerDefaults)
        {
            var targetDefaults = targetProfiles["defaults"] as JsonObject ?? new JsonObject();
            targetProfiles["defaults"] = targetDefaults;
            MergeObject(targetDefaults, layerDefaults);
        }

        var targetList = targetProfiles["list"] as JsonArray ?? [];
        targetProfiles["list"] = targetList;
        if (layerProfiles["list"] is not JsonArray layerList)
        {
            return;
        }

        foreach (var layerProfile in layerList.OfType<JsonObject>())
        {
            var updates = CanonicalGuid(String(layerProfile, "updates"));
            if (updates is not null)
            {
                var updateTarget = targetList
                    .OfType<JsonObject>()
                    .FirstOrDefault(candidate =>
                        string.Equals(EffectiveProfileGuid(candidate), updates, StringComparison.OrdinalIgnoreCase));
                if (updateTarget is not null)
                {
                    var update = (JsonObject)layerProfile.DeepClone();
                    update.Remove("updates");
                    MergeObject(updateTarget, update);
                }

                continue;
            }

            var existing = targetList
                .OfType<JsonObject>()
                .FirstOrDefault(candidate => ProfilesMatch(candidate, layerProfile));
            if (existing is null)
            {
                targetList.Add(layerProfile.DeepClone());
            }
            else
            {
                MergeObject(existing, layerProfile);
            }
        }
    }

    private static JsonObject NormalizeProfiles(JsonNode? node)
    {
        if (node is JsonObject profilesObject)
        {
            return (JsonObject)profilesObject.DeepClone();
        }

        if (node is JsonArray profilesArray)
        {
            return new JsonObject
            {
                ["defaults"] = new JsonObject(),
                ["list"] = profilesArray.DeepClone(),
            };
        }

        return new JsonObject
        {
            ["defaults"] = new JsonObject(),
            ["list"] = new JsonArray(),
        };
    }

    private static void MergeNamedArray(JsonObject root, string key, JsonArray? layer)
    {
        if (layer is null)
        {
            root[key] = null;
            return;
        }

        var target = root[key] as JsonArray ?? [];
        root[key] = target;
        foreach (var item in layer.OfType<JsonObject>())
        {
            var name = String(item, "name");
            var existing = target
                .OfType<JsonObject>()
                .FirstOrDefault(candidate => string.Equals(String(candidate, "name"), name, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                target.Add(item.DeepClone());
            }
            else if (key == "themes")
            {
                // Themes are atomic values, not inheritable graphs. Built-in
                // names are reserved and cannot be replaced by the user layer.
                if (Origin(item, SettingsOrigin.None) == SettingsOrigin.User &&
                    Origin(existing, SettingsOrigin.Inbox) != SettingsOrigin.User)
                {
                    continue;
                }

                var index = target.IndexOf(existing);
                target[index] = item.DeepClone();
            }
            else
            {
                MergeObject(existing, item);
            }
        }
    }

    private static void MergeObject(
        JsonObject target,
        JsonObject layer,
        bool preserveNullValues = false)
    {
        foreach (var pair in layer)
        {
            if (pair.Value is null)
            {
                if (preserveNullValues || IsExplicitNullableKey(pair.Key))
                {
                    target[pair.Key] = null;
                }
                continue;
            }

            if (pair.Value is JsonObject layerObject && target[pair.Key] is JsonObject targetObject)
            {
                MergeObject(
                    targetObject,
                    layerObject,
                    preserveNullValues: string.Equals(pair.Key, "environment", StringComparison.Ordinal));
            }
            else
            {
                target[pair.Key] = pair.Value?.DeepClone();
            }
        }
    }

    private static bool IsExplicitNullableKey(string key) => key is
        "tabColor" or
        "foreground" or
        "background" or
        "selectionBackground" or
        "cursorColor" or
        "frame" or
        "unfocusedFrame" or
        "unfocusedBackground";

    private static bool ProfilesMatch(JsonObject left, JsonObject right)
    {
        var leftGuid = EffectiveProfileGuid(left);
        var rightGuid = EffectiveProfileGuid(right);
        if (!string.IsNullOrWhiteSpace(leftGuid) && !string.IsNullOrWhiteSpace(rightGuid))
        {
            return string.Equals(leftGuid, rightGuid, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(String(left, "name"), String(right, "name"), StringComparison.OrdinalIgnoreCase) &&
               string.Equals(String(left, "source"), String(right, "source"), StringComparison.OrdinalIgnoreCase);
    }

    private static string? EffectiveProfileGuid(JsonObject profile)
    {
        var guid = CanonicalGuid(String(profile, "guid"));
        if (!string.IsNullOrWhiteSpace(guid))
        {
            return guid;
        }

        var name = String(profile, "name");
        return string.IsNullOrWhiteSpace(name)
            ? null
            : ProfileGuid.Create(name, String(profile, "source")).ToString("B");
    }

    private static HashSet<string> ProfileIds(JsonObject root)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var profiles = NormalizeProfiles(root["profiles"]);
        if (profiles["list"] is not JsonArray list)
        {
            return result;
        }

        foreach (var profile in list.OfType<JsonObject>())
        {
            if (EffectiveProfileGuid(profile) is { } id)
            {
                result.Add(id);
            }
        }

        return result;
    }

    private static JsonObject? FindMatchingProfile(JsonArray? profiles, JsonObject candidate)
    {
        if (profiles is null)
        {
            return null;
        }

        return profiles
            .OfType<JsonObject>()
            .FirstOrDefault(profile => ProfilesMatch(profile, candidate));
    }

    private static void PrepareFragment(JsonObject fragment, string provider)
    {
        TagNamedEntries(fragment["schemes"] as JsonArray, SettingsOrigin.Fragment, provider);
        TagNamedEntries(fragment["themes"] as JsonArray, SettingsOrigin.Fragment, provider);
        var profiles = NormalizeProfiles(fragment["profiles"]);
        fragment["profiles"] = profiles;
        if (profiles["list"] is not JsonArray list)
        {
            return;
        }

        foreach (var profile in list.OfType<JsonObject>())
        {
            if (profile["updates"] is null)
            {
                profile["source"] = provider;
            }

            profile[OriginKey] = SettingsOrigin.Fragment.ToString();
            profile[SourceKey] = provider;
        }
    }

    private static void PrepareGeneratedProfiles(JsonObject layer, string source)
    {
        var profiles = NormalizeProfiles(layer["profiles"]);
        layer["profiles"] = profiles;
        if (profiles["list"] is not JsonArray list)
        {
            return;
        }

        foreach (var profile in list.OfType<JsonObject>())
        {
            profile[OriginKey] ??= SettingsOrigin.Generated.ToString();
            profile[SourceKey] = source;
        }
    }

    private static void TagNamedEntries(
        JsonArray? entries,
        SettingsOrigin origin,
        string source)
    {
        if (entries is null)
        {
            return;
        }

        foreach (var entry in entries.OfType<JsonObject>())
        {
            entry[OriginKey] = origin.ToString();
            entry[SourceKey] = source;
        }
    }

    private static IEnumerable<JsonObject> ExtractFragmentUpdates(JsonObject fragment)
    {
        var profiles = NormalizeProfiles(fragment["profiles"]);
        fragment["profiles"] = profiles;
        if (profiles["list"] is not JsonArray list)
        {
            return [];
        }

        var updates = new List<JsonObject>();
        for (var index = list.Count - 1; index >= 0; index--)
        {
            if (list[index] is JsonObject profile && profile["updates"] is not null)
            {
                updates.Add((JsonObject)profile.DeepClone());
                list.RemoveAt(index);
            }
        }

        updates.Reverse();
        return updates;
    }

    private static void ApplyFragmentUpdates(JsonObject root, IEnumerable<JsonObject> updates)
    {
        var profiles = NormalizeProfiles(root["profiles"]);
        root["profiles"] = profiles;
        var list = profiles["list"] as JsonArray ?? [];
        profiles["list"] = list;
        foreach (var update in updates)
        {
            var targetGuid = CanonicalGuid(String(update, "updates"));
            var target = list
                .OfType<JsonObject>()
                .FirstOrDefault(profile =>
                    string.Equals(EffectiveProfileGuid(profile), targetGuid, StringComparison.OrdinalIgnoreCase));
            if (target is null)
            {
                continue;
            }

            var layer = (JsonObject)update.DeepClone();
            layer.Remove("updates");
            layer.Remove("source");
            layer.Remove(OriginKey);
            layer.Remove(SourceKey);
            MergeObject(target, layer);
        }
    }

    private static string FragmentProvider(string source)
    {
        var directory = Path.GetDirectoryName(source);
        return string.IsNullOrWhiteSpace(directory)
            ? source
            : Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }

    private static string? CanonicalGuid(string? value) =>
        Guid.TryParse(value, out var guid) ? guid.ToString("B") : value;

    private static bool GuidsEqual(string? left, string? right) =>
        Guid.TryParse(left, out var leftGuid) &&
        Guid.TryParse(right, out var rightGuid) &&
        leftGuid == rightGuid;

    private static Dictionary<string, string?> StringMap(JsonNode? node)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (node is not JsonObject map)
        {
            return result;
        }

        foreach (var pair in map)
        {
            result[pair.Key] = pair.Value is null ? null : StringValue(pair.Value);
        }

        return result;
    }

    private static JsonObject SerializeResolvedSettings(AppSettings settings) => new()
    {
        ["language"] = settings.Language,
        ["warning.inputService"] = settings.InputServiceWarning,
        ["firstWindowPreference"] = settings.FirstWindowPreference,
        ["debugFeatures"] = settings.DebugFeaturesEnabled,
        ["windowingBehavior"] = settings.WindowingBehavior,
        ["alwaysShowNotificationIcon"] = settings.AlwaysShowNotificationIcon,
        ["disabledProfileSources"] = StringArray(settings.DisabledProfileSources),
        ["compatibility.allowHeadless"] = settings.AllowHeadless,
        ["experimental.enableColorSelection"] = settings.EnableColorSelection,
        ["defaultProfile"] = settings.DefaultProfile,
        ["initialCols"] = settings.InitialCols,
        ["initialRows"] = settings.InitialRows,
        ["initialPosition"] = settings.InitialPosition,
        ["centerOnLaunch"] = settings.CenterOnLaunch,
        ["launchMode"] = LaunchModeString(settings.LaunchMode),
        ["alwaysOnTop"] = settings.AlwaysOnTop,
        ["autoHideWindow"] = settings.AutoHideWindow,
        ["alwaysShowTabs"] = settings.AlwaysShowTabs,
        ["showTabsInTitlebar"] = settings.ShowTabsInTitlebar,
        ["showTerminalTitleInTitlebar"] = settings.ShowTerminalTitleInTitlebar,
        ["showTabsFullscreen"] = settings.ShowTabsFullscreen,
        ["copyOnSelect"] = settings.CopyOnSelect,
        ["copyFormatting"] = CopyFormatsNode(
            settings.CopyFormatting
                ? settings.CopyFormatFormats == CopyFormat.None ? CopyFormat.All : settings.CopyFormatFormats
                : CopyFormat.None),
        ["trimBlockSelection"] = settings.TrimBlockSelection,
        ["trimPaste"] = settings.TrimPaste,
        ["focusFollowMouse"] = settings.FocusFollowMouse,
        ["experimental.scrollToZoom"] = settings.ScrollToZoom,
        ["experimental.scrollToChangeOpacity"] = settings.ScrollToChangeOpacity,
        ["rendering.graphicsAPI"] = settings.GraphicsApi,
        ["rendering.disablePartialInvalidation"] = settings.DisablePartialInvalidation,
        ["rendering.software"] = settings.SoftwareRendering,
        ["compatibility.textMeasurement"] = settings.TextMeasurement,
        ["compatibility.ambiguousWidth"] = settings.AmbiguousWidth,
        ["defaultInputScope"] = settings.DefaultInputScope,
        ["experimental.useBackgroundImageForWindow"] = settings.UseBackgroundImageForWindow,
        ["experimental.detectURLs"] = settings.DetectUrls,
        ["newTabPosition"] = settings.NewTabPosition,
        ["snapToGridOnResize"] = settings.SnapToGridOnResize,
        ["disableAnimations"] = settings.DisableAnimations,
        ["minimizeToNotificationArea"] = settings.MinimizeToNotificationArea,
        ["showAdminShield"] = settings.ShowAdminShield,
        ["theme"] = ThemePairNode(settings.Theme),
        ["startupActions"] = settings.StartupActions,
        ["wordDelimiters"] = settings.WordDelimiters,
        ["tabWidthMode"] = TabWidthModeString(settings.TabWidthMode),
        ["warning.confirmOnClose"] = ConfirmOnCloseString(settings.ConfirmOnClose),
        ["useAcrylicInTabRow"] = settings.UseAcrylicInTabRow,
        ["warning.largePaste"] = settings.WarnAboutLargePaste,
        ["warning.multiLinePaste"] = settings.WarnAboutMultiLinePaste,
        ["tabSwitcherMode"] = settings.TabSwitcherMode,
        ["safeUriSchemes"] = StringArray(settings.SafeUriSchemes),
        ["experimental.enableShellCompletionMenu"] = settings.EnableShellCompletionMenu,
        ["compatibility.enableUnfocusedAcrylic"] = settings.EnableUnfocusedAcrylic,
        ["newTabMenu"] = SerializeNewTabMenu(settings.NewTabMenu),
        ["searchWebDefaultQueryUrl"] = settings.SearchWebDefaultQueryUrl,
        ["profiles"] = SerializeProfiles(settings),
        ["schemes"] = new JsonArray(settings.Schemes.Select(SerializeScheme).ToArray()),
        ["themes"] = new JsonArray(settings.Themes.Select(SerializeTheme).ToArray()),
        ["actions"] = settings.Actions.DeepClone(),
        ["keybindings"] = settings.Keybindings.DeepClone(),
    };

    [RequiresDynamicCode("Calls Microsoft.Terminal.Settings.SettingsLoader.ApplyProfileChanges(JsonObject, JsonObject, JsonObject)")]
    [RequiresUnreferencedCode("Calls Microsoft.Terminal.Settings.SettingsLoader.ApplyProfileChanges(JsonObject, JsonObject, JsonObject)")]
    private static void ApplyResolvedChanges(
        JsonObject document,
        JsonObject baseline,
        JsonObject current,
        IReadOnlySet<string> inheritedProfileIds)
    {
        foreach (var pair in current)
        {
            if (pair.Key is "profiles" or "schemes" or "themes")
            {
                continue;
            }

            if (!JsonNode.DeepEquals(pair.Value, baseline[pair.Key]))
            {
                document[pair.Key] = pair.Value?.DeepClone();
            }
        }

        ApplyProfileChanges(
            document,
            baseline["profiles"] as JsonObject,
            current["profiles"] as JsonObject,
            inheritedProfileIds);
        ApplyNamedArrayChanges(
            document,
            "schemes",
            baseline["schemes"] as JsonArray,
            current["schemes"] as JsonArray);
        ApplyNamedArrayChanges(
            document,
            "themes",
            baseline["themes"] as JsonArray,
            current["themes"] as JsonArray);
    }

    [RequiresUnreferencedCode("Calls System.Text.Json.Nodes.JsonArray.Add<T>(T)")]
    [RequiresDynamicCode("Calls System.Text.Json.Nodes.JsonArray.Add<T>(T)")]
    private static void ApplyProfileChanges(
        JsonObject document,
        JsonObject? baselineProfiles,
        JsonObject? currentProfiles,
        IReadOnlySet<string> inheritedProfileIds)
    {
        if (currentProfiles is null)
        {
            return;
        }

        var target = NormalizeProfiles(document["profiles"]);
        document["profiles"] = target;
        var targetDefaults = target["defaults"] as JsonObject ?? new JsonObject();
        target["defaults"] = targetDefaults;
        ApplyObjectChanges(
            targetDefaults,
            baselineProfiles?["defaults"] as JsonObject,
            currentProfiles["defaults"] as JsonObject);

        var targetList = target["list"] as JsonArray ?? [];
        target["list"] = targetList;
        var baselineList = baselineProfiles?["list"] as JsonArray ?? [];
        var currentList = currentProfiles["list"] as JsonArray ?? [];

        foreach (var currentProfile in currentList.OfType<JsonObject>())
        {
            var baselineProfile = FindMatchingProfile(baselineList, currentProfile);
            if (baselineProfile is not null && JsonNode.DeepEquals(baselineProfile, currentProfile))
            {
                continue;
            }

            var targetProfile = FindMatchingProfile(targetList, currentProfile);
            if (targetProfile is null)
            {
                targetProfile = new JsonObject
                {
                    ["guid"] = currentProfile["guid"]?.DeepClone(),
                    ["name"] = currentProfile["name"]?.DeepClone(),
                    ["source"] = currentProfile["source"]?.DeepClone(),
                };
                targetList.Add(targetProfile);
            }

            ApplyObjectChanges(targetProfile, baselineProfile, currentProfile);
        }

        foreach (var baselineProfile in baselineList.OfType<JsonObject>())
        {
            if (FindMatchingProfile(currentList, baselineProfile) is not null)
            {
                continue;
            }

            var targetProfile = FindMatchingProfile(targetList, baselineProfile);
            var profileId = EffectiveProfileGuid(baselineProfile);
            var isInherited = profileId is not null && inheritedProfileIds.Contains(profileId);
            if (isInherited)
            {
                targetProfile ??= new JsonObject
                {
                    ["guid"] = baselineProfile["guid"]?.DeepClone(),
                    ["name"] = baselineProfile["name"]?.DeepClone(),
                    ["source"] = baselineProfile["source"]?.DeepClone(),
                };
                if (targetProfile.Parent is null)
                {
                    targetList.Add(targetProfile);
                }

                targetProfile["hidden"] = true;
            }
            else if (targetProfile is not null)
            {
                targetList.Remove(targetProfile);
            }
        }
    }

    private static void ApplyNamedArrayChanges(
        JsonObject document,
        string key,
        JsonArray? baseline,
        JsonArray? current)
    {
        if (current is null)
        {
            return;
        }

        var target = document[key] as JsonArray ?? [];
        document[key] = target;
        baseline ??= [];

        foreach (var currentItem in current.OfType<JsonObject>())
        {
            var name = String(currentItem, "name");
            var baselineItem = FindNamed(baseline, name);
            if (baselineItem is not null && JsonNode.DeepEquals(baselineItem, currentItem))
            {
                continue;
            }

            var targetItem = FindNamed(target, name);
            if (targetItem is null)
            {
                target.Add(currentItem.DeepClone());
            }
            else
            {
                ApplyObjectChanges(targetItem, baselineItem, currentItem);
            }
        }

        foreach (var baselineItem in baseline.OfType<JsonObject>())
        {
            var name = String(baselineItem, "name");
            if (FindNamed(current, name) is null && FindNamed(target, name) is { } targetItem)
            {
                target.Remove(targetItem);
            }
        }
    }

    private static JsonObject? FindNamed(JsonArray array, string? name) =>
        array.OfType<JsonObject>().FirstOrDefault(
            item => string.Equals(String(item, "name"), name, StringComparison.OrdinalIgnoreCase));

    private static void ApplyObjectChanges(JsonObject target, JsonObject? baseline, JsonObject? current)
    {
        if (current is null)
        {
            return;
        }

        foreach (var pair in current)
        {
            var baselineValue = baseline?[pair.Key];
            if (JsonNode.DeepEquals(pair.Value, baselineValue))
            {
                continue;
            }

            if (pair.Value is JsonObject currentObject)
            {
                var targetObject = target[pair.Key] as JsonObject ?? new JsonObject();
                target[pair.Key] = targetObject;
                ApplyObjectChanges(targetObject, baselineValue as JsonObject, currentObject);
            }
            else
            {
                target[pair.Key] = pair.Value?.DeepClone();
            }
        }

        if (baseline is null)
        {
            return;
        }

        foreach (var pair in baseline)
        {
            if (!current.ContainsKey(pair.Key))
            {
                target[pair.Key] = null;
            }
        }
    }

    private static JsonObject SerializeProfiles(AppSettings settings)
    {
        var defaults = SerializeProfile(settings.ProfileDefaults, includeIdentity: false);
        defaults.Remove("name");
        defaults.Remove("guid");
        defaults.Remove("source");
        defaults.Remove("commandline");

        return new JsonObject
        {
            ["defaults"] = defaults,
            ["list"] = new JsonArray(settings.Profiles
                .Select(profile => SerializeProfile(profile, includeIdentity: true))
                .ToArray()),
        };
    }

    private static JsonObject SerializeProfile(ProfileSettings profile, bool includeIdentity)
    {
        var result = profile.SourceDocument is null
            ? new JsonObject()
            : (JsonObject)profile.SourceDocument.DeepClone();

        if (includeIdentity)
        {
            result["guid"] = profile.Guid;
            result["name"] = profile.Name;
            result["source"] = profile.Source;
            result["commandline"] = profile.Commandline;
            result["hidden"] = profile.Hidden;
        }

        result["startingDirectory"] = profile.StartingDirectory;
        result["icon"] = profile.Icon;
        result["connectionType"] = profile.ConnectionType;
        result["colorScheme"] = ColorSchemeNode(profile.DarkColorScheme, profile.LightColorScheme);
        result["font"] = new JsonObject
        {
            ["face"] = profile.FontFace,
            ["size"] = profile.FontSize,
            ["weight"] = profile.FontWeight,
            ["axes"] = DoubleMap(profile.Font.Axes),
            ["features"] = DoubleMap(profile.Font.Features),
            ["builtinGlyphs"] = profile.Font.BuiltinGlyphs,
            ["colorGlyphs"] = profile.Font.ColorGlyphs,
            ["cellWidth"] = profile.Font.CellWidth,
            ["cellHeight"] = profile.Font.CellHeight,
        };
        result["historySize"] = profile.HistorySize;
        result["padding"] = profile.Padding;
        result["cursorShape"] = profile.CursorShape;
        result["cursorHeight"] = profile.CursorHeight;
        result["closeOnExit"] = CloseOnExitString(profile.CloseOnExit);
        result["tabTitle"] = profile.TabTitle;
        result["tabColor"] = profile.TabColor;
        result["suppressApplicationTitle"] = profile.SuppressApplicationTitle;
        result["useAcrylic"] = profile.UseAcrylic;
        result["opacity"] = profile.Opacity;
        result["foreground"] = profile.Foreground;
        result["background"] = profile.Background;
        result["selectionBackground"] = profile.SelectionBackground;
        result["cursorColor"] = profile.CursorColor;
        result["backgroundImage"] = profile.BackgroundImage;
        result["backgroundImageOpacity"] = profile.BackgroundImageOpacity;
        result["backgroundImageStretchMode"] = profile.BackgroundImageStretchMode;
        result["backgroundImageAlignment"] = profile.BackgroundImageAlignment;
        result["experimental.retroTerminalEffect"] = profile.RetroTerminalEffect;
        result["experimental.pixelShaderPath"] = ResourceNode(profile.PixelShaderPath);
        result["experimental.pixelShaderImagePath"] = ResourceNode(profile.PixelShaderImagePath);
        result["intenseTextStyle"] = profile.IntenseTextStyle;
        result["adjustIndistinguishableColors"] = profile.AdjustIndistinguishableColors;
        result["unfocusedAppearance"] = profile.UnfocusedAppearance is null
            ? null
            : SerializeAppearance(profile.UnfocusedAppearance);
        result["snapOnInput"] = profile.SnapOnInput;
        result["altGrAliasing"] = profile.AltGrAliasing;
        result["answerbackMessage"] = profile.AnswerbackMessage;
        result["scrollbarState"] = profile.ScrollbarState;
        result["antialiasingMode"] = profile.AntialiasingMode;
        result["bellStyle"] = BellStyleNode(profile.BellStyle);
        result["bellSound"] = new JsonArray(profile.BellSound.Select(ResourceNode).ToArray());
        result["rightClickContextMenu"] = profile.RightClickContextMenu;
        result["elevate"] = profile.Elevate;
        result["autoMarkPrompts"] = profile.AutoMarkPrompts;
        result["showMarksOnScrollbar"] = profile.ShowMarksOnScrollbar;
        result["experimental.repositionCursorWithMouse"] = profile.RepositionCursorWithMouse;
        result["compatibility.reloadEnvironmentVariables"] = profile.ReloadEnvironmentVariables;
        result["experimental.rainbowSuggestions"] = profile.RainbowSuggestions;
        result["compatibility.input.forceVT"] = profile.ForceVtInput;
        result["compatibility.kittyKeyboardMode"] = profile.AllowKittyKeyboardMode;
        result["compatibility.allowDECRQCRA"] = profile.AllowVtChecksumReport;
        result["compatibility.allowOSC52"] = profile.AllowVtClipboardWrite;
        result["compatibility.allowOSC777"] = profile.AllowOscNotifications;
        result["compatibility.allowDECNKM"] = profile.AllowKeypadMode;
        result["dragDropDelimiter"] = profile.DragDropDelimiter;
        result["pathTranslationStyle"] = profile.PathTranslationStyle;
        result["environment"] = new JsonObject(profile.Environment
            .Select(static pair => KeyValuePair.Create<string, JsonNode?>(
                pair.Key,
                pair.Value is null ? null : JsonValue.Create(pair.Value))));
        return result;
    }

    private static JsonObject SerializeScheme(SchemeSettings scheme)
    {
        var result = scheme.SourceDocument is null
            ? new JsonObject()
            : (JsonObject)scheme.SourceDocument.DeepClone();
        result.Remove(OriginKey);
        result.Remove(SourceKey);
        result["name"] = scheme.Name;
        result["foreground"] = scheme.Foreground;
        result["background"] = scheme.Background;
        result["cursorColor"] = scheme.CursorColor;
        result["selectionBackground"] = scheme.SelectionBackground;
        result["black"] = scheme.Black;
        result["red"] = scheme.Red;
        result["green"] = scheme.Green;
        result["yellow"] = scheme.Yellow;
        result["blue"] = scheme.Blue;
        result["purple"] = scheme.Purple;
        result["cyan"] = scheme.Cyan;
        result["white"] = scheme.White;
        result["brightBlack"] = scheme.BrightBlack;
        result["brightRed"] = scheme.BrightRed;
        result["brightGreen"] = scheme.BrightGreen;
        result["brightYellow"] = scheme.BrightYellow;
        result["brightBlue"] = scheme.BrightBlue;
        result["brightPurple"] = scheme.BrightPurple;
        result["brightCyan"] = scheme.BrightCyan;
        result["brightWhite"] = scheme.BrightWhite;
        return result;
    }

    private static JsonObject SerializeTheme(ThemeSettings theme)
    {
        var result = theme.SourceDocument is null
            ? new JsonObject()
            : (JsonObject)theme.SourceDocument.DeepClone();
        result["name"] = theme.Name;
        result.Remove(OriginKey);
        result.Remove(SourceKey);

        result["window"] = theme.Window is null ? null : new JsonObject
        {
            ["applicationTheme"] = theme.Window.ApplicationTheme,
            ["frame"] = ThemeColorNode(theme.Window.Frame),
            ["unfocusedFrame"] = ThemeColorNode(theme.Window.UnfocusedFrame),
            ["experimental.rainbowFrame"] = theme.Window.RainbowFrame,
            ["useMica"] = theme.Window.UseMica,
            ["showWorkspacesButton"] = theme.Window.ShowWorkspacesButton,
        };
        result["settings"] = theme.Settings is null ? null : new JsonObject
        {
            ["theme"] = theme.Settings.Theme,
        };
        result["tabRow"] = theme.TabRow is null ? null : new JsonObject
        {
            ["background"] = ThemeColorNode(theme.TabRow.Background),
            ["unfocusedBackground"] = ThemeColorNode(theme.TabRow.UnfocusedBackground),
        };
        result["tab"] = theme.Tab is null ? null : new JsonObject
        {
            ["background"] = ThemeColorNode(theme.Tab.Background),
            ["unfocusedBackground"] = ThemeColorNode(theme.Tab.UnfocusedBackground),
            ["iconStyle"] = theme.Tab.IconStyle,
            ["showCloseButton"] = theme.Tab.ShowCloseButton,
        };
        return result;
    }

    private static FontSettings ResolveFont(JsonObject profile, JsonObject? font)
    {
        if (font is null)
        {
            return new FontSettings
            {
                Face = String(profile, "fontFace") ?? "Cascadia Mono",
                Size = Double(profile, "fontSize", 12, minimum: 1),
                Weight = LegacyFontWeight(profile),
            };
        }

        return new FontSettings
        {
            Face = font.ContainsKey("face")
                ? String(font, "face") ?? "Cascadia Mono"
                : String(profile, "fontFace") ?? "Cascadia Mono",
            Size = font.ContainsKey("size")
                ? Double(font, "size", 12, minimum: 1)
                : Double(profile, "fontSize", 12, minimum: 1),
            Weight = font.ContainsKey("weight") ? FontWeight(font) : LegacyFontWeight(profile),
            Axes = DoubleMap(font["axes"]),
            Features = DoubleMap(font["features"]),
            BuiltinGlyphs = Bool(font, "builtinGlyphs", true),
            ColorGlyphs = Bool(font, "colorGlyphs", true),
            CellWidth = String(font, "cellWidth"),
            CellHeight = String(font, "cellHeight"),
        };
    }

    private static AppearanceSettings ResolveAppearance(JsonObject appearance, JsonObject focused)
    {
        var appearanceSchemes = ColorSchemeNames(appearance);
        var focusedSchemes = ColorSchemeNames(focused);
        return new AppearanceSettings
        {
            CursorShape = String(appearance, "cursorShape") ?? String(focused, "cursorShape") ?? "bar",
            CursorHeight = Int(
                appearance,
                "cursorHeight",
                Int(focused, "cursorHeight", 25, minimum: 1, maximum: 100),
                minimum: 1,
                maximum: 100),
            Foreground = InheritedNullableString(appearance, focused, "foreground"),
            Background = InheritedNullableString(appearance, focused, "background"),
            SelectionBackground = InheritedNullableString(
                appearance,
                focused,
                "selectionBackground"),
            CursorColor = InheritedNullableString(appearance, focused, "cursorColor"),
            BackgroundImage =
                Resource(appearance, "backgroundImage") ?? Resource(focused, "backgroundImage"),
            BackgroundImageOpacity = Double(
                appearance,
                "backgroundImageOpacity",
                Double(focused, "backgroundImageOpacity", 1, minimum: 0, maximum: 1),
                minimum: 0,
                maximum: 1),
            BackgroundImageStretchMode =
                String(appearance, "backgroundImageStretchMode")
                ?? String(focused, "backgroundImageStretchMode")
                ?? "uniformToFill",
            BackgroundImageAlignment =
                String(appearance, "backgroundImageAlignment")
                ?? String(focused, "backgroundImageAlignment")
                ?? "center",
            RetroTerminalEffect = Bool(
                appearance,
                "experimental.retroTerminalEffect",
                Bool(focused, "experimental.retroTerminalEffect")),
            PixelShaderPath =
                Resource(appearance, "experimental.pixelShaderPath")
                ?? Resource(focused, "experimental.pixelShaderPath"),
            PixelShaderImagePath =
                Resource(appearance, "experimental.pixelShaderImagePath")
                ?? Resource(focused, "experimental.pixelShaderImagePath"),
            IntenseTextStyle =
                String(appearance, "intenseTextStyle")
                ?? String(focused, "intenseTextStyle")
                ?? "bright",
            AdjustIndistinguishableColors =
                String(appearance, "adjustIndistinguishableColors")
                ?? String(focused, "adjustIndistinguishableColors")
                ?? "automatic",
            UseAcrylic = Bool(appearance, "useAcrylic", Bool(focused, "useAcrylic")),
            Opacity = Int(
                appearance,
                "opacity",
                Int(focused, "opacity", AcrylicOpacity(focused), minimum: 0, maximum: 100),
                minimum: 0,
                maximum: 100),
            DarkColorScheme =
                appearanceSchemes.DarkName ?? focusedSchemes.DarkName ?? "Campbell",
            LightColorScheme =
                appearanceSchemes.LightName ?? focusedSchemes.LightName ?? "Campbell",
        };
    }

    private static string? InheritedNullableString(
        JsonObject appearance,
        JsonObject focused,
        string key) =>
        appearance.ContainsKey(key) ? String(appearance, key) : String(focused, key);

    private static JsonObject SerializeAppearance(AppearanceSettings appearance) => new()
    {
        ["cursorShape"] = appearance.CursorShape,
        ["cursorHeight"] = appearance.CursorHeight,
        ["foreground"] = appearance.Foreground,
        ["background"] = appearance.Background,
        ["selectionBackground"] = appearance.SelectionBackground,
        ["cursorColor"] = appearance.CursorColor,
        ["backgroundImage"] = ResourceNode(appearance.BackgroundImage),
        ["backgroundImageOpacity"] = appearance.BackgroundImageOpacity,
        ["backgroundImageStretchMode"] = appearance.BackgroundImageStretchMode,
        ["backgroundImageAlignment"] = appearance.BackgroundImageAlignment,
        ["experimental.retroTerminalEffect"] = appearance.RetroTerminalEffect,
        ["experimental.pixelShaderPath"] = ResourceNode(appearance.PixelShaderPath),
        ["experimental.pixelShaderImagePath"] = ResourceNode(appearance.PixelShaderImagePath),
        ["intenseTextStyle"] = appearance.IntenseTextStyle,
        ["adjustIndistinguishableColors"] = appearance.AdjustIndistinguishableColors,
        ["useAcrylic"] = appearance.UseAcrylic,
        ["opacity"] = appearance.Opacity,
        ["colorScheme"] = ColorSchemeNode(appearance.DarkColorScheme, appearance.LightColorScheme),
    };

    private static List<NewTabMenuEntry> ResolveNewTabMenu(
        JsonNode? node,
        bool useDefaultWhenAbsent = true)
    {
        if (node is not JsonArray array)
        {
            return useDefaultWhenAbsent
                ? [new NewTabMenuEntry { Type = NewTabMenuEntryType.RemainingProfiles }]
                : [];
        }

        return array
            .OfType<JsonObject>()
            .Select(ResolveNewTabMenuEntry)
            .Where(static entry => entry is not null)
            .Cast<NewTabMenuEntry>()
            .ToList();
    }

    private static NewTabMenuEntry? ResolveNewTabMenuEntry(JsonObject source)
    {
        var type = String(source, "type")?.ToLowerInvariant() switch
        {
            "profile" => NewTabMenuEntryType.Profile,
            "separator" => NewTabMenuEntryType.Separator,
            "folder" => NewTabMenuEntryType.Folder,
            "remainingprofiles" => NewTabMenuEntryType.RemainingProfiles,
            "matchprofiles" => NewTabMenuEntryType.MatchProfiles,
            "action" => NewTabMenuEntryType.Action,
            _ => NewTabMenuEntryType.Invalid,
        };
        if (type == NewTabMenuEntryType.Invalid)
        {
            return null;
        }

        return new NewTabMenuEntry
        {
            Type = type,
            Profile = String(source, "profile"),
            ActionId = String(source, "action"),
            Name = String(source, "name"),
            Icon = Resource(source, "icon"),
            Inlining = String(source, "inline") ?? "never",
            AllowEmpty = Bool(source, "allowEmpty"),
            MatchName = String(source, "name"),
            MatchCommandline = String(source, "commandline"),
            MatchSource = String(source, "source"),
            Entries = ResolveNewTabMenu(source["entries"], useDefaultWhenAbsent: false),
            SourceDocument = (JsonObject)source.DeepClone(),
        };
    }

    private static JsonArray SerializeNewTabMenu(IEnumerable<NewTabMenuEntry> entries) =>
        new(entries.Select(SerializeNewTabMenuEntry).ToArray());

    private static JsonObject SerializeNewTabMenuEntry(NewTabMenuEntry entry)
    {
        var result = entry.SourceDocument is null
            ? new JsonObject()
            : (JsonObject)entry.SourceDocument.DeepClone();
        result["type"] = entry.Type switch
        {
            NewTabMenuEntryType.Profile => "profile",
            NewTabMenuEntryType.Separator => "separator",
            NewTabMenuEntryType.Folder => "folder",
            NewTabMenuEntryType.RemainingProfiles => "remainingProfiles",
            NewTabMenuEntryType.MatchProfiles => "matchProfiles",
            NewTabMenuEntryType.Action => "action",
            _ => "invalid",
        };

        if (entry.Type == NewTabMenuEntryType.Profile)
        {
            result["profile"] = entry.Profile;
        }
        else if (entry.Type == NewTabMenuEntryType.Action)
        {
            result["action"] = entry.ActionId;
        }
        else if (entry.Type == NewTabMenuEntryType.Folder)
        {
            result["name"] = entry.Name;
            result["icon"] = ResourceNode(entry.Icon);
            result["inline"] = entry.Inlining;
            result["allowEmpty"] = entry.AllowEmpty;
            result["entries"] = SerializeNewTabMenu(entry.Entries);
        }
        else if (entry.Type == NewTabMenuEntryType.MatchProfiles)
        {
            result["name"] = entry.MatchName;
            result["commandline"] = entry.MatchCommandline;
            result["source"] = entry.MatchSource;
        }

        return result;
    }

    private static ThemePair ThemePairValue(JsonNode? node)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var name))
        {
            return new ThemePair { DarkName = name, LightName = name };
        }

        if (node is JsonObject pair)
        {
            return new ThemePair
            {
                DarkName = String(pair, "dark") ?? "system",
                LightName = String(pair, "light") ?? "system",
            };
        }

        return new ThemePair();
    }

    private static JsonNode ThemePairNode(ThemePair pair) =>
        string.Equals(pair.DarkName, pair.LightName, StringComparison.Ordinal)
            ? JsonValue.Create(pair.DarkName)
            : new JsonObject
            {
                ["dark"] = pair.DarkName,
                ["light"] = pair.LightName,
            };

    private static (string? DarkName, string? LightName) ColorSchemeNames(JsonObject profile)
    {
        if (profile["colorScheme"] is JsonValue value && value.TryGetValue<string>(out var name))
        {
            return (name, name);
        }

        if (profile["colorScheme"] is JsonObject pair)
        {
            return (String(pair, "dark"), String(pair, "light"));
        }

        return (null, null);
    }

    private static JsonNode ColorSchemeNode(string dark, string light) =>
        string.Equals(dark, light, StringComparison.Ordinal)
            ? JsonValue.Create(dark)
            : new JsonObject { ["dark"] = dark, ["light"] = light };

    private static ThemeColor? ThemeColorValue(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text)
            ? new ThemeColor { Value = text }
            : null;

    private static JsonNode? ThemeColorNode(ThemeColor? color) =>
        color is null ? null : JsonValue.Create(color.Value);

    private static MediaResource? Resource(JsonObject root, string key) =>
        root.TryGetPropertyValue(key, out var node)
            ? new MediaResource { Path = node is null ? null : StringValue(node) }
            : null;

    private static List<MediaResource> Resources(JsonNode? node)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var path))
        {
            return [new MediaResource { Path = path }];
        }

        return node is JsonArray array
            ? array.Select(static item => new MediaResource
                {
                    Path = item is null ? null : StringValue(item),
                })
                .ToList()
            : [];
    }

    private static JsonNode? ResourceNode(MediaResource? resource) =>
        resource?.Path is null ? null : JsonValue.Create(resource.Path);

    private static BellStyle BellStyleValue(JsonNode? node)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue<string>(out var single))
            {
                return BellStyleName(single);
            }

            if (value.TryGetValue<bool>(out var enabled))
            {
                return enabled ? BellStyle.Audible : BellStyle.None;
            }
        }

        if (node is not JsonArray array)
        {
            return BellStyle.Audible;
        }

        var result = BellStyle.None;
        foreach (var name in array.Select(static item => item is null ? null : StringValue(item)))
        {
            result |= BellStyleName(name);
        }

        return result;
    }

    private static BellStyle BellStyleName(string? name) => name?.ToLowerInvariant() switch
    {
        "audible" => BellStyle.Audible,
        "window" => BellStyle.Window,
        "taskbar" => BellStyle.Taskbar,
        "notification" => BellStyle.Notification,
        "all" => BellStyle.All,
        _ => BellStyle.None,
    };

    private static JsonNode BellStyleNode(BellStyle style)
    {
        if (style == BellStyle.None)
        {
            return JsonValue.Create("none");
        }

        if (style == BellStyle.All)
        {
            return JsonValue.Create("all");
        }

        var values = new JsonArray();
        if (style.HasFlag(BellStyle.Audible)) values.Add((JsonNode?)JsonValue.Create("audible"));
        if (style.HasFlag(BellStyle.Window)) values.Add((JsonNode?)JsonValue.Create("window"));
        if (style.HasFlag(BellStyle.Taskbar)) values.Add((JsonNode?)JsonValue.Create("taskbar"));
        if (style.HasFlag(BellStyle.Notification)) values.Add((JsonNode?)JsonValue.Create("notification"));
        return values.Count == 1 ? values[0]!.DeepClone() : values;
    }

    private static CopyFormat CopyFormats(JsonObject root)
    {
        if (root["copyFormatting"] is JsonValue value)
        {
            if (value.TryGetValue<bool>(out var boolean))
            {
                return boolean ? CopyFormat.All : CopyFormat.None;
            }

            if (value.TryGetValue<string>(out var single))
            {
                return CopyFormatName(single);
            }
        }

        if (root["copyFormatting"] is not JsonArray array)
        {
            return CopyFormat.None;
        }

        var result = CopyFormat.None;
        foreach (var name in array.Select(static item => item is null ? null : StringValue(item)))
        {
            result |= CopyFormatName(name);
        }

        return result;
    }

    private static CopyFormat CopyFormatName(string? name) => name?.ToLowerInvariant() switch
    {
        "html" => CopyFormat.Html,
        "rtf" => CopyFormat.Rtf,
        "all" => CopyFormat.All,
        _ => CopyFormat.None,
    };

    private static JsonNode CopyFormatsNode(CopyFormat formats) => formats switch
    {
        CopyFormat.None => JsonValue.Create(false),
        CopyFormat.All => JsonValue.Create(true),
        CopyFormat.Html => JsonValue.Create("html"),
        CopyFormat.Rtf => JsonValue.Create("rtf"),
        _ => JsonValue.Create(false),
    };

    private static JsonArray StringArray(IEnumerable<string> values) =>
        new(values.Select(static value => (JsonNode?)JsonValue.Create(value)).ToArray());

    private static List<string> StringList(JsonNode? node) =>
        node is JsonArray array
            ? array.Select(static item => item is null ? null : StringValue(item))
                .Where(static value => value is not null)
                .Cast<string>()
                .ToList()
            : [];

    private static JsonArray CloneArray(JsonNode? node) =>
        node is JsonArray array ? (JsonArray)array.DeepClone() : [];

    private static Dictionary<string, double> DoubleMap(JsonNode? node)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        if (node is not JsonObject map)
        {
            return result;
        }

        foreach (var pair in map)
        {
            if (pair.Value is JsonValue value && value.TryGetValue<double>(out var number))
            {
                result[pair.Key] = number;
            }
        }

        return result;
    }

    private static JsonObject DoubleMap(IReadOnlyDictionary<string, double> values) =>
        new(values.Select(static pair =>
            KeyValuePair.Create<string, JsonNode?>(pair.Key, JsonValue.Create(pair.Value))));

    private static int LegacyFontWeight(JsonObject source)
    {
        if (source["fontWeight"] is JsonValue value && value.TryGetValue<int>(out var numeric))
        {
            return Math.Clamp(numeric, 100, 999);
        }

        return FontWeightName(String(source, "fontWeight"));
    }

    private static int FontWeightName(string? name) => name?.ToLowerInvariant() switch
    {
        "thin" => 100,
        "extralight" => 200,
        "light" => 300,
        "semilight" => 350,
        "medium" => 500,
        "semibold" => 600,
        "bold" => 700,
        "extrabold" => 800,
        "black" => 900,
        _ => 400,
    };

    private static SettingsOrigin Origin(JsonObject source, SettingsOrigin fallback) =>
        Enum.TryParse<SettingsOrigin>(String(source, OriginKey), out var origin) ? origin : fallback;

    private static JsonObject CleanSourceDocument(JsonObject source)
    {
        var result = (JsonObject)source.DeepClone();
        result.Remove(OriginKey);
        result.Remove(SourceKey);
        return result;
    }

    private static IEnumerable<JsonObject> OrderProfiles(JsonArray profiles, JsonArray? userProfiles)
    {
        var yielded = new HashSet<JsonObject>(ReferenceEqualityComparer.Instance);
        if (userProfiles is not null)
        {
            foreach (var userProfile in userProfiles.OfType<JsonObject>())
            {
                var match = profiles.OfType<JsonObject>().FirstOrDefault(
                    profile => ProfilesMatch(profile, userProfile));
                if (match is not null && yielded.Add(match))
                {
                    yield return match;
                }
            }
        }

        foreach (var profile in profiles.OfType<JsonObject>())
        {
            if (yielded.Add(profile))
            {
                yield return profile;
            }
        }
    }

    private static void MigrateLegacyAliases(JsonObject root)
    {
        Alias(root, "inputServiceWarning", "warning.inputService");
        Alias(root, "largePasteWarning", "warning.largePaste");
        Alias(root, "multiLinePasteWarning", "warning.multiLinePaste");

        if (!root.ContainsKey("tabSwitcherMode") &&
            root["useTabSwitcher"] is JsonValue switcher &&
            switcher.TryGetValue<bool>(out var useSwitcher))
        {
            root["tabSwitcherMode"] = useSwitcher ? "mostRecentlyUsed" : "disabled";
        }

        if (!root.ContainsKey("warning.confirmOnClose") &&
            root["confirmCloseAllTabs"] is JsonValue close &&
            close.TryGetValue<bool>(out var confirmClose))
        {
            root["warning.confirmOnClose"] = confirmClose ? "automatic" : "never";
        }

        if (!root.ContainsKey("firstWindowPreference") &&
            root["persistedWindowLayout"] is JsonValue persisted &&
            persisted.TryGetValue<bool>(out var usePersisted) &&
            usePersisted)
        {
            root["firstWindowPreference"] = "persistedLayoutAndContent";
        }

        var profiles = NormalizeProfiles(root["profiles"]);
        root["profiles"] = profiles;
        var defaults = profiles["defaults"] as JsonObject ?? new JsonObject();
        profiles["defaults"] = defaults;
        MigrateProfileAliases(defaults);
        if (root.TryGetPropertyValue("compatibility.reloadEnvironmentVariables", out var reload) &&
            !defaults.ContainsKey("compatibility.reloadEnvironmentVariables"))
        {
            defaults["compatibility.reloadEnvironmentVariables"] = reload?.DeepClone();
        }
        if (root.TryGetPropertyValue("experimental.input.forceVT", out var forceVt) &&
            !defaults.ContainsKey("compatibility.input.forceVT"))
        {
            defaults["compatibility.input.forceVT"] = forceVt?.DeepClone();
        }

        if (profiles["list"] is JsonArray list)
        {
            foreach (var profile in list.OfType<JsonObject>())
            {
                MigrateProfileAliases(profile);
            }
        }

        if (root["updates"] is JsonArray updates)
        {
            foreach (var update in updates.OfType<JsonObject>())
            {
                MigrateProfileAliases(update);
            }
        }
    }

    private static void MigrateProfileAliases(JsonObject profile)
    {
        Alias(profile, "experimental.autoMarkPrompts", "autoMarkPrompts");
        Alias(profile, "experimental.showMarksOnScrollbar", "showMarksOnScrollbar");
        Alias(profile, "experimental.rightClickContextMenu", "rightClickContextMenu");
        Alias(profile, "experimental.input.forceVT", "compatibility.input.forceVT");

        // A modern font object suppresses legacy keys declared in the same
        // layer, while still allowing missing properties to inherit legacy
        // values from lower-precedence layers.
        if (profile["font"] is JsonObject)
        {
            profile.Remove("fontFace");
            profile.Remove("fontSize");
            profile.Remove("fontWeight");
        }
    }

    private static void Alias(JsonObject root, string legacyKey, string modernKey)
    {
        if (!root.ContainsKey(modernKey) && root.TryGetPropertyValue(legacyKey, out var value))
        {
            root[modernKey] = value?.DeepClone();
        }
    }

    private static void HandleUserSchemeCollisions(
        JsonObject merged,
        JsonObject user,
        ICollection<SettingsDiagnostic> diagnostics)
    {
        if (merged["schemes"] is not JsonArray inherited ||
            user["schemes"] is not JsonArray userSchemes)
        {
            return;
        }

        for (var index = userSchemes.Count - 1; index >= 0; index--)
        {
            if (userSchemes[index] is not JsonObject candidate)
            {
                continue;
            }

            var name = String(candidate, "name");
            var collision = FindNamed(inherited, name);
            if (collision is null)
            {
                continue;
            }

            if (SchemesEquivalent(collision, candidate))
            {
                userSchemes.RemoveAt(index);
                continue;
            }

            var renamed = UniqueModifiedSchemeName(name ?? "Unnamed", inherited, userSchemes);
            var replacement = (JsonObject)collision.DeepClone();
            MergeObject(replacement, candidate);
            replacement["name"] = renamed;
            userSchemes[index] = replacement;
            RetargetColorSchemeReferences(merged, name, renamed);
            RetargetColorSchemeReferences(user, name, renamed);
            diagnostics.Add(new SettingsDiagnostic(
                SettingsDiagnosticSeverity.Warning,
                "ColorSchemeRenamed",
                $"User color scheme '{name}' conflicts with a built-in scheme and was renamed to '{renamed}'."));
        }
    }

    private static bool SchemesEquivalent(JsonObject inherited, JsonObject candidate)
    {
        foreach (var key in SchemeColorKeys)
        {
            if (!candidate.TryGetPropertyValue(key, out var candidateValue) ||
                !JsonNode.DeepEquals(candidateValue, inherited[key]))
            {
                return false;
            }
        }

        return true;
    }

    private static string UniqueModifiedSchemeName(
        string name,
        JsonArray inherited,
        JsonArray userSchemes)
    {
        for (var suffix = 1; ; suffix++)
        {
            var candidate = suffix == 1 ? $"{name} (modified)" : $"{name} (modified {suffix})";
            if (FindNamed(inherited, candidate) is null && FindNamed(userSchemes, candidate) is null)
            {
                return candidate;
            }
        }
    }

    private static void RetargetColorSchemeReferences(JsonNode node, string? oldName, string newName)
    {
        if (oldName is null)
        {
            return;
        }

        if (node is JsonObject obj)
        {
            foreach (var pair in obj.ToArray())
            {
                if (pair.Key == "colorScheme" && pair.Value is not null)
                {
                    RetargetColorSchemeNode(obj, pair.Key, pair.Value, oldName, newName);
                }
                else if (pair.Value is not null)
                {
                    RetargetColorSchemeReferences(pair.Value, oldName, newName);
                }
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is not null)
                {
                    RetargetColorSchemeReferences(item, oldName, newName);
                }
            }
        }
    }

    private static void RetargetColorSchemeNode(
        JsonObject parent,
        string key,
        JsonNode value,
        string oldName,
        string newName)
    {
        if (value is JsonValue scalar &&
            scalar.TryGetValue<string>(out var current) &&
            string.Equals(current, oldName, StringComparison.OrdinalIgnoreCase))
        {
            parent[key] = newName;
            return;
        }

        if (value is not JsonObject pair)
        {
            return;
        }

        foreach (var channel in new[] { "dark", "light" })
        {
            if (pair[channel] is JsonValue channelValue &&
                channelValue.TryGetValue<string>(out var channelName) &&
                string.Equals(channelName, oldName, StringComparison.OrdinalIgnoreCase))
            {
                pair[channel] = newName;
            }
        }
    }

    private static readonly string[] SchemeColorKeys =
    [
        "foreground", "background", "cursorColor", "selectionBackground",
        "black", "red", "green", "yellow", "blue", "purple", "cyan", "white",
        "brightBlack", "brightRed", "brightGreen", "brightYellow", "brightBlue",
        "brightPurple", "brightCyan", "brightWhite",
    ];

    private static string LaunchModeString(LaunchMode value) => value switch
    {
        LaunchMode.Maximized => "maximized",
        LaunchMode.Fullscreen => "fullscreen",
        LaunchMode.Focus => "focus",
        LaunchMode.MaximizedFocus => "maximizedFocus",
        _ => "default",
    };

    private static string TabWidthModeString(TabWidthMode value) => value switch
    {
        TabWidthMode.TitleLength => "titleLength",
        TabWidthMode.Compact => "compact",
        _ => "equal",
    };

    private static string ConfirmOnCloseString(ConfirmOnClose value) => value switch
    {
        ConfirmOnClose.Never => "never",
        ConfirmOnClose.Always => "always",
        _ => "automatic",
    };

    private static string CloseOnExitString(CloseOnExitMode value) => value switch
    {
        CloseOnExitMode.Never => "never",
        CloseOnExitMode.Graceful => "graceful",
        CloseOnExitMode.Always => "always",
        _ => "automatic",
    };

    private static string ColorSchemeName(JsonObject profile)
    {
        if (profile["colorScheme"] is JsonValue)
        {
            return String(profile, "colorScheme") ?? "Campbell";
        }

        if (profile["colorScheme"] is JsonObject pair)
        {
            return String(pair, "dark") ?? String(pair, "light") ?? "Campbell";
        }

        return "Campbell";
    }

    private static int AcrylicOpacity(JsonObject profile)
    {
        var value = Double(profile, "acrylicOpacity", 1, minimum: 0, maximum: 1);
        return (int)Math.Round(value * 100);
    }

    private static int FontWeight(JsonObject? font)
    {
        if (font?["weight"] is JsonValue value && value.TryGetValue<int>(out var numeric))
        {
            return Math.Clamp(numeric, 100, 999);
        }

        return String(font, "weight")?.ToLowerInvariant() switch
        {
            "thin" => 100,
            "extralight" => 200,
            "light" => 300,
            "semilight" => 350,
            "medium" => 500,
            "semibold" => 600,
            "bold" => 700,
            "extrabold" => 800,
            "black" => 900,
            _ => 400,
        };
    }

    private static string Padding(JsonObject profile)
    {
        if (profile["padding"] is JsonValue value && value.TryGetValue<int>(out var numeric))
        {
            return numeric.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return String(profile, "padding") ?? "8";
    }

    private static bool CopyFormatting(JsonObject root)
    {
        if (root["copyFormatting"] is JsonValue value && value.TryGetValue<bool>(out var boolean))
        {
            return boolean;
        }

        return root["copyFormatting"] is not null;
    }

    private static LaunchMode LaunchModeValue(JsonObject root, string key) =>
        String(root, key)?.ToLowerInvariant() switch
        {
            "maximized" => LaunchMode.Maximized,
            "fullscreen" => LaunchMode.Fullscreen,
            "focus" => LaunchMode.Focus,
            "maximizedfocus" => LaunchMode.MaximizedFocus,
            _ => LaunchMode.Default,
        };

    private static TabWidthMode TabWidthModeValue(JsonObject root, string key) =>
        String(root, key)?.ToLowerInvariant() switch
        {
            "titlelength" => TabWidthMode.TitleLength,
            "compact" => TabWidthMode.Compact,
            _ => TabWidthMode.Equal,
        };

    private static ConfirmOnClose ConfirmOnCloseValue(JsonObject root, string key) =>
        String(root, key)?.ToLowerInvariant() switch
        {
            "never" => ConfirmOnClose.Never,
            "always" => ConfirmOnClose.Always,
            _ => ConfirmOnClose.Automatic,
        };

    private static CloseOnExitMode CloseOnExitValue(JsonObject root)
    {
        if (root["closeOnExit"] is JsonValue value && value.TryGetValue<bool>(out var boolean))
        {
            return boolean ? CloseOnExitMode.Graceful : CloseOnExitMode.Never;
        }

        return String(root, "closeOnExit")?.ToLowerInvariant() switch
        {
            "never" => CloseOnExitMode.Never,
            "graceful" => CloseOnExitMode.Graceful,
            "always" => CloseOnExitMode.Always,
            _ => CloseOnExitMode.Automatic,
        };
    }

    private static string? String(JsonObject? root, string key) =>
        root?[key] is JsonNode node ? StringValue(node) : null;

    private static string? StringValue(JsonNode node) =>
        node is JsonValue value && value.TryGetValue<string>(out var result) ? result : null;

    private static bool Bool(JsonObject? root, string key, bool fallback = false) =>
        root?[key] is JsonValue value && value.TryGetValue<bool>(out var result) ? result : fallback;

    private static bool? NullableBool(JsonObject? root, string key) =>
        root?[key] is JsonValue value && value.TryGetValue<bool>(out var result) ? result : null;

    private static int Int(
        JsonObject? root,
        string key,
        int fallback,
        int minimum = int.MinValue,
        int maximum = int.MaxValue)
    {
        var result = root?[key] is JsonValue value && value.TryGetValue<int>(out var number)
            ? number
            : fallback;
        return Math.Clamp(result, minimum, maximum);
    }

    private static double Double(
        JsonObject? root,
        string key,
        double fallback,
        double minimum = double.MinValue,
        double maximum = double.MaxValue)
    {
        var result = root?[key] is JsonValue value && value.TryGetValue<double>(out var number)
            ? number
            : fallback;
        return Math.Clamp(result, minimum, maximum);
    }
}
