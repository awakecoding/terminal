namespace Devolutions.Terminal.Settings;

public sealed class AzureCloudShellProfileGenerator : IDynamicProfileGenerator
{
    private readonly Guid? _connectionType;

    public AzureCloudShellProfileGenerator(Guid? connectionType = null)
    {
        _connectionType = connectionType;
    }

    public string Source => DynamicProfileSource.Azure;
    public string DisplayName => "Azure Cloud Shell";
    public string Icon => "ms-appx:///ProfileGeneratorIcons/AzureCloudShell.png";

    public ValueTask<DynamicProfileGeneratorResult> GenerateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_connectionType is null)
        {
            return ValueTask.FromResult(new DynamicProfileGeneratorResult(
                [],
                [new SettingsDiagnostic(
                    SettingsDiagnosticSeverity.Info,
                    "AzureCloudShellUnavailable",
                    "Azure Cloud Shell profile generation is available when an Azure connection type is registered.",
                    Source)]));
        }

        ProfileSettings profile = new()
        {
            Guid = ProfileGuid.CreateDynamic("Azure Cloud Shell").ToString("B"),
            Name = "Azure Cloud Shell",
            Source = Source,
            Origin = SettingsOrigin.Generated,
            Commandline = string.Empty,
            StartingDirectory = "%USERPROFILE%",
            ConnectionType = _connectionType.Value.ToString("B"),
            ColorScheme = "Vintage",
        };
        return ValueTask.FromResult(new DynamicProfileGeneratorResult([profile], []));
    }
}
