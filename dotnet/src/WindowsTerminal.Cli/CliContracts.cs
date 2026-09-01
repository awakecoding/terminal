using System.Text;
using System.Text.Json;
using Microsoft.Terminal.Settings;

namespace WindowsTerminal.Cli;

[Flags]
public enum CliLaunchMode
{
    Default = 0,
    Maximized = 1,
    Fullscreen = 2,
    Focus = 4,
}

public sealed record CliSaveRequest(string Name, string KeyChord, string Commandline);

public sealed record CliInvocation(
    string TargetWindow,
    int? PositionX,
    int? PositionY,
    int? Columns,
    int? Rows,
    CliLaunchMode LaunchMode,
    int? SavedLayout,
    IReadOnlyList<ActionAndArgs> Actions,
    CliSaveRequest? SaveRequest = null);

public sealed record CliParseResult(
    int ExitCode,
    string Message,
    bool ShouldExit,
    CliInvocation? Invocation)
{
    public bool IsSuccess => ExitCode == 0;
}

public static class CliInvocationSerializer
{
    public static string Serialize(CliInvocation invocation)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("targetWindow", invocation.TargetWindow);
            WriteNullable(writer, "positionX", invocation.PositionX);
            WriteNullable(writer, "positionY", invocation.PositionY);
            WriteNullable(writer, "columns", invocation.Columns);
            WriteNullable(writer, "rows", invocation.Rows);
            writer.WriteNumber("launchMode", (int)invocation.LaunchMode);
            WriteNullable(writer, "savedLayout", invocation.SavedLayout);
            writer.WritePropertyName("actions");
            writer.WriteStartArray();
            foreach (var action in invocation.Actions)
            {
                using var actionDocument = JsonDocument.Parse(ActionJson.Serialize(action));
                actionDocument.RootElement.WriteTo(writer);
            }

            writer.WriteEndArray();
            if (invocation.SaveRequest is { } save)
            {
                writer.WritePropertyName("save");
                writer.WriteStartObject();
                writer.WriteString("name", save.Name);
                writer.WriteString("keyChord", save.KeyChord);
                writer.WriteString("commandline", save.Commandline);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static CliInvocation Deserialize(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var actions = new List<ActionAndArgs>();
        foreach (var element in root.GetProperty("actions").EnumerateArray())
        {
            actions.Add(ActionJson.Parse(element));
        }

        CliSaveRequest? save = null;
        if (root.TryGetProperty("save", out var saveElement))
        {
            save = new(
                String(saveElement, "name"),
                String(saveElement, "keyChord"),
                String(saveElement, "commandline"));
        }

        return new(
            String(root, "targetWindow"),
            NullableInt(root, "positionX"),
            NullableInt(root, "positionY"),
            NullableInt(root, "columns"),
            NullableInt(root, "rows"),
            (CliLaunchMode)root.GetProperty("launchMode").GetInt32(),
            NullableInt(root, "savedLayout"),
            actions,
            save);
    }

    private static void WriteNullable(Utf8JsonWriter writer, string name, int? value)
    {
        if (value is { } number)
        {
            writer.WriteNumber(name, number);
        }
        else
        {
            writer.WriteNull(name);
        }
    }

    private static int? NullableInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : null;

    private static string String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value.GetString() ?? string.Empty : string.Empty;
}
