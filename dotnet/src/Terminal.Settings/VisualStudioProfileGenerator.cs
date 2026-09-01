using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Microsoft.Terminal.Settings;

public sealed class VisualStudioProfileGenerator : IDynamicProfileGenerator
{
    private readonly IDynamicProfileCommandRunner _runner;
    private readonly DynamicProfileEnvironment _environment;
    private readonly TimeSpan _timeout;
    private readonly string? _vswherePath;

    public VisualStudioProfileGenerator(
        IDynamicProfileCommandRunner? runner = null,
        DynamicProfileEnvironment? environment = null,
        TimeSpan? timeout = null,
        string? vswherePath = null)
    {
        _runner = runner ?? new DynamicProfileCommandRunner();
        _environment = environment ?? new DynamicProfileEnvironment();
        _timeout = timeout ?? TimeSpan.FromSeconds(10);
        _vswherePath = vswherePath;
    }

    public string Source => DynamicProfileSource.VisualStudio;
    public string DisplayName => "Visual Studio";
    public string Icon => "ms-appx:///ProfileGeneratorIcons/VisualStudio.png";

    public async ValueTask<DynamicProfileGeneratorResult> GenerateAsync(CancellationToken cancellationToken)
    {
        var vswhere = FindVswhere();
        if (vswhere is null)
        {
            return DynamicProfileGeneratorResult.Empty;
        }

        var result = await _runner.RunAsync(
            new DynamicProfileCommand(
                vswhere,
                ["-all", "-products", "*", "-prerelease", "-format", "json", "-utf8"],
                _timeout),
            cancellationToken).ConfigureAwait(false);
        if (result.TimedOut)
        {
            return Failure("DynamicProfileCommandTimedOut", $"'{vswhere}' timed out.");
        }

        if (result.ExitCode != 0)
        {
            return Failure(
                "DynamicProfileCommandFailed",
                $"'{vswhere}' exited with code {result.ExitCode}: {result.StandardError.Trim()}");
        }

        IReadOnlyList<VisualStudioInstance> instances;
        try
        {
            instances = ParseInstances(result.StandardOutput);
        }
        catch (JsonException ex)
        {
            return Failure("VisualStudioDiscoveryInvalidJson", $"vswhere returned invalid JSON: {ex.Message}");
        }

        var profiles = new List<ProfileSettings>();
        var hidden = false;
        foreach (var instance in instances
                     .OrderByDescending(item => item.Version)
                     .ThenByDescending(item => item.InstallDate)
                     .ThenBy(item => item.InstanceId, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var devCmd = Path.Combine(instance.InstallationPath, "Common7", "Tools", "VsDevCmd.bat");
            if (_environment.FileExists(devCmd))
            {
                profiles.Add(CreateDevCmd(instance, devCmd, hidden));
            }

            var moduleRelativePath = instance.Version >= new Version(16, 3)
                ? Path.Combine("Common7", "Tools", "Microsoft.VisualStudio.DevShell.dll")
                : Path.Combine("Common7", "Tools", "vsdevshell", "Microsoft.VisualStudio.DevShell.dll");
            var module = Path.Combine(instance.InstallationPath, moduleRelativePath);
            if (instance.Version >= new Version(16, 2) && _environment.FileExists(module))
            {
                profiles.Add(CreateDevShell(instance, module, hidden));
            }

            hidden = true;
        }

        return new DynamicProfileGeneratorResult(profiles, []);
    }

    public static IReadOnlyList<VisualStudioInstance> ParseInstances(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("The vswhere root value must be an array.");
        }

        var result = new List<VisualStudioInstance>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            var instanceId = String(item, "instanceId");
            var path = String(item, "installationPath");
            var versionText = String(item, "installationVersion");
            if (string.IsNullOrWhiteSpace(instanceId) ||
                string.IsNullOrWhiteSpace(path) ||
                !Version.TryParse(versionText, out var version))
            {
                continue;
            }

            var suffix = version.Major.ToString(CultureInfo.InvariantCulture);
            if (item.TryGetProperty("catalog", out var catalog) &&
                !string.IsNullOrWhiteSpace(String(catalog, "productLineVersion")))
            {
                suffix = String(catalog, "productLineVersion")!;
            }

            string? nickname = null;
            string? channelId = null;
            if (item.TryGetProperty("properties", out var properties))
            {
                nickname = String(properties, "nickname");
                channelId = String(properties, "channelId");
            }

            if (!string.IsNullOrWhiteSpace(nickname))
            {
                suffix += $" ({nickname})";
            }
            else if (!string.IsNullOrWhiteSpace(channelId))
            {
                var channel = channelId[(channelId.LastIndexOf('.') + 1)..];
                if (!channel.Equals("Release", StringComparison.OrdinalIgnoreCase))
                {
                    suffix += $" [{channel}]";
                }
            }

            var installDate = DateTimeOffset.MinValue;
            if (DateTimeOffset.TryParse(
                    String(item, "installDate"),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal,
                    out var parsedDate))
            {
                installDate = parsedDate;
            }

            result.Add(new VisualStudioInstance(instanceId, path, version, installDate, suffix));
        }

        return result;
    }

    private ProfileSettings CreateDevCmd(VisualStudioInstance instance, string script, bool hidden) => new()
    {
        Guid = ProfileGuid.CreateDynamic($"VsDevCmd{instance.InstanceId}").ToString("B"),
        Name = $"Developer Command Prompt for VS {instance.ProfileNameSuffix}",
        Source = Source,
        Origin = SettingsOrigin.Generated,
        Commandline = $"cmd.exe /k \"{script}\" -startdir=none{ArchitectureArguments(instance.Version)}",
        StartingDirectory = instance.InstallationPath,
        Icon = "ms-appx:///ProfileIcons/vs-cmd.png",
        Hidden = hidden,
    };

    private ProfileSettings CreateDevShell(VisualStudioInstance instance, string module, bool hidden)
    {
        var pwsh = _environment.ResolveExecutable("pwsh.exe");
        var shell = pwsh is null ? "powershell.exe" : "pwsh.exe";
        var command = $"{shell} -NoExit -Command \"&{{Import-Module \\\"{module}\\\"; " +
                      $"Enter-VsDevShell {instance.InstanceId} -SkipAutomaticLocation" +
                      $"{DevShellArchitectureArguments(instance.Version)}}}\"";
        return new ProfileSettings
        {
            Guid = ProfileGuid.CreateDynamic($"VsDevShell{instance.InstanceId}").ToString("B"),
            Name = $"Developer PowerShell for VS {instance.ProfileNameSuffix}",
            Source = Source,
            Origin = SettingsOrigin.Generated,
            Commandline = command,
            StartingDirectory = instance.InstallationPath,
            Icon = pwsh is null
                ? "ms-appx:///ProfileIcons/vs-powershell.png"
                : "ms-appx:///ProfileIcons/vs-pwsh.png",
            Hidden = hidden,
        };
    }

    private string? FindVswhere()
    {
        var candidates = new[]
        {
            _vswherePath,
            Path.Combine(_environment.ProgramFilesX86, "Microsoft Visual Studio", "Installer", "vswhere.exe"),
            _environment.ResolveExecutable("vswhere.exe"),
        };
        return candidates.FirstOrDefault(path => path is not null && _environment.FileExists(path));
    }

    private string ArchitectureArguments(Version version) => _environment.ProcessArchitecture switch
    {
        Architecture.Arm64 when version >= new Version(17, 4) => " -arch=arm64 -host_arch=arm64",
        Architecture.Arm64 => " -arch=arm64 -host_arch=x64",
        Architecture.X64 => " -arch=x64 -host_arch=x64",
        _ => string.Empty,
    };

    private string DevShellArchitectureArguments(Version version) => _environment.ProcessArchitecture switch
    {
        Architecture.Arm64 when version >= new Version(17, 4) =>
            " -DevCmdArguments \\\"-arch=arm64 -host_arch=arm64\\\"",
        Architecture.Arm64 => " -DevCmdArguments \\\"-arch=arm64 -host_arch=x64\\\"",
        Architecture.X64 => " -DevCmdArguments \\\"-arch=x64 -host_arch=x64\\\"",
        _ => string.Empty,
    };

    private DynamicProfileGeneratorResult Failure(string code, string message) => new(
        [],
        [new SettingsDiagnostic(SettingsDiagnosticSeverity.Warning, code, message, Source)]);

    private static string? String(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

public sealed record VisualStudioInstance(
    string InstanceId,
    string InstallationPath,
    Version Version,
    DateTimeOffset InstallDate,
    string ProfileNameSuffix);
