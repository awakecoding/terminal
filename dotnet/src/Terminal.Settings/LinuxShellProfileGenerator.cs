namespace Microsoft.Terminal.Settings;

public sealed class LinuxShellProfileGenerator(DynamicProfileEnvironment environment)
    : IDynamicProfileGenerator
{
    public string Source => DynamicProfileSource.Linux;
    public string DisplayName => "Linux shells";
    public string Icon => "ms-appx:///ProfileIcons/terminal.png";

    public ValueTask<DynamicProfileGeneratorResult> GenerateAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var candidates = new List<string>();
        Add(environment.Shell);
        foreach (var shell in new[] { "bash", "zsh", "fish", "pwsh", "sh" })
        {
            Add(environment.ResolveExecutable(shell));
        }

        var profiles = candidates
            .Distinct(StringComparer.Ordinal)
            .Select(CreateProfile)
            .ToArray();
        return ValueTask.FromResult(new DynamicProfileGeneratorResult(profiles, []));

        void Add(string? executable)
        {
            if (!string.IsNullOrWhiteSpace(executable) &&
                environment.FileExists(executable))
            {
                candidates.Add(Path.GetFullPath(executable));
            }
        }
    }

    private ProfileSettings CreateProfile(string executable)
    {
        var fileName = Path.GetFileName(executable);
        var name = fileName switch
        {
            "bash" => "Bash",
            "zsh" => "Zsh",
            "fish" => "Fish",
            "pwsh" => "PowerShell",
            "sh" => "Shell",
            _ => fileName,
        };
        return new ProfileSettings
        {
            Guid = ProfileGuid.Create(name, Source).ToString("B"),
            Name = name,
            Source = Source,
            Origin = SettingsOrigin.Generated,
            Commandline = executable,
            StartingDirectory = environment.UserProfile,
            Icon = Icon,
        };
    }
}
