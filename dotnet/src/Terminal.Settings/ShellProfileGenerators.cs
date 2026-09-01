namespace Microsoft.Terminal.Settings;

public sealed class InboxShellProfileGenerator : IDynamicProfileGenerator
{
    private readonly DynamicProfileEnvironment _environment;

    public InboxShellProfileGenerator(DynamicProfileEnvironment? environment = null)
    {
        _environment = environment ?? new DynamicProfileEnvironment();
    }

    public string Source => DynamicProfileSource.Inbox;
    public string DisplayName => "Windows shells";
    public string Icon => "\uE756";

    public ValueTask<DynamicProfileGeneratorResult> GenerateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var profiles = new List<ProfileSettings>();
        var powerShell = Path.Combine(
            _environment.SystemDirectory,
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        if (_environment.FileExists(powerShell))
        {
            var profile = ProfileSettings.CreatePowerShell();
            profile.Origin = SettingsOrigin.Inbox;
            profiles.Add(profile);
        }

        var cmd = Path.Combine(_environment.SystemDirectory, "cmd.exe");
        if (_environment.FileExists(cmd))
        {
            var profile = ProfileSettings.CreateCmd();
            profile.Origin = SettingsOrigin.Inbox;
            profiles.Add(profile);
        }

        return ValueTask.FromResult(new DynamicProfileGeneratorResult(profiles, []));
    }
}

public sealed class PowerShellCoreProfileGenerator : IDynamicProfileGenerator
{
    private const string PowerShellIcon = "ms-appx:///ProfileIcons/pwsh.png";
    private const string PowerShellPreviewIcon = "ms-appx:///ProfileIcons/pwsh-preview.png";
    private static readonly Guid PreferredProfileGuid = new("574e775e-4f2a-5b96-ac1e-a2962a402336");
    private readonly DynamicProfileEnvironment _environment;

    public PowerShellCoreProfileGenerator(DynamicProfileEnvironment? environment = null)
    {
        _environment = environment ?? new DynamicProfileEnvironment();
    }

    public string Source => DynamicProfileSource.PowerShellCore;
    public string DisplayName => "PowerShell";
    public string Icon => "ms-appx:///ProfileGeneratorIcons/PowerShell.png";

    public ValueTask<DynamicProfileGeneratorResult> GenerateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var instances = Discover()
            .DistinctBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(item => item.MajorVersion)
            .ThenBy(item => item.Preference)
            .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var profiles = new List<ProfileSettings>(instances.Length);
        for (var index = 0; index < instances.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var instance = instances[index];
            var name = index == 0 ? "PowerShell" : instance.Name;
            profiles.Add(new ProfileSettings
            {
                Guid = (index == 0 ? PreferredProfileGuid : ProfileGuid.CreateDynamic(name)).ToString("B"),
                Name = name,
                Source = Source,
                Origin = SettingsOrigin.Generated,
                Commandline = Quote(instance.Path),
                StartingDirectory = "%USERPROFILE%",
                Icon = instance.Preview ? PowerShellPreviewIcon : PowerShellIcon,
                ColorScheme = "Campbell",
            });
        }

        return ValueTask.FromResult(new DynamicProfileGeneratorResult(profiles, []));
    }

    private IEnumerable<PowerShellInstance> Discover()
    {
        foreach (var item in DiscoverVersioned(
                     Path.Combine(_environment.ProgramFiles, "PowerShell"),
                     preference: 0,
                     architectureSuffix: null))
        {
            yield return item;
        }

        foreach (var item in DiscoverVersioned(
                     Path.Combine(_environment.ProgramFilesX86, "PowerShell"),
                     preference: 5,
                     architectureSuffix: "(x86)"))
        {
            yield return item;
        }

        foreach (var item in DiscoverSingle(
                     Path.Combine(_environment.UserProfile, ".dotnet", "tools", "pwsh.exe"),
                     "PowerShell (dotnet global)",
                     preference: 3))
        {
            yield return item;
        }

        foreach (var item in DiscoverSingle(
                     Path.Combine(_environment.UserProfile, "scoop", "shims", "pwsh.exe"),
                     "PowerShell (scoop)",
                     preference: 2))
        {
            yield return item;
        }

        var pathInstance = _environment.ResolveExecutable("pwsh.exe");
        if (!string.IsNullOrWhiteSpace(pathInstance) && _environment.FileExists(pathInstance))
        {
            yield return new PowerShellInstance(0, 4, pathInstance, "PowerShell", Preview: false);
        }
    }

    private IEnumerable<PowerShellInstance> DiscoverVersioned(
        string root,
        int preference,
        string? architectureSuffix)
    {
        foreach (var directory in _environment.EnumerateDirectories(root))
        {
            var folder = Path.GetFileName(directory);
            var preview = folder.Contains("-preview", StringComparison.OrdinalIgnoreCase);
            var majorText = folder.Split('-', 2)[0].Split('.', 2)[0];
            if (!int.TryParse(majorText, out var major))
            {
                continue;
            }

            var executable = Path.Combine(directory, "pwsh.exe");
            if (!_environment.FileExists(executable))
            {
                continue;
            }

            var name = major < 7 ? $"PowerShell Core {major}" : $"PowerShell {major}";
            if (preview)
            {
                name += " Preview";
            }

            if (architectureSuffix is not null)
            {
                name += $" {architectureSuffix}";
            }

            yield return new PowerShellInstance(
                major,
                preference + (preview ? 10 : 0),
                executable,
                name,
                preview);
        }
    }

    private IEnumerable<PowerShellInstance> DiscoverSingle(string path, string name, int preference)
    {
        if (_environment.FileExists(path))
        {
            yield return new PowerShellInstance(0, preference, path, name, Preview: false);
        }
    }

    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    private sealed record PowerShellInstance(
        int MajorVersion,
        int Preference,
        string Path,
        string Name,
        bool Preview);
}
