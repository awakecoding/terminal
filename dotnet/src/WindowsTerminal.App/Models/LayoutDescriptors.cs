using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Terminal.Settings;
using WindowsTerminal.Panes;

namespace WindowsTerminal.Models;

public sealed class TerminalSessionDescriptor
{
    public Guid SessionId { get; set; } = Guid.NewGuid();
    public string? ProfileId { get; set; }
    public string ProfileName { get; set; } = string.Empty;
    public string Commandline { get; set; } = string.Empty;
    public string StartingDirectory { get; set; } = string.Empty;
    public string? TabTitle { get; set; }
    public string? TabColor { get; set; }
    public string? Icon { get; set; }
    public bool Elevate { get; set; }
    public bool SuppressApplicationTitle { get; set; }
    public bool ReloadEnvironmentVariables { get; set; } = true;
}

public sealed class PaneLayoutDescriptor
{
    public TerminalSessionDescriptor? Session { get; set; }
    public PaneSplitOrientation? Orientation { get; set; }
    public double Ratio { get; set; } = 0.5;
    public PaneLayoutDescriptor? First { get; set; }
    public PaneLayoutDescriptor? Second { get; set; }
    public PanePresentationState Presentation { get; set; } = new();

    [JsonIgnore]
    public bool IsLeaf => Session is not null;
}

public sealed class TabLayoutDescriptor
{
    public Guid TabId { get; set; } = Guid.NewGuid();
    public Guid ActiveSessionId { get; set; }
    public Guid? ZoomedSessionId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? CustomTitle { get; set; }
    public string? Color { get; set; }
    public PaneLayoutDescriptor Root { get; set; } = new();
}

public sealed class TerminalWindowLayoutDescriptor
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public Guid? ActiveTabId { get; set; }
    public List<TabLayoutDescriptor> Tabs { get; set; } = [];
}

public sealed record TabTearOffRequest(
    Guid TransferId,
    int SourceWindowId,
    TabLayoutDescriptor Tab,
    PixelPosition ScreenPosition);

public readonly record struct PixelPosition(int X, int Y);

public sealed record TabTransferResult(Guid TransferId, bool Accepted, string? Message = null);

public interface ITabTransferTarget
{
    ValueTask<TabTransferResult> TransferTabAsync(
        TabTearOffRequest request,
        CancellationToken cancellationToken = default);
}

public static class TerminalLayoutSerializer
{
    public static JsonArray SerializeTabs(TerminalWindowLayoutDescriptor layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        Validate(layout);
        return
        [
            JsonSerializer.SerializeToNode(
                layout,
                TerminalLayoutJsonContext.Default.TerminalWindowLayoutDescriptor)!,
        ];
    }

    public static TerminalWindowLayoutDescriptor? DeserializeTabs(JsonArray tabs)
    {
        _ = TryDeserializeTabs(tabs, out var layout, out _);
        return layout;
    }

    public static bool TryDeserializeTabs(
        JsonArray tabs,
        out TerminalWindowLayoutDescriptor? layout,
        out string? diagnostic)
    {
        ArgumentNullException.ThrowIfNull(tabs);
        layout = null;
        diagnostic = null;
        if (tabs.Count != 1 || tabs[0] is not JsonObject document)
        {
            diagnostic = LooksLikeNativeActionArray(tabs)
                ? "Native Windows Terminal tabLayout action arrays are not supported by this port; the saved data was left unchanged."
                : "The saved tabLayout does not contain one versioned terminal layout descriptor.";
            return false;
        }

        if (document["version"] is null || document["tabs"] is null)
        {
            diagnostic = "Native Windows Terminal or unversioned tabLayout data is not supported by this port; the saved data was left unchanged.";
            return false;
        }

        try
        {
            layout = document.Deserialize(
                TerminalLayoutJsonContext.Default.TerminalWindowLayoutDescriptor);
            if (layout is null)
            {
                diagnostic = "The saved terminal layout descriptor was empty.";
                return false;
            }

            Validate(layout);
            return true;
        }
        catch (JsonException ex)
        {
            layout = null;
            diagnostic = $"The saved terminal layout descriptor is invalid JSON: {ex.Message}";
            return false;
        }
        catch (InvalidOperationException ex)
        {
            layout = null;
            diagnostic = ex.Message;
            return false;
        }
    }

    private static bool LooksLikeNativeActionArray(JsonArray tabs) =>
        tabs.Count > 0 &&
        tabs.OfType<JsonObject>().Any(static item =>
            item["command"] is not null || item["action"] is not null);

    public static WindowLayoutState ToApplicationState(
        TerminalWindowLayoutDescriptor layout,
        string? position = null,
        WindowSizeState? size = null,
        LaunchMode? launchMode = null) =>
        new()
        {
            TabLayout = SerializeTabs(layout),
            InitialPosition = position,
            InitialSize = size,
            LaunchMode = launchMode,
        };

    public static void Validate(TerminalWindowLayoutDescriptor layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (layout.Version != TerminalWindowLayoutDescriptor.CurrentVersion)
        {
            throw new InvalidOperationException($"Unsupported terminal layout version '{layout.Version}'.");
        }

        if (layout.Tabs is null)
        {
            throw new InvalidOperationException("A window layout must contain a tab collection.");
        }

        var tabIds = new HashSet<Guid>();
        foreach (var tab in layout.Tabs)
        {
            if (tab is null || tab.Root is null)
            {
                throw new InvalidOperationException("A tab and its root pane are required.");
            }

            if (tab.TabId == Guid.Empty || !tabIds.Add(tab.TabId))
            {
                throw new InvalidOperationException("Tab identifiers must be unique and non-empty.");
            }

            var sessions = new HashSet<Guid>();
            ValidatePane(tab.Root, sessions);
            if (!sessions.Contains(tab.ActiveSessionId) ||
                (tab.ZoomedSessionId is { } zoomed && !sessions.Contains(zoomed)))
            {
                throw new InvalidOperationException("Active and zoomed sessions must exist in their tab.");
            }
        }

        if (layout.ActiveTabId is { } activeTab && !tabIds.Contains(activeTab))
        {
            throw new InvalidOperationException("The active tab must exist in the window layout.");
        }
    }

    private static void ValidatePane(PaneLayoutDescriptor? pane, ISet<Guid> sessions)
    {
        if (pane is null || pane.Presentation is null)
        {
            throw new InvalidOperationException("A pane and its presentation state are required.");
        }

        if (pane.Session is { } session)
        {
            if (pane.First is not null ||
                pane.Second is not null ||
                pane.Orientation is not null ||
                session.SessionId == Guid.Empty ||
                !sessions.Add(session.SessionId))
            {
                throw new InvalidOperationException("Invalid or duplicate pane leaf.");
            }

            return;
        }

        if (pane.First is null || pane.Second is null || pane.Orientation is null)
        {
            throw new InvalidOperationException("A split pane must contain two children and an orientation.");
        }

        pane.Ratio = NormalizeRatio(pane.Ratio);
        ValidatePane(pane.First, sessions);
        ValidatePane(pane.Second, sessions);
    }

    private static double NormalizeRatio(double ratio) =>
        Math.Round(Math.Clamp(double.IsFinite(ratio) ? ratio : 0.5, 0.1, 0.9), 6);
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(TerminalWindowLayoutDescriptor))]
[JsonSerializable(typeof(TabLayoutDescriptor))]
[JsonSerializable(typeof(PaneLayoutDescriptor))]
[JsonSerializable(typeof(TerminalSessionDescriptor))]
[JsonSerializable(typeof(PanePresentationState))]
internal partial class TerminalLayoutJsonContext : JsonSerializerContext;
