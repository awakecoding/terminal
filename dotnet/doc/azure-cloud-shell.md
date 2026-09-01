# Azure Cloud Shell connection

`Terminal.Connection` contains a NativeAOT-safe Azure Cloud Shell client built on
`HttpClient`, `ClientWebSocket`, and source-generated `System.Text.Json`
metadata. It has no dependency on the Azure SDK or MSAL.

## Host integration

The host must register its own Microsoft Entra public-client application. The
client ID is configuration, not a client secret, and is deliberately not
embedded in `Terminal.Connection`.

```csharp
var options = new AzureCloudShellOptions
{
    ClientId = configuredPublicClientId,
    Environment = AzureCloudShellEnvironment.Public,
};
var httpClient = new HttpClient();
var callbacks = new AzureCloudShellAuthenticationCallbacks
{
    ShowDeviceCodeAsync = (prompt, cancellationToken) =>
        authenticationUi.ShowDeviceCodeAsync(
            prompt.Message,
            prompt.UserCode,
            prompt.VerificationUri,
            prompt.ExpiresAt,
            cancellationToken),
    SelectTenantAsync = (tenants, cancellationToken) =>
        authenticationUi.SelectTenantAsync(tenants, cancellationToken),
};
var authenticator = new AzureDeviceCodeAuthenticator(
    httpClient,
    options,
    secureTokenCache);
var service = new AzureCloudShellService(httpClient, options);
var connection = new AzureCloudShellConnection(
    authenticator,
    service,
    new AzureCloudShellWebSocketFactory(),
    callbacks,
    options);
```

Enable device-code/public-client flow on the app registration and grant the
delegated Azure Resource Manager permission needed by the user. The connection
uses the native Windows Terminal v1 OAuth contract:

- `POST {authority}/{tenant}/oauth2/devicecode` with `client_id` and the ARM
  `resource`
- UI receives `message`, `user_code`, `verification_uri`, and expiration;
  `device_code`, access tokens, and refresh tokens never enter the UI callback
- polling uses the legacy v1 `grant_type=device_code` contract and
  handles `authorization_pending`, `slow_down`, cancellation, decline, and
  expiry
- accessible ARM tenants are enumerated; one tenant is automatic, while
  multiple tenants require `SelectTenantAsync`
- the selected tenant's token is acquired with the refresh token

`IAzureCloudShellTokenCache` is optional. Without it, tokens remain in memory
and the device flow runs for each new authenticator. A host that persists tokens
must use an OS-backed secret store and must never serialize credentials into
settings or logs.

Pass `AzureCloudShellConnection.ConnectionTypeGuid` (`D9FCFDFA-A479-412C-83B7-C5640E61CD62`)
to `AzureCloudShellProfileGenerator` when composing dynamic profile generators.

## Service and terminal protocol

The service reads `preferredShellType`, provisions the Linux Cloud Shell
console, creates a terminal with its initial rows and columns, and connects to
the returned direct or Azure Relay WebSocket. Input is one UTF-8 text message
per write. Text and binary WebSocket payloads are forwarded as raw incremental
UTF-8 bytes. Resize uses a coalescing asynchronous worker for the Cloud Shell terminal size
endpoint so Avalonia layout never waits on network or authentication.

`ClientWebSocket` transport PING/PONG keepalive is configured through
`WebSocketKeepAliveInterval` and `WebSocketKeepAliveTimeout`. Unexpected socket
failures retry the same provisioned terminal URI up to
`MaximumReconnectAttempts`; explicit close, cancellation, and normal remote
close do not reconnect. `RestartAsync` provisions a new terminal without
replacing the terminal control.

Subscribe to `DiagnosticEmitted` for stage, stable code, severity, request ID,
and message. `LastFault`, `ServiceMetadata`, and `LastServiceExit` retain HTTP,
Azure error, WebSocket close, tenant, shell, terminal, and reconnect metadata.
Tokens are excluded. Azure does not report a process ID or shell exit code over
this protocol, so process ID is `0`, a normal remote WebSocket close maps to
exit code `0`, and connection failures have no exit code.

## Current service limitations

- Cloud Shell management and terminal endpoints are preview/undocumented
  service contracts mirrored from native Windows Terminal and may change.
- Public Azure and Azure US Government endpoint presets are available. Other
  sovereign clouds require an explicit `AzureCloudShellEnvironment`.
- Reconnect targets the existing terminal. If Azure has destroyed it, the
  connection fails after the retry budget; use `RestartAsync` to reprovision.
- Resize updates local dimensions synchronously, coalesces pending service
  updates, and reports failures through warning diagnostics without stopping
  the session.
- The connection layer does not launch a browser, render auth UI, select a
  tenant, register the dynamic profile, or choose a persistent token store.
  Those remain explicit host composition responsibilities.
- No live Azure test runs by default. Protocol and lifecycle tests use
  deterministic HTTP/WebSocket mocks and require no credentials.
