using System.Diagnostics;
using System.Text;

namespace Microsoft.Terminal.Settings;

public static class SettingsService
{
    public static string SettingsDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WindowsTerminal.NET");

    public static string SettingsPath =>
        Environment.GetEnvironmentVariable("WT_DOTNET_SETTINGS_PATH") ??
        Path.Combine(SettingsDirectory, "settings.json");

    public static string StatePath => Path.Combine(
        Path.GetDirectoryName(Path.GetFullPath(SettingsPath))!,
        "state.json");

    public static IReadOnlyList<SettingsDiagnostic> LastDiagnostics { get; private set; } = [];

    public static AppSettings Load()
    {
        string? userJson = null;
        SettingsDiagnostic? readDiagnostic = null;
        if (File.Exists(SettingsPath))
        {
            try
            {
                userJson = File.ReadAllText(SettingsPath, Encoding.UTF8);
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

        var settings = SettingsLoader.Load(
            SettingsLoader.ReadEmbeddedDefaults(),
            userJson,
            ReadFragments(),
            SettingsPath);
        if (readDiagnostic is not null)
        {
            settings.Diagnostics.Add(readDiagnostic);
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
        ApplicationStateStore.ForSettingsPath(SettingsPath);

    private static SettingsDiagnostic ReadDiagnostic(Exception exception) => new(
        SettingsDiagnosticSeverity.Error,
        "SettingsReadFailed",
        $"Could not read settings from '{SettingsPath}': {exception.Message}",
        SettingsPath);

    private static IEnumerable<SettingsLayer> ReadFragments()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var roots = new[]
        {
            Path.Combine(localAppData, "Microsoft", "Windows Terminal", "Fragments"),
            Path.Combine(programData, "Microsoft", "Windows Terminal", "Fragments"),
        };

        foreach (var root in roots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            string[] paths;
            try
            {
                paths = Directory.GetFiles(
                    root,
                    "*.json",
                    new EnumerationOptions
                    {
                        RecurseSubdirectories = true,
                        IgnoreInaccessible = true,
                        AttributesToSkip = FileAttributes.ReparsePoint,
                    });
            }
            catch (IOException ex)
            {
                Trace.WriteLine($"Could not enumerate settings fragments in '{root}': {ex.Message}");
                continue;
            }
            catch (UnauthorizedAccessException ex)
            {
                Trace.WriteLine($"Could not enumerate settings fragments in '{root}': {ex.Message}");
                continue;
            }

            foreach (var path in paths.Order(StringComparer.OrdinalIgnoreCase))
            {
                string json;
                try
                {
                    json = File.ReadAllText(path, Encoding.UTF8);
                }
                catch (IOException ex)
                {
                    Trace.WriteLine($"Could not read settings fragment '{path}': {ex.Message}");
                    continue;
                }
                catch (UnauthorizedAccessException ex)
                {
                    Trace.WriteLine($"Could not read settings fragment '{path}': {ex.Message}");
                    continue;
                }

                yield return new SettingsLayer(path, json, SettingsLayerKind.Fragment);
            }
        }
    }
}
