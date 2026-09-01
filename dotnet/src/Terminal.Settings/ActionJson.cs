using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Microsoft.Terminal.Settings;

[JsonConverter(typeof(ActionAndArgsJsonConverter))]
public sealed record ActionAndArgs
{
    public ActionAndArgs(ShortcutAction action, IActionArgs? args = null, string? unknownActionName = null)
    {
        Action = action;
        Args = args;
        UnknownActionName = unknownActionName;
    }

    public ShortcutAction Action { get; }
    public IActionArgs? Args { get; }
    public string? UnknownActionName { get; }
    public bool IsUnknown => Args is UnknownActionArgs;

    [JsonIgnore]
    public string ActionName =>
        IsUnknown
            ? UnknownActionName ?? (Args as UnknownActionArgs)?.ActionName ?? string.Empty
            : ActionCatalog.GetJsonName(Action);

    public string GenerateId()
    {
        if (Action is ShortcutAction.Invalid && !IsUnknown)
        {
            return string.Empty;
        }

        var result = $"User.{ActionName}";
        if (Args is not null)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(ActionJson.Serialize(this)));
            result += $".{Convert.ToHexString(bytes.AsSpan(0, 4))}";
        }

        return result;
    }

    public string GenerateName()
    {
        var baseName = Humanize(IsUnknown ? ActionName : Action.ToString());
        return Args switch
        {
            SendInputArgs { Input.Length: > 0 } value => $"{baseName}: {value.Input}",
            SetColorSchemeArgs { SchemeName.Length: > 0 } value => $"{baseName}: {value.SchemeName}",
            RenameTabArgs { Title.Length: > 0 } value => $"{baseName}: {value.Title}",
            RenameWindowArgs { Name.Length: > 0 } value => $"{baseName}: {value.Name}",
            OpenWorkspaceArgs { Name.Length: > 0 } value => $"{baseName}: {value.Name}",
            ExecuteCommandlineArgs { Commandline.Length: > 0 } value => $"{baseName}: {value.Commandline}",
            _ => baseName,
        };
    }

    private static string Humanize(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "Unknown action";
        }

        var builder = new StringBuilder(value.Length + 8);
        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];
            if (index > 0 && char.IsUpper(current) && !char.IsUpper(value[index - 1]))
            {
                builder.Append(' ');
            }

            builder.Append(index == 0 ? char.ToUpperInvariant(current) : current);
        }

        return builder.ToString();
    }
}

public static class ActionJson
{
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = 128,
    };

    public static ActionAndArgs Parse(string json)
    {
        using var document = JsonDocument.Parse(json, DocumentOptions);
        return Parse(document.RootElement);
    }

    public static ActionAndArgs Parse(JsonNode? node)
    {
        if (node is null)
        {
            return new(ShortcutAction.Invalid);
        }

        using var document = JsonDocument.Parse(node.ToJsonString(), DocumentOptions);
        return Parse(document.RootElement);
    }

    public static ActionAndArgs Parse(JsonElement json)
    {
        string? actionName = null;
        if (json.ValueKind == JsonValueKind.String)
        {
            actionName = json.GetString();
        }
        else if (json.ValueKind == JsonValueKind.Object)
        {
            actionName = String(json, "action");
        }

        if (string.IsNullOrEmpty(actionName) || string.Equals(actionName, "unbound", StringComparison.Ordinal))
        {
            return new(ShortcutAction.Invalid);
        }

        if (!ActionCatalog.TryGet(actionName, out var definition))
        {
            var raw = JsonNode.Parse(json.GetRawText()) as JsonObject
                ?? new JsonObject { ["action"] = actionName };
            return new(ShortcutAction.Invalid, new UnknownActionArgs(actionName, raw), actionName);
        }

        var args = definition.Action == ShortcutAction.QuakeMode
            ? new GlobalSummonArgs(Name: "_quake", DropdownDuration: 200)
            : definition.HasArguments ? ParseArgs(definition.Action, json) : null;
        return new(definition.Action, args);
    }

    public static string Serialize(ActionAndArgs value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            Write(writer, value);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    internal static void Write(Utf8JsonWriter writer, ActionAndArgs value)
    {
        if (value.IsUnknown && value.Args is UnknownActionArgs unknown)
        {
            unknown.Raw.WriteTo(writer);
            return;
        }

        if (value.Action == ShortcutAction.Invalid)
        {
            writer.WriteStringValue("unbound");
            return;
        }

        if (value.Args is null)
        {
            writer.WriteStringValue(value.ActionName);
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("action", value.ActionName);
        if (value.Action != ShortcutAction.QuakeMode)
        {
            WriteArgs(writer, value.Args);
        }
        writer.WriteEndObject();
    }

    private static IActionArgs ParseArgs(ShortcutAction action, JsonElement json) => action switch
    {
        ShortcutAction.AdjustFontSize => new AdjustFontSizeArgs(Float(json, "delta")),
        ShortcutAction.CloseOtherTabs => new CloseOtherTabsArgs(NullableUInt(json, "index")),
        ShortcutAction.CloseTabsAfter => new CloseTabsAfterArgs(NullableUInt(json, "index")),
        ShortcutAction.CloseTab => new CloseTabArgs(NullableUInt(json, "index")),
        ShortcutAction.CopyText => new CopyTextArgs(
            Bool(json, "dismissSelection", true),
            Bool(json, "singleLine"),
            Bool(json, "withControlSequences"),
            NullableCopyFormat(json, "copyFormatting")),
        ShortcutAction.ExecuteCommandline => new ExecuteCommandlineArgs(String(json, "commandline") ?? ""),
        ShortcutAction.FindMatch => new FindMatchArgs(
            Enum(json, "direction", FindMatchDirection.None, ("next", FindMatchDirection.Next), ("prev", FindMatchDirection.Previous))),
        ShortcutAction.SearchForText => new SearchForTextArgs(String(json, "queryUrl") ?? ""),
        ShortcutAction.GlobalSummon => new GlobalSummonArgs(
            String(json, "name") ?? "",
            Enum(json, "desktop", DesktopBehavior.ToCurrent, ("any", DesktopBehavior.Any), ("toCurrent", DesktopBehavior.ToCurrent), ("onCurrent", DesktopBehavior.OnCurrent)),
            Enum(json, "monitor", MonitorBehavior.ToMouse, ("any", MonitorBehavior.Any), ("toCurrent", MonitorBehavior.ToCurrent), ("toMouse", MonitorBehavior.ToMouse)),
            Bool(json, "toggleVisibility", true),
            UInt(json, "dropdownDuration")),
        ShortcutAction.MoveFocus => new MoveFocusArgs(FocusDirection(json)),
        ShortcutAction.MovePane => new MovePaneArgs(UInt(json, "index"), String(json, "window") ?? ""),
        ShortcutAction.SwapPane => new SwapPaneArgs(FocusDirection(json)),
        ShortcutAction.MoveTab => new MoveTabArgs(
            String(json, "window") ?? "",
            Enum(json, "direction", MoveTabDirection.None, ("forward", MoveTabDirection.Forward), ("backward", MoveTabDirection.Backward))),
        ShortcutAction.NewTab => new NewTabArgs(ContentArgs(json)),
        ShortcutAction.NewWindow => new NewWindowArgs(ContentArgs(json)),
        ShortcutAction.NextTab => new NextTabArgs(NullableTabSwitcherMode(json, "tabSwitcherMode")),
        ShortcutAction.OpenSettings => new OpenSettingsArgs(
            Enum(json, "target", SettingsTarget.SettingsFile,
                ("settingsFile", SettingsTarget.SettingsFile), ("defaultsFile", SettingsTarget.DefaultsFile),
                ("allFiles", SettingsTarget.AllFiles), ("settingsUI", SettingsTarget.SettingsUI), ("directory", SettingsTarget.Directory))),
        ShortcutAction.SetFocusMode => new SetFocusModeArgs(Bool(json, "isFocusMode")),
        ShortcutAction.SetFullScreen => new SetFullScreenArgs(Bool(json, "isFullScreen")),
        ShortcutAction.SetMaximized => new SetMaximizedArgs(Bool(json, "isMaximized")),
        ShortcutAction.PrevTab => new PrevTabArgs(NullableTabSwitcherMode(json, "tabSwitcherMode")),
        ShortcutAction.RenameTab => new RenameTabArgs(String(json, "title") ?? ""),
        ShortcutAction.RenameWindow => new RenameWindowArgs(String(json, "name") ?? ""),
        ShortcutAction.ResizePane => new ResizePaneArgs(ResizeDirection(json)),
        ShortcutAction.ScrollDown => new ScrollDownArgs(NullableUInt(json, "rowsToScroll")),
        ShortcutAction.ScrollUp => new ScrollUpArgs(NullableUInt(json, "rowsToScroll")),
        ShortcutAction.ScrollToMark => new ScrollToMarkArgs(
            Enum(json, "direction", ScrollToMarkDirection.Previous,
                ("previous", ScrollToMarkDirection.Previous), ("next", ScrollToMarkDirection.Next),
                ("first", ScrollToMarkDirection.First), ("last", ScrollToMarkDirection.Last))),
        ShortcutAction.AddMark => new AddMarkArgs(String(json, "color")),
        ShortcutAction.SendInput => new SendInputArgs(String(json, "input") ?? ""),
        ShortcutAction.SetColorScheme => new SetColorSchemeArgs(String(json, "colorScheme") ?? ""),
        ShortcutAction.SetTabColor => new SetTabColorArgs(String(json, "color")),
        ShortcutAction.SplitPane => new SplitPaneArgs(
            SplitDirection(json),
            Enum(json, "splitMode", SplitType.Manual, ("manual", SplitType.Manual), ("duplicate", SplitType.Duplicate)),
            Float(json, "size", 0.5f),
            ContentArgs(json)),
        ShortcutAction.SwitchToTab => new SwitchToTabArgs(UInt(json, "index")),
        ShortcutAction.ToggleCommandPalette => new ToggleCommandPaletteArgs(
            Enum(json, "launchMode", CommandPaletteLaunchMode.Action,
                ("action", CommandPaletteLaunchMode.Action), ("commandLine", CommandPaletteLaunchMode.CommandLine))),
        ShortcutAction.FocusPane => new FocusPaneArgs(UInt(json, "id")),
        ShortcutAction.ExportBuffer => new ExportBufferArgs(String(json, "path") ?? ""),
        ShortcutAction.ClearBuffer => new ClearBufferArgs(
            Enum(json, "clear", ClearBufferType.All,
                ("screen", ClearBufferType.Screen), ("scrollback", ClearBufferType.Scrollback), ("all", ClearBufferType.All))),
        ShortcutAction.MultipleActions => new MultipleActionsArgs(Actions(json)),
        ShortcutAction.AdjustOpacity => new AdjustOpacityArgs(Int(json, "opacity"), Bool(json, "relative", true)),
        ShortcutAction.Suggestions => new SuggestionsArgs(Suggestions(json, "source"), Bool(json, "useCommandline")),
        ShortcutAction.SelectCommand => new SelectCommandArgs(SelectDirection(json)),
        ShortcutAction.SelectOutput => new SelectOutputArgs(SelectDirection(json)),
        ShortcutAction.ColorSelection => new ColorSelectionArgs(
            SelectionColor(json, "foreground"),
            SelectionColor(json, "background"),
            Enum(json, "matchMode", MatchMode.None, ("none", MatchMode.None), ("all", MatchMode.All))),
        ShortcutAction.OpenWorkspace => new OpenWorkspaceArgs(String(json, "name") ?? ""),
        _ => throw new InvalidOperationException($"No argument converter is registered for '{action}'."),
    };

    private static void WriteArgs(Utf8JsonWriter writer, IActionArgs args)
    {
        switch (args)
        {
            case AdjustFontSizeArgs value: Number(writer, "delta", value.Delta); break;
            case CloseOtherTabsArgs value: Number(writer, "index", value.Index); break;
            case CloseTabsAfterArgs value: Number(writer, "index", value.Index); break;
            case CloseTabArgs value: Number(writer, "index", value.Index); break;
            case CopyTextArgs value:
                writer.WriteBoolean("dismissSelection", value.DismissSelection);
                writer.WriteBoolean("singleLine", value.SingleLine);
                writer.WriteBoolean("withControlSequences", value.WithControlSequences);
                if (value.CopyFormatting is { } copyFormat) writer.WriteString("copyFormatting", CopyFormatString(copyFormat));
                break;
            case ExecuteCommandlineArgs value: writer.WriteString("commandline", value.Commandline); break;
            case FindMatchArgs value: writer.WriteString("direction", value.Direction == FindMatchDirection.Previous ? "prev" : Lower(value.Direction)); break;
            case SearchForTextArgs value: writer.WriteString("queryUrl", value.QueryUrl); break;
            case GlobalSummonArgs value:
                writer.WriteString("name", value.Name);
                writer.WriteString("desktop", Lower(value.Desktop));
                writer.WriteString("monitor", Lower(value.Monitor));
                writer.WriteBoolean("toggleVisibility", value.ToggleVisibility);
                writer.WriteNumber("dropdownDuration", value.DropdownDuration);
                break;
            case MoveFocusArgs value: writer.WriteString("direction", Lower(value.FocusDirection)); break;
            case MovePaneArgs value: writer.WriteNumber("index", value.TabIndex); writer.WriteString("window", value.Window); break;
            case SwapPaneArgs value: writer.WriteString("direction", Lower(value.Direction)); break;
            case MoveTabArgs value: writer.WriteString("window", value.Window); writer.WriteString("direction", Lower(value.Direction)); break;
            case NewTabArgs value: WriteContentArgs(writer, value.ContentArgs); break;
            case NewWindowArgs value: WriteContentArgs(writer, value.ContentArgs); break;
            case NextTabArgs value: Enum(writer, "tabSwitcherMode", value.SwitcherMode, TabSwitcherModeString); break;
            case OpenSettingsArgs value: writer.WriteString("target", Lower(value.Target)); break;
            case SetFocusModeArgs value: writer.WriteBoolean("isFocusMode", value.IsFocusMode); break;
            case SetFullScreenArgs value: writer.WriteBoolean("isFullScreen", value.IsFullScreen); break;
            case SetMaximizedArgs value: writer.WriteBoolean("isMaximized", value.IsMaximized); break;
            case PrevTabArgs value: Enum(writer, "tabSwitcherMode", value.SwitcherMode, TabSwitcherModeString); break;
            case RenameTabArgs value: writer.WriteString("title", value.Title); break;
            case RenameWindowArgs value: writer.WriteString("name", value.Name); break;
            case ResizePaneArgs value: writer.WriteString("direction", Lower(value.ResizeDirection)); break;
            case ScrollDownArgs value: Number(writer, "rowsToScroll", value.RowsToScroll); break;
            case ScrollUpArgs value: Number(writer, "rowsToScroll", value.RowsToScroll); break;
            case ScrollToMarkArgs value: writer.WriteString("direction", Lower(value.Direction)); break;
            case AddMarkArgs value: String(writer, "color", value.Color); break;
            case SendInputArgs value: writer.WriteString("input", value.Input); break;
            case SetColorSchemeArgs value: writer.WriteString("colorScheme", value.SchemeName); break;
            case SetTabColorArgs value: String(writer, "color", value.TabColor); break;
            case SplitPaneArgs value:
                writer.WriteString("split", value.SplitDirection == global::Microsoft.Terminal.Settings.SplitDirection.Automatic ? "auto" : Lower(value.SplitDirection));
                writer.WriteString("splitMode", Lower(value.SplitMode));
                writer.WriteNumber("size", value.SplitSize);
                WriteContentArgs(writer, value.ContentArgs);
                break;
            case SwitchToTabArgs value: writer.WriteNumber("index", value.TabIndex); break;
            case ToggleCommandPaletteArgs value: writer.WriteString("launchMode", Lower(value.LaunchMode)); break;
            case FocusPaneArgs value: writer.WriteNumber("id", value.Id); break;
            case ExportBufferArgs value: writer.WriteString("path", value.Path); break;
            case ClearBufferArgs value: writer.WriteString("clear", Lower(value.Clear)); break;
            case MultipleActionsArgs value:
                writer.WritePropertyName("actions");
                writer.WriteStartArray();
                foreach (var action in value.Actions) Write(writer, action);
                writer.WriteEndArray();
                break;
            case AdjustOpacityArgs value: writer.WriteNumber("opacity", value.Opacity); writer.WriteBoolean("relative", value.Relative); break;
            case SuggestionsArgs value: writer.WriteString("source", SuggestionsString(value.Source)); writer.WriteBoolean("useCommandline", value.UseCommandline); break;
            case SelectCommandArgs value: writer.WriteString("direction", value.Direction == SelectOutputDirection.Previous ? "prev" : "next"); break;
            case SelectOutputArgs value: writer.WriteString("direction", value.Direction == SelectOutputDirection.Previous ? "prev" : "next"); break;
            case ColorSelectionArgs value:
                String(writer, "foreground", value.Foreground?.Value);
                String(writer, "background", value.Background?.Value);
                writer.WriteString("matchMode", Lower(value.MatchMode));
                break;
            case OpenWorkspaceArgs value: writer.WriteString("name", value.Name); break;
            default: throw new JsonException($"Unsupported action argument type '{args.GetType().Name}'.");
        }
    }

    private static INewContentArgs ContentArgs(JsonElement json)
    {
        var type = String(json, "type");
        if (!string.IsNullOrEmpty(type))
        {
            return new BaseContentArgs(type);
        }

        return new NewTerminalArgs(
            String(json, "commandline") ?? "",
            String(json, "startingDirectory") ?? "",
            String(json, "tabTitle") ?? "",
            String(json, "tabColor"),
            NullableInt(json, "index"),
            String(json, "profile") ?? "",
            Guid(json, "sessionId"),
            Bool(json, "appendCommandLine"),
            NullableBool(json, "suppressApplicationTitle"),
            String(json, "colorScheme") ?? "",
            NullableBool(json, "elevate"),
            NullableBool(json, "reloadEnvironmentVariables"),
            ULong(json, "__content"));
    }

    private static void WriteContentArgs(Utf8JsonWriter writer, INewContentArgs? content)
    {
        if (content is BaseContentArgs baseContent)
        {
            writer.WriteString("type", baseContent.Type);
        }
        else if (content is NewTerminalArgs terminal)
        {
            writer.WriteString("commandline", terminal.Commandline);
            writer.WriteString("startingDirectory", terminal.StartingDirectory);
            writer.WriteString("tabTitle", terminal.TabTitle);
            String(writer, "tabColor", terminal.TabColor);
            Number(writer, "index", terminal.ProfileIndex);
            writer.WriteString("profile", terminal.Profile);
            if (terminal.SessionId != System.Guid.Empty) writer.WriteString("sessionId", terminal.SessionId);
            writer.WriteBoolean("appendCommandLine", terminal.AppendCommandLine);
            Boolean(writer, "suppressApplicationTitle", terminal.SuppressApplicationTitle);
            writer.WriteString("colorScheme", terminal.ColorScheme);
            Boolean(writer, "elevate", terminal.Elevate);
            Boolean(writer, "reloadEnvironmentVariables", terminal.ReloadEnvironmentVariables);
            if (terminal.ContentId != 0) writer.WriteNumber("__content", terminal.ContentId);
        }
    }

    private static IReadOnlyList<ActionAndArgs> Actions(JsonElement json)
    {
        if (!Property(json, "actions", out var actions) || actions.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return actions.EnumerateArray().Select(Parse).ToArray();
    }

    private static bool Property(JsonElement json, string name, out JsonElement value)
    {
        if (json.ValueKind == JsonValueKind.Object)
        {
            return json.TryGetProperty(name, out value);
        }

        value = default;
        return false;
    }

    private static string? String(JsonElement json, string name) =>
        Property(json, name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Bool(JsonElement json, string name, bool defaultValue = false) =>
        Property(json, name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : defaultValue;

    private static bool? NullableBool(JsonElement json, string name) =>
        Property(json, name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static int Int(JsonElement json, string name, int defaultValue = 0) =>
        Property(json, name, out var value) && value.TryGetInt32(out var result) ? result : defaultValue;

    private static int? NullableInt(JsonElement json, string name) =>
        Property(json, name, out var value) && value.TryGetInt32(out var result) ? result : null;

    private static uint UInt(JsonElement json, string name, uint defaultValue = 0) =>
        Property(json, name, out var value) && value.TryGetUInt32(out var result) ? result : defaultValue;

    private static uint? NullableUInt(JsonElement json, string name) =>
        Property(json, name, out var value) && value.TryGetUInt32(out var result) ? result : null;

    private static ulong ULong(JsonElement json, string name, ulong defaultValue = 0) =>
        Property(json, name, out var value) && value.TryGetUInt64(out var result) ? result : defaultValue;

    private static float Float(JsonElement json, string name, float defaultValue = 0) =>
        Property(json, name, out var value) && value.TryGetSingle(out var result) ? result : defaultValue;

    private static Guid Guid(JsonElement json, string name) =>
        Property(json, name, out var value) && value.ValueKind == JsonValueKind.String && value.TryGetGuid(out var result)
            ? result
            : System.Guid.Empty;

    private static T Enum<T>(JsonElement json, string name, T defaultValue, params (string Name, T Value)[] values)
    {
        var text = String(json, name);
        if (text is not null)
        {
            foreach (var candidate in values)
            {
                if (string.Equals(text, candidate.Name, StringComparison.OrdinalIgnoreCase))
                {
                    return candidate.Value;
                }
            }
        }

        return defaultValue;
    }

    private static FocusDirection FocusDirection(JsonElement json) =>
        Enum(json, "direction", Settings.FocusDirection.None,
            ("left", Settings.FocusDirection.Left), ("right", Settings.FocusDirection.Right),
            ("up", Settings.FocusDirection.Up), ("down", Settings.FocusDirection.Down),
            ("previous", Settings.FocusDirection.Previous), ("previousInOrder", Settings.FocusDirection.PreviousInOrder),
            ("nextInOrder", Settings.FocusDirection.NextInOrder), ("first", Settings.FocusDirection.First),
            ("parent", Settings.FocusDirection.Parent), ("child", Settings.FocusDirection.Child));

    private static ResizeDirection ResizeDirection(JsonElement json) =>
        Enum(json, "direction", Settings.ResizeDirection.None,
            ("left", Settings.ResizeDirection.Left), ("right", Settings.ResizeDirection.Right),
            ("up", Settings.ResizeDirection.Up), ("down", Settings.ResizeDirection.Down));

    private static SplitDirection SplitDirection(JsonElement json) =>
        Enum(json, "split", Settings.SplitDirection.Automatic,
            ("auto", Settings.SplitDirection.Automatic), ("up", Settings.SplitDirection.Up),
            ("right", Settings.SplitDirection.Right), ("vertical", Settings.SplitDirection.Right),
            ("down", Settings.SplitDirection.Down), ("horizontal", Settings.SplitDirection.Down),
            ("left", Settings.SplitDirection.Left));

    private static SelectOutputDirection SelectDirection(JsonElement json) =>
        Enum(json, "direction", SelectOutputDirection.Previous,
            ("prev", SelectOutputDirection.Previous), ("next", SelectOutputDirection.Next));

    private static TabSwitcherMode? NullableTabSwitcherMode(JsonElement json, string name)
    {
        if (!Property(json, name, out var value))
        {
            return null;
        }

        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return value.GetBoolean() ? TabSwitcherMode.MostRecentlyUsed : TabSwitcherMode.Disabled;
        }

        return Enum(json, name, TabSwitcherMode.InOrder,
            ("mru", TabSwitcherMode.MostRecentlyUsed), ("inOrder", TabSwitcherMode.InOrder), ("disabled", TabSwitcherMode.Disabled));
    }

    private static CopyFormat? NullableCopyFormat(JsonElement json, string name)
    {
        if (!Property(json, name, out var value))
        {
            return null;
        }

        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return value.GetBoolean() ? CopyFormat.All : CopyFormat.None;
        }

        var text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return text?.ToLowerInvariant() switch
        {
            "none" => CopyFormat.None,
            "html" => CopyFormat.Html,
            "rtf" => CopyFormat.Rtf,
            "all" => CopyFormat.All,
            _ => null,
        };
    }

    private static SuggestionsSource Suggestions(JsonElement json, string name)
    {
        var text = String(json, name);
        return text?.ToLowerInvariant() switch
        {
            "none" => SuggestionsSource.None,
            "tasks" or "snippets" => SuggestionsSource.Tasks,
            "commandhistory" => SuggestionsSource.CommandHistory,
            "directoryhistory" => SuggestionsSource.DirectoryHistory,
            "quickfix" => SuggestionsSource.QuickFixes,
            "all" => SuggestionsSource.All,
            _ => SuggestionsSource.Tasks,
        };
    }

    private static SelectionColor? SelectionColor(JsonElement json, string name)
    {
        var value = String(json, name);
        return value is null ? null : new SelectionColor(value);
    }

    private static string Lower<T>(T value) where T : struct, Enum
    {
        var text = value.ToString();
        return text.Length == 0 ? text : char.ToLowerInvariant(text[0]) + text[1..];
    }

    private static string TabSwitcherModeString(TabSwitcherMode value) => value switch
    {
        TabSwitcherMode.MostRecentlyUsed => "mru",
        TabSwitcherMode.InOrder => "inOrder",
        _ => "disabled",
    };

    private static string CopyFormatString(CopyFormat value) => value switch
    {
        CopyFormat.None => "none",
        CopyFormat.Html => "html",
        CopyFormat.Rtf => "rtf",
        _ => "all",
    };

    private static string SuggestionsString(SuggestionsSource value) => value switch
    {
        SuggestionsSource.None => "none",
        SuggestionsSource.Tasks => "tasks",
        SuggestionsSource.CommandHistory => "commandHistory",
        SuggestionsSource.DirectoryHistory => "directoryHistory",
        SuggestionsSource.QuickFixes => "quickFix",
        _ => "all",
    };

    private static void String(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is not null) writer.WriteString(name, value);
    }

    private static void Number(Utf8JsonWriter writer, string name, uint? value)
    {
        if (value is { } number) writer.WriteNumber(name, number);
    }

    private static void Number(Utf8JsonWriter writer, string name, int? value)
    {
        if (value is { } number) writer.WriteNumber(name, number);
    }

    private static void Number(Utf8JsonWriter writer, string name, float value) => writer.WriteNumber(name, value);

    private static void Boolean(Utf8JsonWriter writer, string name, bool? value)
    {
        if (value is { } flag) writer.WriteBoolean(name, flag);
    }

    private static void Enum<T>(Utf8JsonWriter writer, string name, T? value, Func<T, string> format) where T : struct
    {
        if (value is { } enumValue) writer.WriteString(name, format(enumValue));
    }
}

public sealed class ActionAndArgsJsonConverter : JsonConverter<ActionAndArgs>
{
    public override ActionAndArgs Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        return ActionJson.Parse(document.RootElement);
    }

    public override void Write(Utf8JsonWriter writer, ActionAndArgs value, JsonSerializerOptions options) =>
        ActionJson.Write(writer, value);
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(ActionAndArgs))]
[JsonSerializable(typeof(List<ActionAndArgs>))]
[JsonSerializable(typeof(AdjustFontSizeArgs))]
[JsonSerializable(typeof(CloseOtherTabsArgs))]
[JsonSerializable(typeof(CloseTabsAfterArgs))]
[JsonSerializable(typeof(CloseTabArgs))]
[JsonSerializable(typeof(CopyTextArgs))]
[JsonSerializable(typeof(ExecuteCommandlineArgs))]
[JsonSerializable(typeof(FindMatchArgs))]
[JsonSerializable(typeof(SearchForTextArgs))]
[JsonSerializable(typeof(GlobalSummonArgs))]
[JsonSerializable(typeof(MoveFocusArgs))]
[JsonSerializable(typeof(MovePaneArgs))]
[JsonSerializable(typeof(SwapPaneArgs))]
[JsonSerializable(typeof(MoveTabArgs))]
[JsonSerializable(typeof(NewTerminalArgs))]
[JsonSerializable(typeof(BaseContentArgs))]
[JsonSerializable(typeof(NewTabArgs))]
[JsonSerializable(typeof(NewWindowArgs))]
[JsonSerializable(typeof(NextTabArgs))]
[JsonSerializable(typeof(OpenSettingsArgs))]
[JsonSerializable(typeof(SetFocusModeArgs))]
[JsonSerializable(typeof(SetFullScreenArgs))]
[JsonSerializable(typeof(SetMaximizedArgs))]
[JsonSerializable(typeof(PrevTabArgs))]
[JsonSerializable(typeof(RenameTabArgs))]
[JsonSerializable(typeof(RenameWindowArgs))]
[JsonSerializable(typeof(ResizePaneArgs))]
[JsonSerializable(typeof(ScrollDownArgs))]
[JsonSerializable(typeof(ScrollUpArgs))]
[JsonSerializable(typeof(ScrollToMarkArgs))]
[JsonSerializable(typeof(AddMarkArgs))]
[JsonSerializable(typeof(SendInputArgs))]
[JsonSerializable(typeof(SetColorSchemeArgs))]
[JsonSerializable(typeof(SetTabColorArgs))]
[JsonSerializable(typeof(SplitPaneArgs))]
[JsonSerializable(typeof(SwitchToTabArgs))]
[JsonSerializable(typeof(ToggleCommandPaletteArgs))]
[JsonSerializable(typeof(FocusPaneArgs))]
[JsonSerializable(typeof(ExportBufferArgs))]
[JsonSerializable(typeof(ClearBufferArgs))]
[JsonSerializable(typeof(MultipleActionsArgs))]
[JsonSerializable(typeof(AdjustOpacityArgs))]
[JsonSerializable(typeof(SuggestionsArgs))]
[JsonSerializable(typeof(SelectCommandArgs))]
[JsonSerializable(typeof(SelectOutputArgs))]
[JsonSerializable(typeof(ColorSelectionArgs))]
[JsonSerializable(typeof(OpenWorkspaceArgs))]
[JsonSerializable(typeof(UnknownActionArgs))]
public partial class ActionJsonContext : JsonSerializerContext;
