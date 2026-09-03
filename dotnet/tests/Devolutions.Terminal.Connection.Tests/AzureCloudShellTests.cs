using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using Devolutions.Terminal.Connection;
using Xunit;

namespace Devolutions.Terminal.Connection.Tests;

public sealed class AzureCloudShellTests
{
    private static readonly AzureCloudShellOptions DefaultOptions = new()
    {
        ClientId = new Guid("11111111-2222-3333-4444-555555555555"),
        MaximumReconnectAttempts = 2,
        ReconnectDelay = TimeSpan.Zero,
    };

    [Fact]
    public void ExposesNativeAzureConnectionType()
    {
        Assert.Equal(
            new Guid("D9FCFDFA-A479-412C-83B7-C5640E61CD62"),
            AzureCloudShellConnection.ConnectionTypeGuid);
    }

    [Fact]
    public void CredentialStringDoesNotExposeTokens()
    {
        var credential = Credential();

        var text = credential.ToString();

        Assert.DoesNotContain(credential.AccessToken, text, StringComparison.Ordinal);
        Assert.DoesNotContain(credential.RefreshToken!, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartsWithPreferredShellAndPublishesMetadata()
    {
        var fixture = new ConnectionFixture();
        var diagnostics = new List<AzureCloudShellDiagnostic>();
        fixture.Connection.DiagnosticEmitted += (_, diagnostic) => diagnostics.Add(diagnostic);
        await using var connection = fixture.Connection;

        await connection.StartAsync(string.Empty, null, 132, 43);

        Assert.True(connection.IsRunning);
        Assert.Equal(TerminalConnectionState.Connected, connection.State);
        Assert.Equal(132, connection.Columns);
        Assert.Equal(43, connection.Rows);
        Assert.Equal("pwsh", connection.ServiceMetadata?.ShellType);
        Assert.Equal("tenant-1", connection.ServiceMetadata?.TenantId);
        Assert.Equal("terminal-1", connection.ServiceMetadata?.TerminalId);
        Assert.Equal(0, connection.ProcessMetadata?.ProcessId);
        Assert.Equal((132, 43, "pwsh"), fixture.Service.ProvisionCalls.Single());
        Assert.Equal(DefaultOptions.WebSocketKeepAliveInterval, fixture.Factory.KeepAliveInterval);
        Assert.Equal(DefaultOptions.WebSocketKeepAliveTimeout, fixture.Factory.KeepAliveTimeout);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "TerminalConnected");
    }

    [Fact]
    public async Task SendsUtf8TextAndPublishesIncrementalOutput()
    {
        var socket = new MockWebSocket();
        var fixture = new ConnectionFixture([socket]);
        var output = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Connection.OutputReceived += (_, bytes) => output.TrySetResult(bytes.ToArray());
        await using var connection = fixture.Connection;
        await connection.StartAsync(string.Empty, null, 80, 24);

        await connection.WriteAsync(Encoding.UTF8.GetBytes("héllo\r"));
        socket.QueueMessage(Encoding.UTF8.GetBytes("世界"), WebSocketMessageType.Binary);

        Assert.Equal("héllo\r", Encoding.UTF8.GetString(socket.Sent.Single()));
        Assert.Equal("世界", Encoding.UTF8.GetString(
            await output.Task.WaitAsync(TimeSpan.FromSeconds(2))));
    }

    [Fact]
    public async Task ResizeUpdatesDimensionsAndCallsService()
    {
        var fixture = new ConnectionFixture();
        await using var connection = fixture.Connection;
        await connection.StartAsync(string.Empty, null, 80, 24);

        connection.Resize(160, 50);
        await WaitUntilAsync(() => fixture.Service.ResizeCalls.Count == 1);

        Assert.Equal(160, connection.Columns);
        Assert.Equal(50, connection.Rows);
        Assert.Equal((160, 50), fixture.Service.ResizeCalls.Single());
    }

    [Fact]
    public async Task ResizeFailureIsDiagnosticAndDoesNotStopSession()
    {
        var fixture = new ConnectionFixture();
        fixture.Service.ResizeException = new AzureCloudShellException(
            AzureCloudShellStage.Resize,
            "ResizeRejected",
            "resize rejected",
            HttpStatusCode.BadGateway,
            requestId: "request-7",
            isTransient: true);
        var diagnostic = new TaskCompletionSource<AzureCloudShellDiagnostic>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Connection.DiagnosticEmitted += (_, value) =>
        {
            if (value.Code == "TerminalResizeFailed")
            {
                diagnostic.TrySetResult(value);
            }
        };
        await using var connection = fixture.Connection;
        await connection.StartAsync(string.Empty, null, 80, 24);

        connection.Resize(100, 30);

        Assert.True(connection.IsRunning);
        Assert.Equal("request-7", (await diagnostic.Task).RequestId);
    }

    [Fact]
    public async Task CancellationStopsSessionWithCancelledExit()
    {
        using var cancellation = new CancellationTokenSource();
        var fixture = new ConnectionFixture();
        var exited = CaptureExit(fixture.Connection);
        await using var connection = fixture.Connection;
        await connection.StartAsync(string.Empty, null, 80, 24, cancellation.Token);

        await cancellation.CancelAsync();
        var result = await exited.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(TerminalExitReason.Cancelled, result.Reason);
        Assert.False(result.ShouldClose);
        Assert.Equal(TerminalConnectionState.Closed, connection.State);
    }

    [Fact]
    public async Task NormalRemoteClosePublishesCloseMetadata()
    {
        var socket = new MockWebSocket();
        var fixture = new ConnectionFixture([socket]);
        var exited = CaptureExit(fixture.Connection);
        await using var connection = fixture.Connection;
        await connection.StartAsync(new TerminalLaunchOptions
        {
            CommandLine = string.Empty,
            Columns = 80,
            Rows = 24,
            CloseOnExit = TerminalCloseOnExitPolicy.Graceful,
        });

        socket.QueueClose(WebSocketCloseStatus.NormalClosure, "shell ended");
        var result = await exited.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(TerminalExitReason.ProcessExited, result.Reason);
        Assert.Equal(0, result.ExitCode);
        Assert.True(result.ShouldClose);
        Assert.Equal(WebSocketCloseStatus.NormalClosure, connection.LastServiceExit?.CloseStatus);
        Assert.Equal("shell ended", connection.LastServiceExit?.CloseDescription);
    }

    [Fact]
    public async Task UnexpectedCloseReconnectsAndContinuesReceiving()
    {
        var first = new MockWebSocket();
        var second = new MockWebSocket();
        var fixture = new ConnectionFixture([first, second]);
        var output = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Connection.OutputReceived += (_, bytes) =>
            output.TrySetResult(Encoding.UTF8.GetString(bytes.Span));
        await using var connection = fixture.Connection;
        await connection.StartAsync(string.Empty, null, 80, 24);

        first.QueueClose(WebSocketCloseStatus.EndpointUnavailable, "relay moved");
        await second.Connected.Task.WaitAsync(TimeSpan.FromSeconds(2));
        second.QueueMessage("after reconnect"u8.ToArray());

        Assert.Equal("after reconnect", await output.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(connection.IsRunning);
        second.QueueClose(WebSocketCloseStatus.NormalClosure, null);
        await WaitUntilAsync(() => connection.LastServiceExit is not null);
        Assert.Equal(1, connection.LastServiceExit?.ReconnectAttempts);
    }

    [Fact]
    public async Task ReconnectExhaustionPublishesRichFault()
    {
        var first = new MockWebSocket();
        var failed1 = new MockWebSocket
        {
            ConnectException = new WebSocketException("relay unavailable"),
        };
        var failed2 = new MockWebSocket
        {
            ConnectException = new WebSocketException("relay unavailable"),
        };
        var fixture = new ConnectionFixture([first, failed1, failed2]);
        var faulted = new TaskCompletionSource<Exception>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Connection.Faulted += (_, error) => faulted.TrySetResult(error);
        await using var connection = fixture.Connection;
        await connection.StartAsync(string.Empty, null, 80, 24);

        first.QueueException(new WebSocketException("network lost"));
        var error = await faulted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var azureError = Assert.IsType<AzureCloudShellException>(error);
        Assert.Equal("ReconnectExhausted", azureError.Code);
        Assert.True(azureError.IsTransient);
        Assert.Equal(AzureCloudShellStage.Reconnect, connection.LastFault?.Stage);
        Assert.Equal(2, connection.LastServiceExit?.ReconnectAttempts);
        Assert.Equal(TerminalConnectionState.Failed, connection.State);
    }

    [Fact]
    public async Task RestartProvisionsNewTerminalAndSessionIdentity()
    {
        var fixture = new ConnectionFixture([new MockWebSocket(), new MockWebSocket()]);
        await using var connection = fixture.Connection;
        await connection.StartAsync(string.Empty, null, 80, 24);
        var firstSession = connection.ServiceMetadata?.SessionId;

        await connection.RestartAsync(new TerminalLaunchOptions
        {
            CommandLine = string.Empty,
            Columns = 100,
            Rows = 35,
        });

        Assert.NotEqual(firstSession, connection.ServiceMetadata?.SessionId);
        Assert.Equal(2, fixture.Service.ProvisionCalls.Count);
        Assert.Equal(100, connection.Columns);
        Assert.Equal(35, connection.Rows);
        Assert.True(connection.IsRunning);
    }

    [Fact]
    public async Task StartupFailureCanBeRetriedAndCarriesFaultMetadata()
    {
        var authenticator = new MockAuthenticator
        {
            Exception = new AzureCloudShellException(
                AzureCloudShellStage.Authentication,
                "ConditionalAccess",
                "Sign-in blocked.",
                HttpStatusCode.Forbidden,
                "interaction_required",
                "request-42"),
        };
        var fixture = new ConnectionFixture(authenticator: authenticator);
        var faulted = new TaskCompletionSource<Exception>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Connection.Faulted += (_, error) => faulted.TrySetResult(error);
        await using var connection = fixture.Connection;

        var thrown = await Assert.ThrowsAsync<AzureCloudShellException>(
            () => connection.StartAsync(string.Empty, null, 80, 24));

        Assert.Equal("ConditionalAccess", thrown.Code);
        Assert.Equal("request-42", connection.LastFault?.RequestId);
        Assert.Equal(TerminalExitReason.StartupFailure, connection.LastExitInfo?.Reason);
        authenticator.Exception = null;
        await connection.StartAsync(string.Empty, null, 80, 24);
        Assert.True(connection.IsRunning);
    }

    [Fact]
    public async Task CloseNeverRequestsPolicyDrivenTabClose()
    {
        var fixture = new ConnectionFixture();
        var exited = CaptureExit(fixture.Connection);
        await using var connection = fixture.Connection;
        await connection.StartAsync(new TerminalLaunchOptions
        {
            CommandLine = string.Empty,
            Columns = 80,
            Rows = 24,
            CloseOnExit = TerminalCloseOnExitPolicy.Always,
        });

        await connection.CloseAsync();
        var result = await exited.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(TerminalExitReason.Closed, result.Reason);
        Assert.False(result.ShouldClose);
    }

    [Fact]
    public async Task CloseAbortsWhenCloseOutputDoesNotComplete()
    {
        var socket = new MockWebSocket { HangCloseOutput = true };
        var options = DefaultOptions with
        {
            WebSocketCloseTimeout = TimeSpan.FromMilliseconds(25),
        };
        var fixture = new ConnectionFixture([socket], options: options);
        var diagnostics = new List<AzureCloudShellDiagnostic>();
        fixture.Connection.DiagnosticEmitted += (_, diagnostic) => diagnostics.Add(diagnostic);
        await using var connection = fixture.Connection;
        await connection.StartAsync(string.Empty, null, 80, 24);

        await connection.CloseAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(TerminalConnectionState.Closed, connection.State);
        Assert.Contains(diagnostics, value => value.Code == "WebSocketCloseTimedOut");
    }

    [Fact]
    public async Task CancellationDuringReconnectPreservesCancelledReason()
    {
        using var cancellation = new CancellationTokenSource();
        var first = new MockWebSocket();
        var options = DefaultOptions with { ReconnectDelay = TimeSpan.FromMinutes(1) };
        var fixture = new ConnectionFixture([first], options: options);
        var reconnecting = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var faulted = false;
        fixture.Connection.DiagnosticEmitted += (_, diagnostic) =>
        {
            if (diagnostic.Code == "ReconnectAttempt")
            {
                reconnecting.TrySetResult();
            }
        };
        fixture.Connection.Faulted += (_, _) => faulted = true;
        var exited = CaptureExit(fixture.Connection);
        await using var connection = fixture.Connection;
        await connection.StartAsync(string.Empty, null, 80, 24, cancellation.Token);
        first.QueueException(new WebSocketException("network lost"));
        await reconnecting.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await cancellation.CancelAsync();
        var result = await exited.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(TerminalExitReason.Cancelled, result.Reason);
        Assert.False(faulted);
        Assert.Equal(TerminalConnectionState.Closed, connection.State);
    }

    [Fact]
    public async Task ResizeRefreshesAnExpiringCredential()
    {
        var stale = Credential(expiresAt: DateTimeOffset.UtcNow.AddMinutes(1));
        var fresh = stale with
        {
            AccessToken = "fresh-access-token",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(2),
        };
        var authenticator = new MockAuthenticator([stale, fresh]);
        var fixture = new ConnectionFixture(authenticator: authenticator);
        await using var connection = fixture.Connection;
        await connection.StartAsync(string.Empty, null, 80, 24);

        connection.Resize(90, 25);
        await WaitUntilAsync(() => fixture.Service.ResizeCalls.Count == 1);

        Assert.Equal(2, authenticator.CallCount);
        Assert.Equal("fresh-access-token", fixture.Service.ResizeCredentials.Single().AccessToken);
    }

    [Fact]
    public async Task ServiceUsesNativeRestContract()
    {
        var handler = new QueueHttpHandler(
            JsonResponse("""{"properties":{"preferredShellType":"bash"}}"""),
            JsonResponse("""{"properties":{"uri":"https://cloud.console.azure.com/session"}}""",
                requestId: "console-request"),
            JsonResponse("""{"id":"terminal-abc"}"""),
            JsonResponse("{}"));
        using var httpClient = new HttpClient(handler);
        var service = new AzureCloudShellService(httpClient, DefaultOptions);
        var credential = Credential();

        var settings = await service.GetUserSettingsAsync(credential, default);
        var terminal = await service.ProvisionTerminalAsync(
            credential,
            settings.PreferredShellType,
            132,
            43,
            default);
        await service.ResizeTerminalAsync(credential, terminal, 160, 50, default);

        Assert.Equal("bash", settings.PreferredShellType);
        Assert.Equal(
            "wss://cloud.console.azure.com/session/terminals/terminal-abc",
            terminal.WebSocketUri.AbsoluteUri);
        Assert.Collection(
            handler.Requests,
            request =>
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal(
                    "https://management.azure.com/providers/Microsoft.Portal/userSettings/cloudconsole?api-version=2025-09-01-preview",
                    request.Uri.AbsoluteUri);
                Assert.Equal("Bearer access-token", request.Authorization);
            },
            request =>
            {
                Assert.Equal(HttpMethod.Put, request.Method);
                Assert.Contains("/providers/Microsoft.Portal/consoles/default?", request.Uri.AbsoluteUri);
                Assert.Equal("""{"properties":{"osType":"linux"}}""", request.Body);
            },
            request =>
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal(
                    "https://cloud.console.azure.com/session/terminals?cols=132&rows=43&version=2019-01-01&shell=bash",
                    request.Uri.AbsoluteUri);
                Assert.Equal("{}", request.Body);
                Assert.Equal("application/json; charset=utf-8", request.ContentType);
                Assert.Equal("https://cloud.console.azure.com/session/", request.Referrer);
            },
            request =>
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal(
                    "https://cloud.console.azure.com/session/terminals/terminal-abc/size?cols=160&rows=50&version=2019-01-01",
                    request.Uri.AbsoluteUri);
                Assert.Equal(string.Empty, request.Body);
            });
    }

    [Fact]
    public async Task ServiceBuildsServiceBusHybridConnectionUri()
    {
        var handler = new QueueHttpHandler(
            JsonResponse("""{"properties":{"uri":"https://westus.servicebus.windows.net/cc"}}"""),
            JsonResponse(
                """{"id":"tid123","socketUri":"wss://westus.servicebus.windows.net/cc-AAAA//opaque"}"""));
        using var httpClient = new HttpClient(handler);
        var service = new AzureCloudShellService(httpClient, DefaultOptions);

        var terminal = await service.ProvisionTerminalAsync(
            Credential(),
            "pwsh",
            80,
            24,
            default);

        Assert.Equal(
            "wss://westus.servicebus.windows.net/$hc/cc-AAAA/terminals/tid123",
            terminal.WebSocketUri.AbsoluteUri);
    }

    [Fact]
    public async Task ServiceFailureCarriesHttpAndAzureMetadata()
    {
        var handler = new QueueHttpHandler(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent(
                """{"error":{"code":"TenantDisabled","message":"Cloud Shell disabled."}}""",
                Encoding.UTF8,
                "application/json"),
            Headers =
            {
                { "x-ms-request-id", "request-disabled" },
            },
        });
        using var httpClient = new HttpClient(handler);
        var service = new AzureCloudShellService(httpClient, DefaultOptions);

        var error = await Assert.ThrowsAsync<AzureCloudShellException>(
            async () => await service.GetUserSettingsAsync(Credential(), default));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, error.StatusCode);
        Assert.Equal("TenantDisabled", error.ServiceErrorCode);
        Assert.Equal("request-disabled", error.RequestId);
        Assert.True(error.IsTransient);
    }

    [Fact]
    public async Task DeviceFlowInvokesCallbacksAndRefreshesForSelectedTenant()
    {
        var handler = new QueueHttpHandler(
            JsonResponse(
                """{"message":"Open the page.","device_code":"secret-device-code","user_code":"ABCD-EFGH","verification_url":"https://microsoft.com/devicelogin","interval":"1","expires_in":"900"}"""),
            JsonResponse(
                """{"access_token":"common-token","refresh_token":"refresh-token","expires_in":"3600"}"""),
            JsonResponse(
                """{"value":[{"tenantId":"tenant-a","displayName":"A"},{"tenantID":"tenant-b","displayName":"B"}]}"""),
            JsonResponse(
                """{"access_token":"tenant-token","expires_on":"4102444800"}"""));
        using var httpClient = new HttpClient(handler);
        var timeProvider = new ImmediateTimeProvider();
        var cache = new MemoryTokenCache();
        var authenticator = new AzureDeviceCodeAuthenticator(
            httpClient,
            DefaultOptions,
            cache,
            timeProvider);
        AzureDeviceCodePrompt? prompt = null;
        IReadOnlyList<AzureCloudShellTenant>? offeredTenants = null;

        var credential = await authenticator.AuthenticateAsync(
            new AzureCloudShellAuthenticationCallbacks
            {
                ShowDeviceCodeAsync = (value, _) =>
                {
                    prompt = value;
                    return ValueTask.CompletedTask;
                },
                SelectTenantAsync = (tenants, _) =>
                {
                    offeredTenants = tenants;
                    return ValueTask.FromResult(tenants[1]);
                },
            },
            default);

        Assert.Equal("ABCD-EFGH", prompt?.UserCode);
        Assert.Equal("https://microsoft.com/devicelogin", prompt?.VerificationUri?.AbsoluteUri);
        Assert.Equal(2, offeredTenants?.Count);
        Assert.Equal("tenant-b", credential.Tenant.TenantId);
        Assert.Equal("tenant-token", credential.AccessToken);
        Assert.Equal("refresh-token", credential.RefreshToken);
        Assert.Equal(credential, cache.Value);
        Assert.Collection(
            handler.Requests,
            request =>
            {
                Assert.EndsWith("/common/oauth2/devicecode", request.Uri.AbsolutePath);
                Assert.Contains($"client_id={DefaultOptions.ClientId:D}", request.Body);
                Assert.Contains("resource=https%3A%2F%2Fmanagement.core.windows.net%2F", request.Body);
            },
            request =>
            {
                Assert.EndsWith("/common/oauth2/token", request.Uri.AbsolutePath);
                Assert.Contains("grant_type=device_code", request.Body);
                Assert.Contains("code=secret-device-code", request.Body);
            },
            request =>
            {
                Assert.Equal(
                    "https://management.azure.com/tenants?api-version=2020-01-01",
                    request.Uri.AbsoluteUri);
                Assert.Equal("Bearer common-token", request.Authorization);
            },
            request =>
            {
                Assert.EndsWith("/tenant-b/oauth2/token", request.Uri.AbsolutePath);
                Assert.Contains("grant_type=refresh_token", request.Body);
                Assert.Contains("refresh_token=refresh-token", request.Body);
            });
    }

    [Fact]
    public async Task CachedCredentialAvoidsNetworkAndCallbacks()
    {
        var handler = new QueueHttpHandler();
        using var httpClient = new HttpClient(handler);
        var cache = new MemoryTokenCache
        {
            Value = Credential(expiresAt: DateTimeOffset.UtcNow.AddHours(2)),
        };
        var authenticator = new AzureDeviceCodeAuthenticator(
            httpClient,
            DefaultOptions,
            cache);

        var credential = await authenticator.AuthenticateAsync(
            new AzureCloudShellAuthenticationCallbacks
            {
                ShowDeviceCodeAsync = (_, _) =>
                    throw new InvalidOperationException("Callback must not run."),
            },
            default);

        Assert.Equal(cache.Value, credential);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task InvalidCachedRefreshFallsBackToDeviceFlow()
    {
        var handler = new QueueHttpHandler(
            JsonResponse(
                """{"error":"invalid_grant","error_description":"refresh expired"}""",
                HttpStatusCode.BadRequest),
            JsonResponse(
                """{"message":"Authenticate","device_code":"code","interval":"1","expires_in":"900"}"""),
            JsonResponse(
                """{"access_token":"new-access","expires_in":"3600"}"""),
            JsonResponse(
                """{"value":[{"tenantId":"tenant-new"}]}"""));
        using var httpClient = new HttpClient(handler);
        var cache = new MemoryTokenCache
        {
            Value = new AzureCloudShellCredential(
                "expired",
                "bad-refresh",
                DateTimeOffset.UnixEpoch,
                new AzureCloudShellTenant("tenant-old", null, null)),
        };
        var authenticator = new AzureDeviceCodeAuthenticator(
            httpClient,
            DefaultOptions,
            cache,
            new ImmediateTimeProvider());
        var callbackCalled = false;

        var credential = await authenticator.AuthenticateAsync(
            new AzureCloudShellAuthenticationCallbacks
            {
                ShowDeviceCodeAsync = (_, _) =>
                {
                    callbackCalled = true;
                    return ValueTask.CompletedTask;
                },
            },
            default);

        Assert.True(callbackCalled);
        Assert.True(cache.Cleared);
        Assert.Equal("new-access", credential.AccessToken);
        Assert.Equal("tenant-new", credential.Tenant.TenantId);
    }

    [Fact]
    public async Task AuthenticationTimeoutIsReportedAsTransientFault()
    {
        using var httpClient = new HttpClient(new ThrowingHttpHandler(
            new TaskCanceledException("HTTP timeout")));
        var authenticator = new AzureDeviceCodeAuthenticator(httpClient, DefaultOptions);

        var error = await Assert.ThrowsAsync<AzureCloudShellException>(
            async () => await authenticator.AuthenticateAsync(
                new AzureCloudShellAuthenticationCallbacks
                {
                    ShowDeviceCodeAsync = (_, _) => ValueTask.CompletedTask,
                },
                default));

        Assert.Equal("AuthenticationTimedOut", error.Code);
        Assert.True(error.IsTransient);
    }

    private static TaskCompletionSource<TerminalExitInfo> CaptureExit(
        AzureCloudShellConnection connection)
    {
        var exited = new TaskCompletionSource<TerminalExitInfo>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        connection.SessionExited += (_, exit) => exited.TrySetResult(exit);
        return exited;
    }

    private static AzureCloudShellCredential Credential(
        DateTimeOffset? expiresAt = null) =>
        new(
            "access-token",
            "refresh-token",
            expiresAt ?? DateTimeOffset.UtcNow.AddHours(1),
            new AzureCloudShellTenant("tenant-1", "Tenant One", "example.test"));

    private static HttpResponseMessage JsonResponse(
        string json,
        HttpStatusCode statusCode = HttpStatusCode.OK,
        string? requestId = null)
    {
        var response = new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        if (requestId is not null)
        {
            response.Headers.Add("x-ms-request-id", requestId);
        }

        return response;
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!predicate())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class ConnectionFixture
    {
        public ConnectionFixture(
            IEnumerable<MockWebSocket>? sockets = null,
            MockAuthenticator? authenticator = null,
            AzureCloudShellOptions? options = null)
        {
            Authenticator = authenticator ?? new MockAuthenticator();
            Service = new MockService();
            Factory = new MockWebSocketFactory(sockets ?? [new MockWebSocket()]);
            var connectionOptions = options ?? DefaultOptions;
            Connection = new AzureCloudShellConnection(
                Authenticator,
                Service,
                Factory,
                new AzureCloudShellAuthenticationCallbacks
                {
                    ShowDeviceCodeAsync = (_, _) => ValueTask.CompletedTask,
                },
                connectionOptions);
        }

        public MockAuthenticator Authenticator { get; }
        public MockService Service { get; }
        public MockWebSocketFactory Factory { get; }
        public AzureCloudShellConnection Connection { get; }
    }

    private sealed class MockAuthenticator : IAzureCloudShellAuthenticator
    {
        private readonly Queue<AzureCloudShellCredential> _credentials;

        public MockAuthenticator(IEnumerable<AzureCloudShellCredential>? credentials = null)
        {
            _credentials = new Queue<AzureCloudShellCredential>(
                credentials ?? [Credential()]);
        }

        public Exception? Exception { get; set; }
        public int CallCount { get; private set; }

        public ValueTask<AzureCloudShellCredential> AuthenticateAsync(
            AzureCloudShellAuthenticationCallbacks callbacks,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Exception is null
                ? ValueTask.FromResult(
                    _credentials.Count > 1 ? _credentials.Dequeue() : _credentials.Peek())
                : ValueTask.FromException<AzureCloudShellCredential>(Exception);
        }
    }

    private sealed class MockService : IAzureCloudShellService
    {
        private int _terminalNumber;

        public List<(int Columns, int Rows, string Shell)> ProvisionCalls { get; } = [];
        public List<(int Columns, int Rows)> ResizeCalls { get; } = [];
        public List<AzureCloudShellCredential> ResizeCredentials { get; } = [];
        public Exception? ResizeException { get; set; }

        public ValueTask<AzureCloudShellUserSettings> GetUserSettingsAsync(
            AzureCloudShellCredential credential,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new AzureCloudShellUserSettings("pwsh"));

        public ValueTask<AzureCloudShellTerminal> ProvisionTerminalAsync(
            AzureCloudShellCredential credential,
            string shellType,
            int columns,
            int rows,
            CancellationToken cancellationToken)
        {
            ProvisionCalls.Add((columns, rows, shellType));
            var id = $"terminal-{Interlocked.Increment(ref _terminalNumber)}";
            return ValueTask.FromResult(new AzureCloudShellTerminal(
                id,
                new Uri("https://cloud.console.azure.com/session/"),
                new Uri($"wss://cloud.console.azure.com/session/terminals/{id}"),
                shellType,
                "request-1"));
        }

        public ValueTask ResizeTerminalAsync(
            AzureCloudShellCredential credential,
            AzureCloudShellTerminal terminal,
            int columns,
            int rows,
            CancellationToken cancellationToken)
        {
            if (ResizeException is not null)
            {
                return ValueTask.FromException(ResizeException);
            }

            ResizeCredentials.Add(credential);
            ResizeCalls.Add((columns, rows));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class MockWebSocketFactory(IEnumerable<MockWebSocket> sockets)
        : IAzureCloudShellWebSocketFactory
    {
        private readonly Queue<MockWebSocket> _sockets = new(sockets);

        public TimeSpan KeepAliveInterval { get; private set; }
        public TimeSpan KeepAliveTimeout { get; private set; }

        public IAzureCloudShellWebSocket Create(
            TimeSpan keepAliveInterval,
            TimeSpan keepAliveTimeout)
        {
            KeepAliveInterval = keepAliveInterval;
            KeepAliveTimeout = keepAliveTimeout;
            return _sockets.Dequeue();
        }
    }

    private sealed class MockWebSocket : IAzureCloudShellWebSocket
    {
        private readonly ConcurrentQueue<ReceiveItem> _items = new();
        private readonly SemaphoreSlim _available = new(0);

        public WebSocketState State { get; private set; } = WebSocketState.None;
        public WebSocketCloseStatus? CloseStatus { get; private set; }
        public string? CloseStatusDescription { get; private set; }
        public Exception? ConnectException { get; init; }
        public bool HangCloseOutput { get; init; }
        public List<byte[]> Sent { get; } = [];
        public TaskCompletionSource Connected { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask ConnectAsync(Uri uri, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ConnectException is not null)
            {
                return ValueTask.FromException(ConnectException);
            }

            State = WebSocketState.Open;
            Connected.TrySetResult();
            return ValueTask.CompletedTask;
        }

        public ValueTask SendAsync(
            ReadOnlyMemory<byte> data,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Sent.Add(data.ToArray());
            return ValueTask.CompletedTask;
        }

        public async ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken)
        {
            await _available.WaitAsync(cancellationToken);
            Assert.True(_items.TryDequeue(out var item));
            if (item.Exception is not null)
            {
                throw item.Exception;
            }

            if (item.MessageType == WebSocketMessageType.Close)
            {
                State = WebSocketState.CloseReceived;
                CloseStatus = item.CloseStatus;
                CloseStatusDescription = item.CloseDescription;
                return new ValueWebSocketReceiveResult(0, WebSocketMessageType.Close, true);
            }

            item.Data.CopyTo(buffer);
            return new ValueWebSocketReceiveResult(
                item.Data.Length,
                item.MessageType,
                true);
        }

        public async ValueTask CloseOutputAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            if (HangCloseOutput)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            CloseStatus = closeStatus;
            CloseStatusDescription = statusDescription;
            State = WebSocketState.Closed;
            QueueClose(closeStatus, statusDescription);
        }

        public void Abort()
        {
            State = WebSocketState.Aborted;
        }

        public ValueTask DisposeAsync()
        {
            State = WebSocketState.Closed;
            return ValueTask.CompletedTask;
        }

        public void QueueMessage(
            byte[] data,
            WebSocketMessageType messageType = WebSocketMessageType.Text)
        {
            _items.Enqueue(new ReceiveItem(data, messageType, null, null, null));
            _available.Release();
        }

        public void QueueClose(
            WebSocketCloseStatus status,
            string? description)
        {
            _items.Enqueue(new ReceiveItem([], WebSocketMessageType.Close, status, description, null));
            _available.Release();
        }

        public void QueueException(Exception exception)
        {
            _items.Enqueue(new ReceiveItem([], WebSocketMessageType.Binary, null, null, exception));
            _available.Release();
        }

        private sealed record ReceiveItem(
            byte[] Data,
            WebSocketMessageType MessageType,
            WebSocketCloseStatus? CloseStatus,
            string? CloseDescription,
            Exception? Exception);
    }

    private sealed class QueueHttpHandler(params HttpResponseMessage[] responses)
        : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);

        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new CapturedRequest(
                request.Method,
                request.RequestUri!,
                body,
                request.Content?.Headers.ContentType?.ToString(),
                request.Headers.Authorization?.ToString(),
                request.Headers.Referrer?.AbsoluteUri));
            return _responses.Dequeue();
        }
    }

    private sealed class ThrowingHttpHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(exception);
    }

    private sealed record CapturedRequest(
        HttpMethod Method,
        Uri Uri,
        string? Body,
        string? ContentType,
        string? Authorization,
        string? Referrer);

    private sealed class MemoryTokenCache : IAzureCloudShellTokenCache
    {
        public AzureCloudShellCredential? Value { get; set; }
        public bool Cleared { get; private set; }

        public ValueTask<AzureCloudShellCredential?> LoadAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(Value);

        public ValueTask StoreAsync(
            AzureCloudShellCredential credential,
            CancellationToken cancellationToken)
        {
            Value = credential;
            return ValueTask.CompletedTask;
        }

        public ValueTask ClearAsync(CancellationToken cancellationToken)
        {
            Value = null;
            Cleared = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ImmediateTimeProvider : TimeProvider
    {
        private long _utcTicks = DateTimeOffset.UtcNow.UtcTicks;

        public override DateTimeOffset GetUtcNow() =>
            new(Interlocked.Read(ref _utcTicks), TimeSpan.Zero);

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            Interlocked.Add(ref _utcTicks, Math.Max(dueTime.Ticks, 0));
            return new ImmediateTimer(callback, state);
        }

        private sealed class ImmediateTimer : ITimer
        {
            private readonly TimerCallback _callback;
            private readonly object? _state;
            private int _disposed;

            public ImmediateTimer(TimerCallback callback, object? state)
            {
                _callback = callback;
                _state = state;
                ThreadPool.QueueUserWorkItem(static timer =>
                {
                    var value = (ImmediateTimer)timer!;
                    if (Volatile.Read(ref value._disposed) == 0)
                    {
                        value._callback(value._state);
                    }
                }, this);
            }

            public bool Change(TimeSpan dueTime, TimeSpan period) => false;

            public void Dispose() => Interlocked.Exchange(ref _disposed, 1);

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
