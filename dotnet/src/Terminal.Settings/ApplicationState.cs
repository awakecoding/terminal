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
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        using var stateLock = AcquireStateLock();
        SaveUnlocked();
    }

    public void Update(Action<ApplicationStateData> update)
    {
        ArgumentNullException.ThrowIfNull(update);

        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        using var stateLock = AcquireStateLock();
        Data = Load(StatePath, out var diagnostic);
        LastDiagnostic = diagnostic;
        update(Data);
        SaveUnlocked();
    }

    private void SaveUnlocked()
    {
        var directory = Path.GetDirectoryName(StatePath)!;
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
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        using var stateLock = AcquireStateLock();
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

    public void SavePersistedWindowLayout(int index, WindowLayoutState layout)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentNullException.ThrowIfNull(layout);

        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        using var stateLock = AcquireStateLock();
        Data = Load(StatePath, out var diagnostic);
        LastDiagnostic = diagnostic;
        while (Data.PersistedWindowLayouts.Count <= index)
        {
            Data.PersistedWindowLayouts.Add(new WindowLayoutState());
        }

        Data.PersistedWindowLayouts[index] = layout;
        SaveUnlocked();
    }

    private FileStream AcquireStateLock()
    {
        var lockPath = StatePath + ".lock";
        for (var attempt = 0; attempt < 80; attempt++)
        {
            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None);
            }
            catch (IOException) when (attempt < 79)
            {
                Thread.Sleep(25);
            }
        }

        throw new IOException($"Could not acquire the application state lock '{lockPath}'.");
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
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        using var stateLock = AcquireStateLock();
        if (File.Exists(StatePath))
        {
            Data = Load(StatePath, out var diagnostic);
            LastDiagnostic = diagnostic;
        }

        Data.PersistedWorkspaces[name] = layout;
        SaveUnlocked();
    }

    public IReadOnlyList<string> GetWorkspaceNames()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        using var stateLock = AcquireStateLock();
        Data = Load(StatePath, out var diagnostic);
        LastDiagnostic = diagnostic;
        return Data.PersistedWorkspaces.Keys
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public WindowLayoutState? GetWorkspace(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        using var stateLock = AcquireStateLock();
        Data = Load(StatePath, out var diagnostic);
        LastDiagnostic = diagnostic;
        return Data.PersistedWorkspaces.GetValueOrDefault(name);
    }

    public bool RemoveWorkspace(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return MutateWorkspace(data => data.PersistedWorkspaces.Remove(name));
    }

    public bool RenameWorkspace(string oldName, string newName)
    {
        if (string.IsNullOrEmpty(oldName) ||
            string.Equals(oldName, newName, StringComparison.Ordinal))
        {
            return false;
        }

        return MutateWorkspace(data =>
        {
            if (!data.PersistedWorkspaces.Remove(oldName, out var layout))
            {
                return false;
            }

            if (!string.IsNullOrEmpty(newName))
            {
                data.PersistedWorkspaces[newName] = layout;
            }

            return true;
        });
    }

    public WindowLayoutState? TakeWorkspace(
        string name,
        Func<WindowLayoutState, bool>? canConsume = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        using var stateLock = AcquireStateLock();
        Data = Load(StatePath, out var diagnostic);
        LastDiagnostic = diagnostic;
        if (!Data.PersistedWorkspaces.TryGetValue(name, out var layout) ||
            (canConsume is not null && !canConsume(layout)))
        {
            return null;
        }

        Data.PersistedWorkspaces.Remove(name);
        SaveUnlocked();
        return layout;
    }

    private bool MutateWorkspace(Func<ApplicationStateData, bool> update)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        using var stateLock = AcquireStateLock();
        Data = Load(StatePath, out var diagnostic);
        LastDiagnostic = diagnostic;
        if (!update(Data))
        {
            return false;
        }

        SaveUnlocked();
        return true;
    }

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
