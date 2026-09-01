using System.Text.Json.Nodes;

namespace Microsoft.Terminal.Settings;

public static class DynamicProfileSource
{
    public const string Inbox = "Windows.Terminal.Inbox";
    public const string PowerShellCore = "Windows.Terminal.PowershellCore";
    public const string Wsl = "Windows.Terminal.Wsl";
    public const string Ssh = "Windows.Terminal.SSH";
    public const string VisualStudio = "Windows.Terminal.VisualStudio";
    public const string Azure = "Windows.Terminal.Azure";
}

public interface IDynamicProfileGenerator
{
    string Source { get; }
    string DisplayName { get; }
    string Icon { get; }
    ValueTask<DynamicProfileGeneratorResult> GenerateAsync(CancellationToken cancellationToken);
}

public sealed record DynamicProfileGeneratorResult(
    IReadOnlyList<ProfileSettings> Profiles,
    IReadOnlyList<SettingsDiagnostic> Diagnostics)
{
    public static DynamicProfileGeneratorResult Empty { get; } = new([], []);
}

public sealed class DynamicProfileGenerationResult
{
    internal DynamicProfileGenerationResult(
        IReadOnlyList<ProfileSettings> profiles,
        IReadOnlyList<SettingsDiagnostic> diagnostics,
        IReadOnlySet<Guid> generatedProfileIds,
        IReadOnlySet<Guid> orphanedProfileIds)
    {
        Profiles = profiles;
        Diagnostics = diagnostics;
        GeneratedProfileIds = generatedProfileIds;
        OrphanedProfileIds = orphanedProfileIds;
    }

    public IReadOnlyList<ProfileSettings> Profiles { get; }
    public IReadOnlyList<SettingsDiagnostic> Diagnostics { get; }
    public IReadOnlySet<Guid> GeneratedProfileIds { get; }
    public IReadOnlySet<Guid> OrphanedProfileIds { get; }

    public SettingsLayer ToSettingsLayer()
    {
        var profiles = new JsonArray();
        foreach (var profile in Profiles)
        {
            var node = new JsonObject
            {
                ["guid"] = profile.Guid,
                ["name"] = profile.Name,
                ["commandline"] = profile.Commandline,
                ["startingDirectory"] = profile.StartingDirectory,
                ["hidden"] = profile.Hidden,
                ["colorScheme"] = profile.ColorScheme,
                ["$terminalOrigin"] = (
                    profile.Origin == SettingsOrigin.Inbox
                        ? SettingsOrigin.Inbox
                        : SettingsOrigin.Generated).ToString(),
            };
            Add(node, "source", profile.Source);
            Add(node, "icon", profile.Icon);
            Add(node, "connectionType", profile.ConnectionType);
            if (!string.Equals(profile.PathTranslationStyle, "none", StringComparison.Ordinal))
            {
                node["pathTranslationStyle"] = profile.PathTranslationStyle;
            }

            profiles.Add((JsonNode)node);
        }

        var document = new JsonObject
        {
            ["profiles"] = new JsonObject
            {
                ["list"] = profiles,
            },
        };
        return new SettingsLayer("dynamic-profiles", document.ToJsonString(), SettingsLayerKind.Generated);
    }

    public void Reconcile(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        foreach (var profile in settings.Profiles)
        {
            profile.Orphaned = Guid.TryParse(profile.Guid, out var id) && OrphanedProfileIds.Contains(id);
            if (!profile.Orphaned)
            {
                continue;
            }

            profile.Hidden = true;
            settings.Diagnostics.Add(new SettingsDiagnostic(
                SettingsDiagnosticSeverity.Warning,
                "OrphanedGeneratedProfile",
                $"Generated profile '{profile.Name}' is no longer available and has been hidden.",
                profile.Source));
            MarkSnapshotHidden(settings.ResolvedSnapshot, id);
        }
    }

    public void UpdateState(ApplicationStateData state)
    {
        ArgumentNullException.ThrowIfNull(state);
        state.GeneratedProfiles.UnionWith(GeneratedProfileIds);
    }

    private static void MarkSnapshotHidden(JsonObject? snapshot, Guid id)
    {
        if (snapshot?["profiles"] is not JsonObject profiles ||
            profiles["list"] is not JsonArray list)
        {
            return;
        }

        var profile = list
            .OfType<JsonObject>()
            .FirstOrDefault(item =>
                Guid.TryParse(item["guid"]?.GetValue<string>(), out var candidate) &&
                candidate == id);
        if (profile is not null)
        {
            profile["hidden"] = true;
        }
    }

    private static void Add(JsonObject target, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            target[key] = value;
        }
    }
}

public sealed class DynamicProfileManager
{
    private readonly IReadOnlyList<IDynamicProfileGenerator> _generators;

    public DynamicProfileManager(IEnumerable<IDynamicProfileGenerator> generators)
    {
        ArgumentNullException.ThrowIfNull(generators);
        _generators = generators.ToArray();
    }

    public static DynamicProfileManager CreateDefault(
        DynamicProfileEnvironment? environment = null,
        IDynamicProfileCommandRunner? commandRunner = null) =>
        CreateDefaultCore(environment, commandRunner, null);

    public static DynamicProfileManager CreateDefaultWithAzure(
        Guid azureConnectionType,
        DynamicProfileEnvironment? environment = null,
        IDynamicProfileCommandRunner? commandRunner = null) =>
        CreateDefaultCore(environment, commandRunner, azureConnectionType);

    private static DynamicProfileManager CreateDefaultCore(
        DynamicProfileEnvironment? environment,
        IDynamicProfileCommandRunner? commandRunner,
        Guid? azureConnectionType)
    {
        environment ??= new DynamicProfileEnvironment();
        commandRunner ??= new DynamicProfileCommandRunner();
        var generators = new List<IDynamicProfileGenerator>
        {
            new InboxShellProfileGenerator(environment),
            new PowerShellCoreProfileGenerator(environment),
            new WslDistroProfileGenerator(commandRunner, environment),
            new SshHostProfileGenerator(environment),
            new VisualStudioProfileGenerator(commandRunner, environment),
        };
        if (azureConnectionType is not null)
        {
            generators.Add(new AzureCloudShellProfileGenerator(azureConnectionType));
        }

        return new DynamicProfileManager(generators);
    }

    public async ValueTask<DynamicProfileGenerationResult> GenerateAsync(
        IEnumerable<string>? disabledSources = null,
        IEnumerable<Guid>? previousGeneratedProfileIds = null,
        CancellationToken cancellationToken = default)
    {
        var disabled = new HashSet<string>(
            disabledSources ?? [],
            StringComparer.OrdinalIgnoreCase);
        var profiles = new List<ProfileSettings>();
        var diagnostics = new List<SettingsDiagnostic>();
        var ids = new HashSet<Guid>();

        foreach (var generator in _generators)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (disabled.Contains(generator.Source))
            {
                diagnostics.Add(new SettingsDiagnostic(
                    SettingsDiagnosticSeverity.Info,
                    "DynamicProfileSourceDisabled",
                    $"Dynamic profile source '{generator.Source}' is disabled.",
                    generator.Source));
                continue;
            }

            DynamicProfileGeneratorResult result;
            try
            {
                result = await generator.GenerateAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (
                ex is IOException or
                UnauthorizedAccessException or
                InvalidOperationException or
                System.ComponentModel.Win32Exception)
            {
                diagnostics.Add(new SettingsDiagnostic(
                    SettingsDiagnosticSeverity.Warning,
                    "DynamicProfileGenerationFailed",
                    $"{generator.DisplayName} profile generation failed: {ex.Message}",
                    generator.Source));
                continue;
            }

            diagnostics.AddRange(result.Diagnostics);
            foreach (var profile in result.Profiles)
            {
                if (!Guid.TryParse(profile.Guid, out var id))
                {
                    diagnostics.Add(new SettingsDiagnostic(
                        SettingsDiagnosticSeverity.Warning,
                        "InvalidGeneratedProfileGuid",
                        $"Generated profile '{profile.Name}' has an invalid GUID and was ignored.",
                        generator.Source));
                    continue;
                }

                if (!ids.Add(id))
                {
                    diagnostics.Add(new SettingsDiagnostic(
                        SettingsDiagnosticSeverity.Warning,
                        "DuplicateGeneratedProfile",
                        $"Generated profile '{profile.Name}' duplicates GUID '{id:B}' and was ignored.",
                        generator.Source));
                    continue;
                }

                profiles.Add(profile);
            }
        }

        var orphaned = new HashSet<Guid>(previousGeneratedProfileIds ?? []);
        orphaned.ExceptWith(ids);
        return new DynamicProfileGenerationResult(profiles, diagnostics, ids, orphaned);
    }
}

public sealed record DynamicSettingsLoadResult(
    AppSettings Settings,
    DynamicProfileGenerationResult Generation);

public static class DynamicSettingsLoader
{
    public static async ValueTask<DynamicSettingsLoadResult> LoadAsync(
        string defaultsJson,
        string? userJson,
        IEnumerable<SettingsLayer>? fragments,
        DynamicProfileManager manager,
        IEnumerable<Guid>? previousGeneratedProfileIds = null,
        string userSource = "settings.json",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manager);
        var fragmentArray = fragments?.ToArray() ?? [];
        var initial = SettingsLoader.Load(defaultsJson, userJson, fragmentArray, userSource);
        var generation = await manager.GenerateAsync(
            initial.DisabledProfileSources,
            previousGeneratedProfileIds,
            cancellationToken).ConfigureAwait(false);
        var layers = new SettingsLayer[fragmentArray.Length + 1];
        layers[0] = generation.ToSettingsLayer();
        fragmentArray.CopyTo(layers, 1);
        var settings = SettingsLoader.Load(defaultsJson, userJson, layers, userSource);
        settings.Diagnostics.AddRange(generation.Diagnostics);
        generation.Reconcile(settings);
        if (userJson is null)
        {
            var preferredPowerShell = settings.Profiles.FirstOrDefault(static profile =>
                profile.Source == DynamicProfileSource.PowerShellCore &&
                profile.Name.Equals("PowerShell", StringComparison.Ordinal));
            if (preferredPowerShell?.Guid is { Length: > 0 } preferredGuid)
            {
                settings.DefaultProfile = preferredGuid;
            }
        }

        return new DynamicSettingsLoadResult(settings, generation);
    }
}
