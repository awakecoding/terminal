namespace Devolutions.Terminal.Settings;

public sealed class SshHostProfileGenerator : IDynamicProfileGenerator
{
    private readonly DynamicProfileEnvironment _environment;
    private readonly IReadOnlyList<string>? _configPaths;

    public SshHostProfileGenerator(
        DynamicProfileEnvironment? environment = null,
        IReadOnlyList<string>? configPaths = null)
    {
        _environment = environment ?? new DynamicProfileEnvironment();
        _configPaths = configPaths;
    }

    public string Source => DynamicProfileSource.Ssh;
    public string DisplayName => "OpenSSH";
    public string Icon => "\uE969";

    public ValueTask<DynamicProfileGeneratorResult> GenerateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ssh = FindSsh();
        if (ssh is null)
        {
            return ValueTask.FromResult(DynamicProfileGeneratorResult.Empty);
        }

        var diagnostics = new List<SettingsDiagnostic>();
        var hosts = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in ConfigPaths())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_environment.FileExists(path))
            {
                continue;
            }

            try
            {
                ParseHosts(_environment.ReadLines(path), hosts);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                diagnostics.Add(new SettingsDiagnostic(
                    SettingsDiagnosticSeverity.Warning,
                    "SshConfigReadFailed",
                    $"Could not read SSH config '{path}': {ex.Message}",
                    path));
            }
        }

        var profiles = hosts.Select(host => new ProfileSettings
        {
            Guid = ProfileGuid.CreateDynamic($"SSH - {host}").ToString("B"),
            Name = $"SSH - {host}",
            Source = Source,
            Origin = SettingsOrigin.Generated,
            Commandline = $"\"{ssh}\" {QuoteArgument(host)}",
            StartingDirectory = "%USERPROFILE%",
            Icon = "\uE977",
        }).ToArray();
        return ValueTask.FromResult(new DynamicProfileGeneratorResult(profiles, diagnostics));
    }

    public static IReadOnlyList<string> ParseHostNames(IEnumerable<string> lines)
    {
        var hosts = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        ParseHosts(lines, hosts);
        return hosts.ToArray();
    }

    private static void ParseHosts(IEnumerable<string> lines, ISet<string> hosts)
    {
        string[] pendingHosts = [];
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var comment = line.IndexOf(" #", StringComparison.Ordinal);
            if (comment >= 0)
            {
                line = line[..comment].TrimEnd();
            }

            var separator = line.IndexOfAny([' ', '\t']);
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator];
            var value = line[(separator + 1)..].Trim();
            if (key.Equals("Host", StringComparison.OrdinalIgnoreCase))
            {
                pendingHosts = value
                    .Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(host => host.IndexOfAny(['*', '?', '!']) < 0)
                    .ToArray();
            }
            else if (key.Equals("HostName", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var host in pendingHosts)
                {
                    hosts.Add(host);
                }

                pendingHosts = [];
            }
        }
    }

    private string? FindSsh()
    {
        var candidates = new[]
        {
            Path.Combine(_environment.SystemDirectory, "OpenSSH", "ssh.exe"),
            Path.Combine(_environment.ProgramFiles, "OpenSSH", "ssh.exe"),
            Path.Combine(_environment.ProgramFilesX86, "OpenSSH", "ssh.exe"),
            _environment.ResolveExecutable("ssh.exe"),
        };
        return candidates.FirstOrDefault(path => path is not null && _environment.FileExists(path));
    }

    private IEnumerable<string> ConfigPaths() => _configPaths ??
    [
        Path.Combine(_environment.ProgramData, "ssh", "ssh_config"),
        Path.Combine(_environment.UserProfile, ".ssh", "config"),
    ];

    private static string QuoteArgument(string value) =>
        value.Any(char.IsWhiteSpace) ? $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"" : value;
}
