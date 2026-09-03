using System.Text.Json.Nodes;

namespace Devolutions.Terminal.Settings;

public static class SettingsSnippetStore
{
    public static string Add(
        AppSettings settings,
        string name,
        string keyChord,
        string commandline)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(commandline);
        if (!string.IsNullOrWhiteSpace(keyChord) &&
            !KeyChord.TryParse(keyChord, out _))
        {
            throw new ArgumentException($"Invalid key chord '{keyChord}'.", nameof(keyChord));
        }

        var action = new ActionAndArgs(
            ShortcutAction.SendInput,
            new SendInputArgs(commandline));
        var id = action.GenerateId();
        var document = settings.UserDocument ??= new JsonObject();
        var actions = document["actions"] as JsonArray ?? [];
        document["actions"] = actions;
        actions.Add((JsonNode)new JsonObject
        {
            ["id"] = id,
            ["name"] = string.IsNullOrWhiteSpace(name) ? null : name,
            ["command"] = new JsonObject
            {
                ["action"] = "sendInput",
                ["input"] = commandline,
            },
        });

        if (!string.IsNullOrWhiteSpace(keyChord))
        {
            var keybindings = document["keybindings"] as JsonArray ?? [];
            document["keybindings"] = keybindings;
            keybindings.Add((JsonNode)new JsonObject
            {
                ["id"] = id,
                ["keys"] = keyChord,
            });
        }

        return id;
    }
}
