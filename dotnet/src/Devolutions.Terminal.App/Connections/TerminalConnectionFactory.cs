using Devolutions.Terminal.Connection;
using Devolutions.Terminal.Settings;

namespace Devolutions.Terminal.App.Connections;

public sealed class TerminalConnectionFactory
{
    public const string AzureClientIdEnvironmentVariable = "WT_AZURE_CLIENT_ID";
    public const string AzureEnvironmentVariable = "WT_AZURE_ENVIRONMENT";

    private static readonly HttpClient AzureHttpClient = new();
    private readonly AzureCloudShellAuthenticationCallbacks _azureCallbacks;
    private readonly IAzureCloudShellTokenCache? _tokenCache;

    public TerminalConnectionFactory(
        AzureCloudShellAuthenticationCallbacks azureCallbacks,
        IAzureCloudShellTokenCache? tokenCache = null)
    {
        _azureCallbacks = azureCallbacks ?? throw new ArgumentNullException(nameof(azureCallbacks));
        _tokenCache = tokenCache;
    }

    public static bool IsAzureConfigured =>
        Guid.TryParse(
            Environment.GetEnvironmentVariable(AzureClientIdEnvironmentVariable),
            out var clientId) &&
        clientId != Guid.Empty;

    public IRestartableTerminalConnection Create(ProfileSettings profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!Guid.TryParse(profile.ConnectionType, out var connectionType) ||
            connectionType != AzureCloudShellConnection.ConnectionTypeGuid)
        {
            return CreateLocalConnection();
        }

        var clientIdValue = Environment.GetEnvironmentVariable(AzureClientIdEnvironmentVariable);
        if (!Guid.TryParse(clientIdValue, out var clientId) || clientId == Guid.Empty)
        {
            throw new InvalidOperationException(
                $"Azure Cloud Shell requires a public-client application GUID in {AzureClientIdEnvironmentVariable}.");
        }

        var environment = Environment.GetEnvironmentVariable(AzureEnvironmentVariable)
            ?.ToLowerInvariant() switch
        {
            "usgovernment" or "azureusgovernment" => AzureCloudShellEnvironment.UsGovernment,
            _ => AzureCloudShellEnvironment.Public,
        };
        var options = new AzureCloudShellOptions
        {
            ClientId = clientId,
            Environment = environment,
        };
        return new AzureCloudShellConnection(
            new AzureDeviceCodeAuthenticator(AzureHttpClient, options, _tokenCache),
            new AzureCloudShellService(AzureHttpClient, options),
            new AzureCloudShellWebSocketFactory(),
            _azureCallbacks,
            options);
    }

    private static IRestartableTerminalConnection CreateLocalConnection()
    {
        if (OperatingSystem.IsWindows())
        {
            return new ConPtyConnection();
        }

        if (OperatingSystem.IsLinux())
        {
            return CreateUnixPtyConnection();
        }

        if (OperatingSystem.IsMacOS())
        {
            return CreateUnixPtyConnection();
        }

        throw new PlatformNotSupportedException(
            "Local terminal sessions require Windows ConPTY or a Unix PTY host.");
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private static LinuxPtyConnection CreateUnixPtyConnection() => new();
}
