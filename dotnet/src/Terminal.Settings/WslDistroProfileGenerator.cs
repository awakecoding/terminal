using System.Text;

namespace Microsoft.Terminal.Settings;

public sealed class WslDistroProfileGenerator : IDynamicProfileGenerator
{
    private const string ProfileIcon =
        "ms-appx:///ProfileIcons/{9acb9455-ca41-5af7-950f-6bca1bc9722f}.png";
    private readonly IDynamicProfileCommandRunner _runner;
    private readonly DynamicProfileEnvironment _environment;
    private readonly TimeSpan _timeout;

    public WslDistroProfileGenerator(
        IDynamicProfileCommandRunner? runner = null,
        DynamicProfileEnvironment? environment = null,
        TimeSpan? timeout = null)
    {
        _runner = runner ?? new DynamicProfileCommandRunner();
        _environment = environment ?? new DynamicProfileEnvironment();
        _timeout = timeout ?? TimeSpan.FromSeconds(5);
    }

    public string Source => DynamicProfileSource.Wsl;
    public string DisplayName => "Windows Subsystem for Linux";
    public string Icon => "ms-appx:///ProfileGeneratorIcons/WSL.png";

    public async ValueTask<DynamicProfileGeneratorResult> GenerateAsync(CancellationToken cancellationToken)
    {
        var executable = Path.Combine(_environment.SystemDirectory, "wsl.exe");
        if (!_environment.FileExists(executable))
        {
            return DynamicProfileGeneratorResult.Empty;
        }

        var result = await _runner.RunAsync(
            new DynamicProfileCommand(executable, ["--list", "--quiet"], _timeout, Encoding.Unicode),
            cancellationToken).ConfigureAwait(false);
        if (result.TimedOut)
        {
            return Failure("DynamicProfileCommandTimedOut", $"'{executable} --list --quiet' timed out.");
        }

        if (result.ExitCode != 0)
        {
            return Failure(
                "DynamicProfileCommandFailed",
                $"'{executable} --list --quiet' exited with code {result.ExitCode}: {result.StandardError.Trim()}");
        }

        var profiles = result.StandardOutput
            .Split(['\r', '\n', '\0'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(name =>
                !name.StartsWith("docker-desktop", StringComparison.OrdinalIgnoreCase) &&
                !name.StartsWith("rancher-desktop", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(CreateProfile)
            .ToArray();
        return new DynamicProfileGeneratorResult(profiles, []);
    }

    private ProfileSettings CreateProfile(string name) => new()
    {
        Guid = ProfileGuid.CreateDynamic(name).ToString("B"),
        Name = name,
        Source = Source,
        Origin = SettingsOrigin.Generated,
        Commandline = $"\"{Path.Combine(_environment.SystemDirectory, "wsl.exe")}\" -d \"{name}\"",
        StartingDirectory = "~",
        Icon = ProfileIcon,
        ColorScheme = "Campbell",
        PathTranslationStyle = "wsl",
    };

    private DynamicProfileGeneratorResult Failure(string code, string message) => new(
        [],
        [new SettingsDiagnostic(SettingsDiagnosticSeverity.Warning, code, message, Source)]);
}
