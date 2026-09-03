namespace Devolutions.Terminal.Settings;

public sealed class LinuxShellProfileGenerator(
    DynamicProfileEnvironment environment,
    string? source = null)
    : IDynamicProfileGenerator
{
    public string Source { get; } = string.IsNullOrWhiteSpace(source)
        ? DynamicProfileSource.Linux
        : source;
    public string DisplayName =>
        string.Equals(Source, DynamicProfileSource.MacOS, StringComparison.Ordinal)
            ? "macOS shells"
            : "Linux shells";
    public string Icon => "ms-appx:///ProfileIcons/terminal.png";

    public ValueTask<DynamicProfileGeneratorResult> GenerateAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var candidates = new List<string>();
        Add(environment.Shell);
        foreach (var shell in new[] { "zsh", "bash", "fish", "pwsh", "sh" })
        {
            Add(environment.ResolveExecutable(shell));
        }

        if (environment.IsMacOS)
        {
            Add("/bin/zsh");
            Add("/bin/bash");
            Add("/bin/sh");
        }

        var profiles = candidates
            .Distinct(StringComparer.Ordinal)
            .DistinctBy(
                static executable => Path.GetFileName(executable),
                StringComparer.OrdinalIgnoreCase)
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
