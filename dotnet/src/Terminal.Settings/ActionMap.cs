using System.Collections.ObjectModel;
using System.Text.Json.Nodes;

namespace Microsoft.Terminal.Settings;

public readonly record struct KeyChord
{
    private static readonly IReadOnlyDictionary<string, string> KeyAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["return"] = "enter",
            ["application"] = "menu",
            ["applications"] = "menu",
            ["apps"] = "menu",
            ["app"] = "menu",
            ["escape"] = "esc",
            ["pagedown"] = "pgdn",
            ["page_down"] = "pgdn",
            ["pageup"] = "pgup",
            ["page_up"] = "pgup",
            ["del"] = "delete",
            ["ins"] = "insert",
            ["spacebar"] = "space",
            ["numpad_add"] = "numpad_plus",
            ["numpad_subtract"] = "numpad_minus",
            ["numpad_decimal"] = "numpad_period",
        };

    public KeyChord(string value)
    {
        Value = Normalize(value);
    }

    public string Value { get; }

    public static KeyChord Parse(string value) => new(value);

    public static bool TryParse(string? value, out KeyChord chord)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                chord = default;
                return false;
            }

            chord = new(value);
            return true;
        }
        catch (ArgumentException)
        {
            chord = default;
            return false;
        }
    }

    public static string Normalize(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var control = false;
        var alt = false;
        var shift = false;
        var windows = false;
        string? key = null;

        foreach (var rawPart in value.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var part = rawPart.ToLowerInvariant();
            switch (part)
            {
                case "ctrl":
                case "control":
                case "ctl":
                    control = true;
                    break;
                case "alt":
                case "option":
                    alt = true;
                    break;
                case "shift":
                    shift = true;
                    break;
                case "win":
                case "windows":
                case "super":
                case "meta":
                case "cmd":
                    windows = true;
                    break;
                default:
                    if (key is not null)
                    {
                        throw new ArgumentException("A key chord can contain only one non-modifier key.", nameof(value));
                    }

                    key = NormalizeKey(part);
                    break;
            }
        }

        if (key is null)
        {
            throw new ArgumentException("A key chord must contain a non-modifier key.", nameof(value));
        }

        var parts = new List<string>(5);
        if (windows) parts.Add("win");
        if (control) parts.Add("ctrl");
        if (alt) parts.Add("alt");
        if (shift) parts.Add("shift");
        parts.Add(key);
        return string.Join('+', parts);
    }

    private static string NormalizeKey(string key)
    {
        if (KeyAliases.TryGetValue(key, out var alias))
        {
            return alias;
        }

        if (key.Length == 1 && char.IsLetterOrDigit(key[0]))
        {
            return key.ToLowerInvariant();
        }

        if ((key.StartsWith("vk(", StringComparison.Ordinal) || key.StartsWith("sc(", StringComparison.Ordinal)) &&
            key.EndsWith(')') &&
            TryParseCode(key.AsSpan(3, key.Length - 4), out var code))
        {
            if (key.StartsWith("vk(", StringComparison.Ordinal) && VirtualKeyName(code) is { } virtualKeyName)
            {
                return virtualKeyName;
            }

            if (key.StartsWith("sc(", StringComparison.Ordinal) && ScanCodeName(code) is { } scanCodeName)
            {
                return scanCodeName;
            }

            return $"{key[..3]}{code})";
        }

        if (key.StartsWith('f') && int.TryParse(key.AsSpan(1), out var functionKey) && functionKey is >= 1 and <= 24)
        {
            return $"f{functionKey}";
        }

        if (key is "enter" or "tab" or "space" or "backspace" or "menu" or "insert" or "delete" or
            "home" or "end" or "pgdn" or "pgup" or "esc" or "left" or "right" or "up" or "down" or
            "numpad_plus" or "numpad_minus" or "numpad_multiply" or "numpad_divide" or "numpad_period" or
            "plus" or "comma" or "minus" or "period" or "slash" or "backslash" or "semicolon" or
            "quote" or "open_bracket" or "close_bracket" or "backtick" or "browser_back" or "browser_forward" or
            "browser_refresh" or "browser_stop" or "browser_search" or "browser_favorites" or "browser_home")
        {
            return key;
        }

        if ((key.StartsWith("numpad", StringComparison.Ordinal) || key.StartsWith("numpad_", StringComparison.Ordinal)) &&
            char.IsDigit(key[^1]))
        {
            return $"numpad{key[^1]}";
        }

        throw new ArgumentException($"'{key}' is not a recognized key.", nameof(key));
    }

    private static bool TryParseCode(ReadOnlySpan<char> value, out byte code)
    {
        var style = System.Globalization.NumberStyles.None;
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            value = value[2..];
            style = System.Globalization.NumberStyles.AllowHexSpecifier;
        }

        return byte.TryParse(value, style, System.Globalization.CultureInfo.InvariantCulture, out code);
    }

    private static string? VirtualKeyName(byte code) => code switch
    {
        0x08 => "backspace",
        0x09 => "tab",
        0x0D => "enter",
        0x1B => "esc",
        0x20 => "space",
        0x21 => "pgup",
        0x22 => "pgdn",
        0x23 => "end",
        0x24 => "home",
        0x25 => "left",
        0x26 => "up",
        0x27 => "right",
        0x28 => "down",
        0x2D => "insert",
        0x2E => "delete",
        >= 0x30 and <= 0x39 => ((char)code).ToString().ToLowerInvariant(),
        >= 0x41 and <= 0x5A => ((char)code).ToString().ToLowerInvariant(),
        >= 0x60 and <= 0x69 => $"numpad{code - 0x60}",
        0x6A => "numpad_multiply",
        0x6B => "numpad_plus",
        0x6D => "numpad_minus",
        0x6E => "numpad_period",
        0x6F => "numpad_divide",
        >= 0x70 and <= 0x87 => $"f{code - 0x6F}",
        _ => null,
    };

    private static string? ScanCodeName(byte code) => code switch
    {
        0x29 => "backtick",
        _ => null,
    };

    public override string ToString() => Value ?? string.Empty;
}

public sealed class Command
{
    internal Command(
        string id,
        string? explicitName,
        string? nameResourceKey,
        string? description,
        string? icon,
        string? iterateOn,
        ActionAndArgs? actionAndArgs,
        IReadOnlyList<Command> nestedCommands,
        SettingsOrigin origin,
        bool idWasGenerated,
        JsonObject source)
    {
        Id = id;
        ExplicitName = explicitName;
        NameResourceKey = nameResourceKey;
        Description = description;
        Icon = icon;
        IterateOn = iterateOn;
        ActionAndArgs = actionAndArgs;
        NestedCommands = nestedCommands;
        Origin = origin;
        IdWasGenerated = idWasGenerated;
        Source = source;
    }

    public string Id { get; }
    public string ID => Id;
    public string? ExplicitName { get; }
    public string? NameResourceKey { get; }
    public string Name => ExplicitName ?? NameResourceKey ?? ActionAndArgs?.GenerateName() ?? string.Empty;
    public string? Description { get; }
    public string? Icon { get; }
    public string? IterateOn { get; }
    public ActionAndArgs? ActionAndArgs { get; }
    public ActionAndArgs? Action => ActionAndArgs;
    public IReadOnlyList<Command> NestedCommands { get; }
    public IReadOnlyList<Command> Commands => NestedCommands;
    public bool HasNestedCommands => NestedCommands.Count > 0;
    public SettingsOrigin Origin { get; }
    public bool IdWasGenerated { get; }
    public JsonObject Source { get; }
}

public sealed record KeyBindingConflict(
    KeyChord Chord,
    string? PreviousCommandId,
    string? CommandId,
    bool IsUnbinding);

public sealed class ActionMap
{
    private readonly Dictionary<string, Command> _commands = new(StringComparer.Ordinal);
    private readonly Dictionary<KeyChord, string> _bindingIds = [];
    private readonly HashSet<KeyChord> _explicitUnbindings = [];
    private readonly List<KeyBindingConflict> _conflicts = [];
    private readonly List<Command> _nestedCommandGroups = [];
    private readonly List<Command> _iterableCommands = [];

    public IReadOnlyDictionary<string, Command> Commands =>
        new ReadOnlyDictionary<string, Command>(_commands);

    public IReadOnlyDictionary<string, ActionAndArgs> AvailableActions =>
        new ReadOnlyDictionary<string, ActionAndArgs>(
            _commands
                .Where(pair => pair.Value.ActionAndArgs is not null)
                .ToDictionary(pair => pair.Key, pair => pair.Value.ActionAndArgs!, StringComparer.Ordinal));

    public IReadOnlyDictionary<KeyChord, Command> KeyBindings =>
        new ReadOnlyDictionary<KeyChord, Command>(
            _bindingIds
                .Where(pair => pair.Value.Length > 0 && _commands.ContainsKey(pair.Value))
                .ToDictionary(pair => pair.Key, pair => _commands[pair.Value]));

    public IReadOnlyDictionary<KeyChord, string> BindingIds =>
        new ReadOnlyDictionary<KeyChord, string>(_bindingIds);

    public IReadOnlyCollection<KeyChord> ExplicitUnbindings => _explicitUnbindings;
    public IReadOnlyList<KeyBindingConflict> Conflicts => _conflicts;

    public IReadOnlyList<Command> AllCommands =>
        _commands.Values
            .Concat(_nestedCommandGroups)
            .Concat(_iterableCommands)
            .ToArray();

    public static ActionMap FromJson(JsonArray? actions, JsonArray? keybindings = null)
    {
        var map = new ActionMap();
        map.Layer(actions, keybindings);
        return map;
    }

    public void Layer(
        JsonArray? actions,
        JsonArray? keybindings = null,
        SettingsOrigin origin = SettingsOrigin.User)
    {
        LayerArray(actions, origin, allowCommandsWithoutKeys: true);
        LayerArray(keybindings, origin, allowCommandsWithoutKeys: false);
    }

    public Command? GetActionByKeyChord(KeyChord chord)
    {
        if (!_bindingIds.TryGetValue(chord, out var id) || id.Length == 0)
        {
            return null;
        }

        return GetActionByID(id);
    }

    public Command? GetActionByKeyChord(string chord) =>
        KeyChord.TryParse(chord, out var parsed) ? GetActionByKeyChord(parsed) : null;

    public Command? Resolve(KeyChord chord) => GetActionByKeyChord(chord);
    public Command? Resolve(string chord) => GetActionByKeyChord(chord);
    public ActionAndArgs? ResolveAction(KeyChord chord) => Resolve(chord)?.ActionAndArgs;
    public ActionAndArgs? ResolveAction(string chord) => Resolve(chord)?.ActionAndArgs;

    public Command? GetActionByID(string commandId) =>
        _commands.TryGetValue(commandId, out var command) ? command : null;

    public Command? GetCommand(string commandId) => GetActionByID(commandId);
    public ActionAndArgs? GetAction(string commandId) => GetActionByID(commandId)?.ActionAndArgs;
    public IEnumerable<ActionAndArgs> EnumerateActions() => AvailableActions.Values;
    public IEnumerable<Command> EnumerateCommands() => AllCommands;

    public bool IsKeyChordExplicitlyUnbound(KeyChord chord) => _explicitUnbindings.Contains(chord);

    public bool IsKeyChordExplicitlyUnbound(string chord) =>
        KeyChord.TryParse(chord, out var parsed) && IsKeyChordExplicitlyUnbound(parsed);

    public KeyChord? GetKeyBindingForAction(string commandId)
    {
        foreach (var pair in _bindingIds)
        {
            if (string.Equals(pair.Value, commandId, StringComparison.Ordinal))
            {
                return pair.Key;
            }
        }

        return null;
    }

    public IReadOnlyList<KeyChord> AllKeyBindingsForAction(string commandId) =>
        _bindingIds
            .Where(pair => string.Equals(pair.Value, commandId, StringComparison.Ordinal))
            .Select(pair => pair.Key)
            .ToArray();

    public bool TryGetCommandId(KeyChord chord, out string commandId)
    {
        if (_bindingIds.TryGetValue(chord, out var id) && id.Length > 0)
        {
            commandId = id;
            return true;
        }

        commandId = string.Empty;
        return false;
    }

    private void LayerArray(JsonArray? array, SettingsOrigin origin, bool allowCommandsWithoutKeys)
    {
        if (array is null)
        {
            return;
        }

        foreach (var item in array.OfType<JsonObject>())
        {
            var chords = ParseChords(item["keys"]);
            var hasCommand = item.ContainsKey("command") || item.ContainsKey("commands");
            if (hasCommand)
            {
                var command = ParseCommand(item, origin);
                AddCommand(command);
                foreach (var commandChord in chords)
                {
                    Bind(
                        commandChord,
                        command.ActionAndArgs is { Action: ShortcutAction.Invalid, IsUnknown: false } ? null : command.Id);
                }
            }
            else if (chords.Count > 0)
            {
                var id = String(item, "id");
                foreach (var bindingChord in chords)
                {
                    Bind(bindingChord, string.IsNullOrEmpty(id) ? null : id);
                }
            }
            else if (allowCommandsWithoutKeys && item.ContainsKey("id"))
            {
                // A malformed command definition remains visible in Source through the
                // generated unknown command rather than being silently discarded.
                var command = ParseCommand(item, origin);
                AddCommand(command);
            }
        }
    }

    private void AddCommand(Command command)
    {
        if (command.HasNestedCommands)
        {
            _nestedCommandGroups.RemoveAll(existing =>
                existing.Id.Length > 0 && string.Equals(existing.Id, command.Id, StringComparison.Ordinal));
            _nestedCommandGroups.Add(command);
            return;
        }

        if (!string.IsNullOrEmpty(command.IterateOn))
        {
            _iterableCommands.Add(command);
            return;
        }

        if (command.ActionAndArgs is { Action: ShortcutAction.Invalid, IsUnknown: false })
        {
            if (command.Id.Length > 0)
            {
                _commands.Remove(command.Id);
            }
            return;
        }

        if (command.Id.Length > 0)
        {
            _commands[command.Id] = command;
        }
    }

    private static Command ParseCommand(JsonObject source, SettingsOrigin origin)
    {
        var nested = new List<Command>();
        if (source["commands"] is JsonArray commands)
        {
            foreach (var item in commands.OfType<JsonObject>())
            {
                nested.Add(ParseCommand(item, origin));
            }
        }

        ActionAndArgs? action = null;
        if (source.ContainsKey("command"))
        {
            action = ActionJson.Parse(source["command"]);
        }
        else if (nested.Count == 0)
        {
            action = new ActionAndArgs(ShortcutAction.Invalid);
        }

        var id = String(source, "id") ?? string.Empty;
        var generated = false;
        if (id.Length == 0 && action is not null &&
            (action.Action is not ShortcutAction.Invalid || action.IsUnknown))
        {
            id = action.GenerateId();
            generated = true;
        }

        var explicitName = source["name"]?.GetValueKind() == System.Text.Json.JsonValueKind.String
            ? source["name"]!.GetValue<string>()
            : null;
        var resourceName = source["name"] is JsonObject nameObject ? String(nameObject, "key") : null;

        return new Command(
            id,
            explicitName,
            resourceName,
            String(source, "description"),
            source["icon"]?.GetValueKind() == System.Text.Json.JsonValueKind.String
                ? source["icon"]!.GetValue<string>()
                : null,
            String(source, "iterateOn"),
            action,
            nested,
            origin,
            generated,
            (JsonObject)source.DeepClone());
    }

    private void Bind(KeyChord chord, string? id)
    {
        var value = id ?? string.Empty;
        if (_bindingIds.TryGetValue(chord, out var previous) &&
            !string.Equals(previous, value, StringComparison.Ordinal))
        {
            _conflicts.Add(new(
                chord,
                previous.Length == 0 ? null : previous,
                value.Length == 0 ? null : value,
                value.Length == 0));
        }

        _bindingIds[chord] = value;
        if (value.Length == 0)
        {
            _explicitUnbindings.Add(chord);
        }
        else
        {
            _explicitUnbindings.Remove(chord);
        }
    }

    private static IReadOnlyList<KeyChord> ParseChords(JsonNode? node)
    {
        if (node?.GetValueKind() == System.Text.Json.JsonValueKind.String)
        {
            return KeyChord.TryParse(node.GetValue<string>(), out var chord) ? [chord] : [];
        }

        if (node is not JsonArray values)
        {
            return [];
        }

        var result = new List<KeyChord>(values.Count);
        foreach (var value in values)
        {
            if (value?.GetValueKind() == System.Text.Json.JsonValueKind.String &&
                KeyChord.TryParse(value.GetValue<string>(), out var chord))
            {
                result.Add(chord);
            }
        }

        return result;
    }

    private static string? String(JsonObject source, string property) =>
        source[property]?.GetValueKind() == System.Text.Json.JsonValueKind.String
            ? source[property]!.GetValue<string>()
            : null;
}
