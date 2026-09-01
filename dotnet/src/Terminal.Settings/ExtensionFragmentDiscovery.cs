using System.Text;

namespace Microsoft.Terminal.Settings;

public sealed record ExtensionFragmentDiscoveryResult(
    IReadOnlyList<SettingsLayer> Fragments,
    IReadOnlyList<SettingsDiagnostic> Diagnostics);

public static class ExtensionFragmentDiscovery
{
    public static IReadOnlyList<string> DefaultRoots()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return
        [
            Path.Combine(localAppData, "Microsoft", "Windows Terminal", "Fragments"),
            Path.Combine(programData, "Microsoft", "Windows Terminal", "Fragments"),
        ];
    }

    public static ExtensionFragmentDiscoveryResult Discover(IEnumerable<string>? roots = null)
    {
        var fragments = new List<SettingsLayer>();
        var diagnostics = new List<SettingsDiagnostic>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots ?? DefaultRoots())
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            string[] files;
            try
            {
                files = Directory.GetFiles(
                    root,
                    "*.json",
                    new EnumerationOptions
                    {
                        RecurseSubdirectories = true,
                        IgnoreInaccessible = true,
                        AttributesToSkip = FileAttributes.ReparsePoint,
                        MatchCasing = MatchCasing.CaseInsensitive,
                    });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                diagnostics.Add(new SettingsDiagnostic(
                    SettingsDiagnosticSeverity.Warning,
                    "FragmentEnumerationFailed",
                    $"Could not enumerate settings fragments in '{root}': {ex.Message}",
                    root));
                continue;
            }

            foreach (var path in files.Order(StringComparer.OrdinalIgnoreCase))
            {
                var fullPath = Path.GetFullPath(path);
                if (!seen.Add(fullPath))
                {
                    continue;
                }

                try
                {
                    fragments.Add(new SettingsLayer(
                        fullPath,
                        File.ReadAllText(fullPath, Encoding.UTF8),
                        SettingsLayerKind.Fragment));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    diagnostics.Add(new SettingsDiagnostic(
                        SettingsDiagnosticSeverity.Warning,
                        "FragmentReadFailed",
                        $"Could not read settings fragment '{fullPath}': {ex.Message}",
                        fullPath));
                }
            }
        }

        return new ExtensionFragmentDiscoveryResult(fragments, diagnostics);
    }
}
