using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Microsoft.Terminal.Settings;

public sealed class WindowLayoutState
{
    public JsonArray TabLayout { get; set; } = [];
    public string? InitialPosition { get; set; }
    public WindowSizeState? InitialSize { get; set; }
    public LaunchMode? LaunchMode { get; set; }
}

public sealed class WindowSizeState
{
    public double Width { get; set; }
    public double Height { get; set; }
}

public sealed class ApplicationStateData
{
    public string? SettingsHash { get; set; }
    public HashSet<Guid> GeneratedProfiles { get; set; } = [];
    public List<WindowLayoutState> PersistedWindowLayouts { get; set; } = [];
    public List<string> RecentCommands { get; set; } = [];
    public List<string> DismissedMessages { get; set; } = [];
    public List<string> AllowedCommandlines { get; set; } = [];
    public HashSet<string> DismissedBadges { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, WindowLayoutState> PersistedWorkspaces { get; set; } =
        new(StringComparer.Ordinal);
    public bool SshFolderGenerated { get; set; }
}

public sealed class ApplicationStateStore
{
    public ApplicationStateStore(string settingsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsDirectory);
        StatePath = Path.Combine(Path.GetFullPath(settingsDirectory), "state.json");
        Data = Load(StatePath, out var diagnostic);
        LastDiagnostic = diagnostic;
    }

    public string StatePath { get; }
    public ApplicationStateData Data { get; private set; }
    public SettingsDiagnostic? LastDiagnostic { get; private set; }

    public static ApplicationStateStore ForSettingsPath(string settingsPath)
    {
        var fullPath = Path.GetFullPath(settingsPath);
        return new ApplicationStateStore(
            Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException($"Settings path '{fullPath}' has no parent directory."));
    }

    public void Save()
    {
        var directory = Path.GetDirectoryName(StatePath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".state.{Guid.NewGuid():N}.tmp");
        try
        {
            var json = JsonSerializer.Serialize(Data, SettingsJsonContext.Default.ApplicationStateData);
            File.WriteAllText(temporaryPath, json, new UTF8Encoding(false));
            File.Move(temporaryPath, StatePath, overwrite: true);
            LastDiagnostic = null;
        }
        catch (IOException ex)
        {
            LastDiagnostic = WriteDiagnostic(ex);
            throw;
        }
        catch (UnauthorizedAccessException ex)
        {
            LastDiagnostic = WriteDiagnostic(ex);
            throw;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public void Reset()
    {
        if (File.Exists(StatePath))
        {
            File.Delete(StatePath);
        }

        Data = new();
        LastDiagnostic = null;
    }

    public void AppendPersistedWindowLayout(WindowLayoutState layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        Data.PersistedWindowLayouts.Add(layout);
    }

    public bool DismissBadge(string badgeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(badgeId);
        return Data.DismissedBadges.Add(badgeId);
    }

    public bool IsBadgeDismissed(string badgeId) => Data.DismissedBadges.Contains(badgeId);

    public void SaveWorkspace(string name, WindowLayoutState layout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(layout);
        Data.PersistedWorkspaces[name] = layout;
    }

    public bool RemoveWorkspace(string name) => Data.PersistedWorkspaces.Remove(name);

    public bool RenameWorkspace(string oldName, string newName)
    {
        if (string.IsNullOrEmpty(oldName) ||
            string.Equals(oldName, newName, StringComparison.Ordinal) ||
            !Data.PersistedWorkspaces.Remove(oldName, out var layout))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(newName))
        {
            Data.PersistedWorkspaces[newName] = layout;
        }

        return true;
    }

    public WindowLayoutState? TakeWorkspace(string name) =>
        Data.PersistedWorkspaces.Remove(name, out var layout) ? layout : null;

    private static ApplicationStateData Load(string path, out SettingsDiagnostic? diagnostic)
    {
        diagnostic = null;
        if (!File.Exists(path))
        {
            return new();
        }

        try
        {
            var json = File.ReadAllText(path, Encoding.UTF8);
            var state = JsonSerializer.Deserialize(
                json,
                SettingsJsonContext.Default.ApplicationStateData);
            if (!IsValid(state))
            {
                diagnostic = new SettingsDiagnostic(
                    SettingsDiagnosticSeverity.Warning,
                    "InvalidApplicationState",
                    $"Application state in '{path}' was discarded because a required collection was null.",
                    path);
                return new();
            }

            return state!;
        }
        catch (JsonException ex)
        {
            diagnostic = new SettingsDiagnostic(
                SettingsDiagnosticSeverity.Warning,
                "InvalidApplicationState",
                $"Application state in '{path}' was discarded: {ex.Message}",
                path,
                ex.LineNumber,
                ex.BytePositionInLine);
            return new();
        }
        catch (IOException ex)
        {
            diagnostic = ReadDiagnostic(path, ex);
            return new();
        }
        catch (UnauthorizedAccessException ex)
        {
            diagnostic = ReadDiagnostic(path, ex);
            return new();
        }
    }

    private static bool IsValid(ApplicationStateData? state) =>
        state is not null &&
        state.GeneratedProfiles is not null &&
        state.PersistedWindowLayouts is not null &&
        state.RecentCommands is not null &&
        state.DismissedMessages is not null &&
        state.AllowedCommandlines is not null &&
        state.DismissedBadges is not null &&
        state.PersistedWorkspaces is not null;

    private SettingsDiagnostic WriteDiagnostic(Exception exception) => new(
        SettingsDiagnosticSeverity.Error,
        "ApplicationStateWriteFailed",
        $"Could not write application state to '{StatePath}': {exception.Message}",
        StatePath);

    private static SettingsDiagnostic ReadDiagnostic(string path, Exception exception) => new(
        SettingsDiagnosticSeverity.Warning,
        "ApplicationStateReadFailed",
        $"Could not read application state from '{path}': {exception.Message}",
        path);
}
