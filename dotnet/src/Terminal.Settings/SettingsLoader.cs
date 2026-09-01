using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Microsoft.Terminal.Settings;

public enum SettingsLayerKind
{
    Defaults,
    Fragment,
    User,
}

public sealed record SettingsLayer(string Source, string Json, SettingsLayerKind Kind);

public static class SettingsLoader
{
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
        var merged = (JsonObject)defaults.DeepClone();

        if (fragments is not null)
        {
            foreach (var fragment in fragments)
            {
                var fragmentObject = ParseObject(fragment.Json, fragment.Source, required: false, diagnostics);
                if (fragmentObject is not null)
                {
                    if (fragment.Kind == SettingsLayerKind.Fragment)
                    {
                        PrepareFragment(fragmentObject, FragmentProvider(fragment.Source));
                        pendingFragmentUpdates.AddRange(ExtractFragmentUpdates(fragmentObject));
                    }

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
                MergeRoot(merged, userDocument);
            }
        }

        ApplyFragmentUpdates(merged, pendingFragmentUpdates);
        var settings = Resolve(merged, userDocument);
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

        return document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;
    }

    private static AppSettings Resolve(JsonObject root, JsonObject? userDocument)
    {
        var settings = new AppSettings
        {
            DefaultProfile = String(root, "defaultProfile"),
            InitialCols = Int(root, "initialCols", 120, minimum: 1),
            InitialRows = Int(root, "initialRows", 30, minimum: 1),
            LaunchMode = LaunchModeValue(root, "launchMode"),
            AlwaysOnTop = Bool(root, "alwaysOnTop"),
            AlwaysShowTabs = Bool(root, "alwaysShowTabs", true),
            ShowTabsInTitlebar = Bool(root, "showTabsInTitlebar", true),
            ShowTerminalTitleInTitlebar = Bool(root, "showTerminalTitleInTitlebar", true),
            ShowTabsFullscreen = Bool(root, "showTabsFullscreen"),
            CopyOnSelect = Bool(root, "copyOnSelect"),
            CopyFormatting = CopyFormatting(root),
            TrimBlockSelection = Bool(root, "trimBlockSelection", true),
            TrimPaste = Bool(root, "trimPaste", true),
            FocusFollowMouse = Bool(root, "focusFollowMouse"),
            SnapToGridOnResize = Bool(root, "snapToGridOnResize", true),
            DisableAnimations = Bool(root, "disableAnimations"),
            MinimizeToNotificationArea = Bool(root, "minimizeToNotificationArea"),
            AlwaysShowNotificationIcon = Bool(root, "alwaysShowNotificationIcon"),
            ShowAdminShield = Bool(root, "showAdminShield", true),
            Theme = String(root, "theme") ?? "dark",
            StartupActions = String(root, "startupActions") ?? string.Empty,
            WordDelimiters = String(root, "wordDelimiters") ?? " /\\()\"'-.,:;<>~!@#$%^&*|+=[]{}~?\u2502",
            TabWidthMode = TabWidthModeValue(root, "tabWidthMode"),
            ConfirmOnClose = ConfirmOnCloseValue(root, "warning.confirmOnClose"),
        };

        var profilesNode = NormalizeProfiles(root["profiles"]);
        var userProfilesNode = NormalizeProfiles(userDocument?["profiles"]);
        var defaultsNode = profilesNode["defaults"] as JsonObject ?? new JsonObject();
        settings.ProfileDefaults = ResolveProfile(defaultsNode, generateGuid: false);
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
            foreach (var profileNode in profiles.OfType<JsonObject>())
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
                if (profile.Guid is not null && !profileIds.Add(profile.Guid))
                {
                    settings.Diagnostics.Add(new SettingsDiagnostic(
                        SettingsDiagnosticSeverity.Warning,
                        "DuplicateProfile",
                        $"A duplicate profile with GUID '{profile.Guid}' was ignored."));
                    continue;
                }

                profile.SourceDocument = (JsonObject)profileNode.DeepClone();
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
            ColorScheme = ColorSchemeName(profile),
            FontFace = String(font, "face") ?? String(profile, "fontFace") ?? "Cascadia Mono",
            FontSize = Double(font, "size", Double(profile, "fontSize", 12), minimum: 1),
            FontWeight = FontWeight(font),
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
            SnapOnInput = Bool(profile, "snapOnInput", true),
            AltGrAliasing = Bool(profile, "altGrAliasing", true),
            Elevate = Bool(profile, "elevate"),
            AutoMarkPrompts = Bool(profile, "autoMarkPrompts", true),
            ShowMarksOnScrollbar = Bool(profile, "showMarksOnScrollbar"),
            ReloadEnvironmentVariables = Bool(profile, "compatibility.reloadEnvironmentVariables", true),
            AllowKittyKeyboardMode = Bool(profile, "compatibility.kittyKeyboardMode", true),
            AllowVtClipboardWrite = Bool(profile, "compatibility.allowOSC52", true),
            AllowOscNotifications = Bool(profile, "compatibility.allowOSC777"),
            Environment = StringMap(profile["environment"]),
        };
    }

    private static SchemeSettings ResolveScheme(JsonObject scheme) => new()
    {
        Name = String(scheme, "name") ?? "Unnamed scheme",
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
        SourceDocument = (JsonObject)scheme.DeepClone(),
    };

    private static ThemeSettings ResolveTheme(JsonObject theme)
    {
        var window = theme["window"] as JsonObject;
        var tabRow = theme["tabRow"] as JsonObject;
        return new ThemeSettings
        {
            Name = String(theme, "name") ?? "unnamed",
            WindowApplicationTheme = String(window, "applicationTheme"),
            UseMica = NullableBool(window, "useMica"),
            TabRowBackground = String(tabRow, "background"),
            SourceDocument = (JsonObject)theme.DeepClone(),
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
            if (!schemeNames.Contains(profile.ColorScheme))
            {
                settings.Diagnostics.Add(new SettingsDiagnostic(
                    SettingsDiagnosticSeverity.Warning,
                    "UnknownColorScheme",
                    $"Profile '{profile.Name}' references unknown color scheme '{profile.ColorScheme}'."));
                profile.ColorScheme = "Campbell";
            }
        }
    }

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
            else
            {
                MergeObject(existing, item);
            }
        }
    }

    private static void MergeObject(JsonObject target, JsonObject layer)
    {
        foreach (var pair in layer)
        {
            if (pair.Value is JsonObject layerObject && target[pair.Key] is JsonObject targetObject)
            {
                MergeObject(targetObject, layerObject);
            }
            else
            {
                target[pair.Key] = pair.Value?.DeepClone();
            }
        }
    }

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
        ["defaultProfile"] = settings.DefaultProfile,
        ["initialCols"] = settings.InitialCols,
        ["initialRows"] = settings.InitialRows,
        ["launchMode"] = LaunchModeString(settings.LaunchMode),
        ["alwaysOnTop"] = settings.AlwaysOnTop,
        ["alwaysShowTabs"] = settings.AlwaysShowTabs,
        ["showTabsInTitlebar"] = settings.ShowTabsInTitlebar,
        ["showTerminalTitleInTitlebar"] = settings.ShowTerminalTitleInTitlebar,
        ["showTabsFullscreen"] = settings.ShowTabsFullscreen,
        ["copyOnSelect"] = settings.CopyOnSelect,
        ["copyFormatting"] = settings.CopyFormatting,
        ["trimBlockSelection"] = settings.TrimBlockSelection,
        ["trimPaste"] = settings.TrimPaste,
        ["focusFollowMouse"] = settings.FocusFollowMouse,
        ["snapToGridOnResize"] = settings.SnapToGridOnResize,
        ["disableAnimations"] = settings.DisableAnimations,
        ["minimizeToNotificationArea"] = settings.MinimizeToNotificationArea,
        ["alwaysShowNotificationIcon"] = settings.AlwaysShowNotificationIcon,
        ["showAdminShield"] = settings.ShowAdminShield,
        ["theme"] = settings.Theme,
        ["startupActions"] = settings.StartupActions,
        ["wordDelimiters"] = settings.WordDelimiters,
        ["tabWidthMode"] = TabWidthModeString(settings.TabWidthMode),
        ["warning.confirmOnClose"] = ConfirmOnCloseString(settings.ConfirmOnClose),
        ["profiles"] = SerializeProfiles(settings),
        ["schemes"] = new JsonArray(settings.Schemes.Select(SerializeScheme).ToArray()),
        ["themes"] = new JsonArray(settings.Themes.Select(SerializeTheme).ToArray()),
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
        result["colorScheme"] = profile.ColorScheme;
        result["font"] = new JsonObject
        {
            ["face"] = profile.FontFace,
            ["size"] = profile.FontSize,
            ["weight"] = profile.FontWeight,
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
        result["snapOnInput"] = profile.SnapOnInput;
        result["altGrAliasing"] = profile.AltGrAliasing;
        result["elevate"] = profile.Elevate;
        result["autoMarkPrompts"] = profile.AutoMarkPrompts;
        result["showMarksOnScrollbar"] = profile.ShowMarksOnScrollbar;
        result["compatibility.reloadEnvironmentVariables"] = profile.ReloadEnvironmentVariables;
        result["compatibility.kittyKeyboardMode"] = profile.AllowKittyKeyboardMode;
        result["compatibility.allowOSC52"] = profile.AllowVtClipboardWrite;
        result["compatibility.allowOSC777"] = profile.AllowOscNotifications;
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

        var window = result["window"] as JsonObject ?? new JsonObject();
        result["window"] = window;
        window["applicationTheme"] = theme.WindowApplicationTheme;
        window["useMica"] = theme.UseMica;

        var tabRow = result["tabRow"] as JsonObject ?? new JsonObject();
        result["tabRow"] = tabRow;
        tabRow["background"] = theme.TabRowBackground;
        return result;
    }

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
