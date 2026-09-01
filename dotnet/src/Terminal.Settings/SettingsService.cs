using System.Text.Json;

namespace Microsoft.Terminal.Settings;

public static class SettingsService
{
    public static string SettingsDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WindowsTerminal.NET");

    public static string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var loaded = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppSettings);
                if (loaded is not null)
                {
                    EnsureProfiles(loaded);
                    return loaded;
                }
            }
        }
        catch (Exception)
        {
            // Fall back to defaults when the user file is missing or invalid.
        }

        var settings = CreateDefault();
        Save(settings);
        return settings;
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(SettingsDirectory);
        var json = JsonSerializer.Serialize(settings, SettingsJsonContext.Default.AppSettings);
        File.WriteAllText(SettingsPath, json);
    }

    public static AppSettings CreateDefault()
    {
        var settings = new AppSettings
        {
            Profiles = [ProfileSettings.PowerShell, ProfileSettings.Cmd],
            DefaultProfile = ProfileSettings.PowerShell.Guid,
            Schemes =
            [
                new SchemeSettings { Name = "Campbell" },
                new SchemeSettings { Name = "One Half Dark", Background = "#282C34", Foreground = "#DCDFE4" },
                new SchemeSettings { Name = "Solarized Dark", Background = "#002B36", Foreground = "#839496" },
            ],
        };

        var pwsh = FindPwsh();
        if (pwsh is not null)
        {
            var profile = ProfileSettings.Pwsh;
            profile.Commandline = pwsh;
            settings.Profiles.Insert(0, profile);
            settings.DefaultProfile = profile.Guid;
        }

        return settings;
    }

    private static void EnsureProfiles(AppSettings settings)
    {
        if (settings.Profiles.Count == 0)
        {
            settings.Profiles.Add(ProfileSettings.PowerShell);
            settings.Profiles.Add(ProfileSettings.Cmd);
        }
    }

    private static string? FindPwsh()
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory.Trim('"'), "pwsh.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
