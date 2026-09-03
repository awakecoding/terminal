using System.Diagnostics;
using System.Text;

namespace Devolutions.Terminal.Settings;

public static class SettingsService
{
    public static string SettingsDirectory { get; } = ResolveSettingsDirectory();
    public static string StateDirectory { get; } = ResolveStateDirectory();

    public static string SettingsPath =>
        SettingsPathOverride ?? Path.Combine(SettingsDirectory, "settings.json");

    public static string StatePath =>
        SettingsPathOverride is { Length: > 0 }
            ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(SettingsPath))!, "state.json")
            : Path.Combine(StateDirectory, "state.json");

    public static IReadOnlyList<SettingsDiagnostic> LastDiagnostics { get; private set; } = [];

    public static AppSettings Load() =>
        LoadAsync().AsTask().GetAwaiter().GetResult();

    public static AppSettings LoadWithDynamicProfiles(DynamicProfileManager dynamicProfileManager) =>
        LoadWithDynamicProfilesAsync(dynamicProfileManager).AsTask().GetAwaiter().GetResult();

    public static ValueTask<AppSettings> LoadAsync(CancellationToken cancellationToken = default) =>
        LoadWithDynamicProfilesAsync(DynamicProfileManager.CreateDefault(), cancellationToken);

    public static async ValueTask<AppSettings> LoadWithDynamicProfilesAsync(
        DynamicProfileManager dynamicProfileManager,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dynamicProfileManager);
        string? userJson = null;
        SettingsDiagnostic? readDiagnostic = null;
        if (File.Exists(SettingsPath))
        {
            try
            {
                userJson = await File.ReadAllTextAsync(
                    SettingsPath,
                    Encoding.UTF8,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                readDiagnostic = ReadDiagnostic(ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                readDiagnostic = ReadDiagnostic(ex);
            }
        }

        var fragmentDiscovery = ExtensionFragmentDiscovery.Discover();
        var state = LoadApplicationState();
        var loaded = await DynamicSettingsLoader.LoadAsync(
            SettingsLoader.ReadEmbeddedDefaults(),
            userJson,
            fragmentDiscovery.Fragments,
            dynamicProfileManager,
            state.Data.GeneratedProfiles,
            SettingsPath,
            cancellationToken).ConfigureAwait(false);
        var settings = loaded.Settings;
        ApplyPlatformProfiles(settings);
        settings.Diagnostics.AddRange(fragmentDiscovery.Diagnostics);
        if (readDiagnostic is not null)
        {
            settings.Diagnostics.Add(readDiagnostic);
        }

        if (state.LastDiagnostic is not null)
        {
            settings.Diagnostics.Add(state.LastDiagnostic);
        }

        if (!state.Data.GeneratedProfiles.IsSupersetOf(loaded.Generation.GeneratedProfileIds))
        {
            try
            {
                state.Update(loaded.Generation.UpdateState);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                settings.Diagnostics.Add(state.LastDiagnostic ?? new SettingsDiagnostic(
                    SettingsDiagnosticSeverity.Warning,
                    "ApplicationStateWriteFailed",
                    $"Could not persist generated profile state: {ex.Message}",
                    state.StatePath));
            }
        }

        LastDiagnostics = settings.Diagnostics;

        foreach (var diagnostic in settings.Diagnostics)
        {
            Trace.WriteLine($"{diagnostic.Severity} {diagnostic.Code}: {diagnostic.Message}");
        }

        return settings;
    }

    public static void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var path = Path.GetFullPath(SettingsPath);
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException($"Settings path '{path}' has no parent directory.");
        Directory.CreateDirectory(directory);

        var tempPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        var backupPath = path + ".bak";
        try
        {
            File.WriteAllText(tempPath, SettingsLoader.SerializeUserDocument(settings), new UTF8Encoding(false));
            if (File.Exists(path))
            {
                File.Copy(path, backupPath, overwrite: true);
            }

            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    public static AppSettings CreateDefault() =>
        SettingsLoader.Load(SettingsLoader.ReadEmbeddedDefaults());

    public static ApplicationStateStore LoadApplicationState() =>
        new(Path.GetDirectoryName(Path.GetFullPath(StatePath))!);

    private static string ResolveSettingsDirectory()
    {
        if (!OperatingSystem.IsLinux())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Devolutions",
                "Terminal");
        }

        var root = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(root))
        {
            root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config");
        }

        return Path.Combine(root, "devolutions-terminal");
    }

    private static string ResolveStateDirectory()
    {
        if (!OperatingSystem.IsLinux())
        {
            return SettingsDirectory;
        }

        var root = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        if (string.IsNullOrWhiteSpace(root))
        {
            root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local",
                "state");
        }

        return Path.Combine(root, "devolutions-terminal");
    }

    private static string? SettingsPathOverride =>
        FirstNonEmpty(
            Environment.GetEnvironmentVariable("DTERM_SETTINGS_PATH"),
            Environment.GetEnvironmentVariable("WT_DOTNET_SETTINGS_PATH"));

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static void ApplyPlatformProfiles(AppSettings settings)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        foreach (var profile in settings.Profiles.Where(static profile =>
                     profile.Origin == SettingsOrigin.Inbox &&
                     profile.Commandline.Contains("%SystemRoot%", StringComparison.OrdinalIgnoreCase)))
        {
            profile.Hidden = true;
        }

        if (settings.GetDefaultProfile().Hidden &&
            settings.Profiles.FirstOrDefault(static profile =>
                !profile.Hidden &&
                profile.Source == DynamicProfileSource.Linux) is { } linuxDefault)
        {
            settings.DefaultProfile = linuxDefault.Guid;
        }
    }

    private static SettingsDiagnostic ReadDiagnostic(Exception exception) => new(
        SettingsDiagnosticSeverity.Error,
        "SettingsReadFailed",
        $"Could not read settings from '{SettingsPath}': {exception.Message}",
        SettingsPath);

}
