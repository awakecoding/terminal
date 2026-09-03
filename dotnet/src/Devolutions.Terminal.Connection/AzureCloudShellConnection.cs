using System.Net.WebSockets;
using System.Text;

namespace Devolutions.Terminal.Connection;

public sealed class AzureCloudShellConnection : IRestartableTerminalConnection
{
    public static readonly Guid ConnectionTypeGuid =
        new("D9FCFDFA-A479-412C-83B7-C5640E61CD62");

    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly IAzureCloudShellAuthenticator _authenticator;
    private readonly IAzureCloudShellService _service;
    private readonly IAzureCloudShellWebSocketFactory _webSocketFactory;
    private readonly AzureCloudShellAuthenticationCallbacks _authenticationCallbacks;
    private readonly AzureCloudShellOptions _options;
    private readonly TimeProvider _timeProvider;
    private SessionResources? _session;
    private TerminalLaunchOptions? _lastOptions;
    private long _generation;
    private bool _hasStarted;
    private bool _disposed;

    public AzureCloudShellConnection(
        IAzureCloudShellAuthenticator authenticator,
        IAzureCloudShellService service,
        IAzureCloudShellWebSocketFactory webSocketFactory,
        AzureCloudShellAuthenticationCallbacks authenticationCallbacks,
        AzureCloudShellOptions options,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(authenticator);
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(webSocketFactory);
        ArgumentNullException.ThrowIfNull(authenticationCallbacks);
        ArgumentNullException.ThrowIfNull(options);
        if (options.ClientId == Guid.Empty)
        {
            throw new ArgumentException("An Azure public-client application ID is required.", nameof(options));
        }

        if (options.MaximumReconnectAttempts < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        if (options.WebSocketCloseTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        _authenticator = authenticator;
        _service = service;
        _webSocketFactory = webSocketFactory;
        _authenticationCallbacks = authenticationCallbacks;
        _options = options;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public event EventHandler<ReadOnlyMemory<byte>>? OutputReceived;
    public event EventHandler<int>? Exited;
    public event EventHandler<TerminalExitInfo>? SessionExited;
    public event EventHandler<Exception>? Faulted;
    public event EventHandler<AzureCloudShellDiagnostic>? DiagnosticEmitted;

    public bool IsRunning { get; private set; }
    public int Columns { get; private set; }
    public int Rows { get; private set; }
    public TerminalConnectionCapabilities Capabilities { get; } =
        TerminalConnectionCapabilities.Resize |
        TerminalConnectionCapabilities.Restart |
        TerminalConnectionCapabilities.ProcessMetadata;
    public TerminalConnectionState State { get; private set; } =
        TerminalConnectionState.NotConnected;
    public TerminalProcessMetadata? ProcessMetadata { get; private set; }
    public TerminalExitInfo? LastExitInfo { get; private set; }
    public AzureCloudShellSessionMetadata? ServiceMetadata { get; private set; }
    public AzureCloudShellExitMetadata? LastServiceExit { get; private set; }
    public AzureCloudShellFaultMetadata? LastFault { get; private set; }

    public Task StartAsync(
        string commandLine,
        string? workingDirectory,
        int columns,
        int rows,
        CancellationToken cancellationToken = default) =>
        StartAsync(
            new TerminalLaunchOptions
            {
                CommandLine = commandLine,
                WorkingDirectory = workingDirectory,
                Columns = columns,
                Rows = rows,
            },
            cancellationToken);

    public async Task StartAsync(
        TerminalLaunchOptions options,
        CancellationToken cancellationToken = default)
    {
        ValidateOptions(options, cancellationToken);
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_stateLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_hasStarted)
                {
                    throw new InvalidOperationException(
                        "The Azure Cloud Shell connection has already started. Use RestartAsync to replace its session.");
                }
            }

            await StartCoreAsync(options, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task RestartAsync(
        TerminalLaunchOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var restartOptions = options ?? _lastOptions
                ?? throw new InvalidOperationException("No previous Azure Cloud Shell launch options are available.");
            ValidateOptions(restartOptions, cancellationToken);
            await StopCoreAsync(TerminalExitReason.Closed, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await StartCoreAsync(restartOptions, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await StopCoreAsync(TerminalExitReason.Closed, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        if (!data.IsEmpty)
        {
            WriteAsync(data.ToArray()).AsTask().GetAwaiter().GetResult();
        }
    }

    public void Write(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > 0)
        {
            Write(Encoding.UTF8.GetBytes(text));
        }
    }

    public async ValueTask WriteAsync(
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default)
    {
        if (data.IsEmpty)
        {
            return;
        }

        SessionResources session;
        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            session = _session
                ?? throw new InvalidOperationException("The Azure Cloud Shell connection is not running.");
            if (!IsRunning)
            {
                throw new InvalidOperationException("The Azure Cloud Shell connection is not running.");
            }
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            session.Lifetime.Token);
        await _writeLock.WaitAsync(linkedCancellation.Token).ConfigureAwait(false);
        try
        {
            if (session.Socket.State != WebSocketState.Open)
            {
                throw new InvalidOperationException("The Azure Cloud Shell WebSocket is not connected.");
            }

            await session.Socket.SendAsync(
                data,
                WebSocketMessageType.Text,
                true,
                linkedCancellation.Token).ConfigureAwait(false);
        }
        catch (WebSocketException ex)
        {
            throw CreateWebSocketException("WebSocketSendFailed", "Sending terminal input failed.", ex);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void Resize(int columns, int rows)
    {
        ValidateDimension(columns, nameof(columns));
        ValidateDimension(rows, nameof(rows));

        SessionResources? session;
        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Columns = columns;
            Rows = rows;
            session = IsRunning ? _session : null;
        }

        if (session is null)
        {
            return;
        }

        QueueResize(session, columns, rows);
    }

    private void QueueResize(SessionResources session, int columns, int rows)
    {
        lock (session.ResizeLock)
        {
            session.PendingResize = (columns, rows);
            if (session.ResizeWorkerRunning)
            {
                return;
            }

            session.ResizeWorkerRunning = true;
            session.ResizeTask = ProcessResizesAsync(session);
        }
    }

    private async Task ProcessResizesAsync(SessionResources session)
    {
        while (!session.Lifetime.IsCancellationRequested)
        {
            (int Columns, int Rows) size;
            lock (session.ResizeLock)
            {
                if (session.PendingResize is not { } pending)
                {
                    session.ResizeWorkerRunning = false;
                    return;
                }

                size = pending;
                session.PendingResize = null;
            }

            try
            {
                if (session.Credential.ExpiresAt <=
                    _timeProvider.GetUtcNow() + _options.TokenRefreshBuffer)
                {
                    EmitDiagnostic(
                        AzureCloudShellDiagnosticSeverity.Information,
                        AzureCloudShellStage.Authentication,
                        "ResizeCredentialRefresh",
                        "Refreshing Azure credentials before resizing the terminal.");
                    session.Credential = await _authenticator.AuthenticateAsync(
                        _authenticationCallbacks,
                        session.Lifetime.Token).ConfigureAwait(false);
                }

                await _service.ResizeTerminalAsync(
                    session.Credential,
                    session.Terminal,
                    size.Columns,
                    size.Rows,
                    session.Lifetime.Token).ConfigureAwait(false);
                EmitDiagnostic(
                    AzureCloudShellDiagnosticSeverity.Trace,
                    AzureCloudShellStage.Resize,
                    "TerminalResized",
                    $"Azure Cloud Shell terminal resized to {size.Columns}x{size.Rows}.",
                    session.Terminal.RequestId);
            }
            catch (OperationCanceledException) when (session.Lifetime.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                EmitDiagnostic(
                    AzureCloudShellDiagnosticSeverity.Warning,
                    AzureCloudShellStage.Resize,
                    "TerminalResizeFailed",
                    ex.Message,
                    (ex as AzureCloudShellException)?.RequestId);
            }
        }

        lock (session.ResizeLock)
        {
            session.ResizeWorkerRunning = false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_stateLock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
            }

            await StopCoreAsync(TerminalExitReason.Disposed, CancellationToken.None)
                .ConfigureAwait(false);
            lock (_stateLock)
            {
                State = TerminalConnectionState.Disposed;
            }
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private async Task StartCoreAsync(
        TerminalLaunchOptions launchOptions,
        CancellationToken cancellationToken)
    {
        lock (_stateLock)
        {
            State = TerminalConnectionState.Connecting;
            LastExitInfo = null;
            LastServiceExit = null;
            LastFault = null;
            Columns = launchOptions.Columns;
            Rows = launchOptions.Rows;
        }

        IAzureCloudShellWebSocket? socket = null;
        CancellationTokenSource? lifetime = null;
        try
        {
            EmitDiagnostic(
                AzureCloudShellDiagnosticSeverity.Information,
                AzureCloudShellStage.Authentication,
                "AuthenticationStarted",
                $"Authenticating to {_options.Environment.Name}.");
            var credential = await _authenticator.AuthenticateAsync(
                _authenticationCallbacks,
                cancellationToken).ConfigureAwait(false);
            EmitDiagnostic(
                AzureCloudShellDiagnosticSeverity.Information,
                AzureCloudShellStage.UserSettings,
                "UserSettingsRequested",
                "Reading Azure Cloud Shell user settings.");
            var settings = await _service.GetUserSettingsAsync(credential, cancellationToken)
                .ConfigureAwait(false);
            var shellType = string.IsNullOrWhiteSpace(_options.ShellType)
                ? settings.PreferredShellType
                : _options.ShellType;

            EmitDiagnostic(
                AzureCloudShellDiagnosticSeverity.Information,
                AzureCloudShellStage.Provisioning,
                "TerminalProvisioningStarted",
                $"Provisioning the Azure Cloud Shell {shellType} terminal.");
            var terminal = await _service.ProvisionTerminalAsync(
                credential,
                shellType,
                launchOptions.Columns,
                launchOptions.Rows,
                cancellationToken).ConfigureAwait(false);
            socket = _webSocketFactory.Create(
                _options.WebSocketKeepAliveInterval,
                _options.WebSocketKeepAliveTimeout);
            await socket.ConnectAsync(terminal.WebSocketUri, cancellationToken).ConfigureAwait(false);
            lifetime = new CancellationTokenSource();

            var generation = ++_generation;
            var sessionId = Guid.NewGuid();
            var startedAt = _timeProvider.GetUtcNow();
            var processMetadata = new TerminalProcessMetadata(
                sessionId,
                0,
                string.IsNullOrWhiteSpace(launchOptions.CommandLine)
                    ? $"Azure Cloud Shell ({shellType})"
                    : launchOptions.CommandLine,
                terminal.CloudShellUri.AbsoluteUri,
                startedAt);
            var serviceMetadata = new AzureCloudShellSessionMetadata(
                sessionId,
                terminal.Id,
                credential.Tenant.TenantId,
                shellType,
                terminal.CloudShellUri,
                terminal.WebSocketUri.Host,
                startedAt);
            var session = new SessionResources(
                generation,
                launchOptions,
                credential,
                terminal,
                processMetadata,
                serviceMetadata,
                socket,
                lifetime);

            lock (_stateLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _session = session;
                _lastOptions = launchOptions;
                _hasStarted = true;
                ProcessMetadata = processMetadata;
                ServiceMetadata = serviceMetadata;
                IsRunning = true;
                State = TerminalConnectionState.Connected;
            }

            socket = null;
            lifetime = null;
            session.CancellationRegistration = cancellationToken.Register(
                static state =>
                {
                    var cancellation = (CancellationState)state!;
                    cancellation.Connection.Cancel(cancellation.Generation);
                },
                new CancellationState(this, generation));
            session.ReceiveTask = ReceiveLoopAsync(session);
            EmitDiagnostic(
                AzureCloudShellDiagnosticSeverity.Information,
                AzureCloudShellStage.WebSocket,
                "TerminalConnected",
                "Azure Cloud Shell terminal connected.",
                terminal.RequestId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            PublishStartupExit(TerminalExitReason.Cancelled);
            throw;
        }
        catch (Exception ex)
        {
            var failure = NormalizeException(
                ex,
                AzureCloudShellStage.Lifecycle,
                "StartupFailed",
                "Azure Cloud Shell startup failed.");
            PublishStartupFault(failure);
            throw failure;
        }
        finally
        {
            if (socket is not null)
            {
                await socket.DisposeAsync().ConfigureAwait(false);
            }

            lifetime?.Dispose();
        }
    }

    private async Task StopCoreAsync(
        TerminalExitReason reason,
        CancellationToken cancellationToken)
    {
        SessionResources? session;
        lock (_stateLock)
        {
            session = _session;
            if (session is null)
            {
                IsRunning = false;
                if (!_disposed)
                {
                    State = TerminalConnectionState.Closed;
                }

                return;
            }

            session.RequestedExitReason ??= reason;
            IsRunning = false;
            State = TerminalConnectionState.Closing;
        }

        session.CancellationRegistration.Dispose();
        OperationCanceledException? cancellationError = null;
        try
        {
            using var closeCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            closeCancellation.CancelAfter(_options.WebSocketCloseTimeout);
            await _writeLock.WaitAsync(closeCancellation.Token).ConfigureAwait(false);
            try
            {
                if (session.Socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    await session.Socket.CloseOutputAsync(
                        WebSocketCloseStatus.NormalClosure,
                        null,
                        closeCancellation.Token).ConfigureAwait(false);
                }
            }
            finally
            {
                _writeLock.Release();
            }
        }
        catch (OperationCanceledException ex)
        {
            session.Socket.Abort();
            if (cancellationToken.IsCancellationRequested)
            {
                cancellationError = ex;
            }
            else
            {
                EmitDiagnostic(
                    AzureCloudShellDiagnosticSeverity.Warning,
                    AzureCloudShellStage.Lifecycle,
                    "WebSocketCloseTimedOut",
                    "Azure Cloud Shell did not accept the WebSocket close before the timeout.",
                    session.Terminal.RequestId);
            }
        }
        catch (WebSocketException)
        {
            session.Socket.Abort();
        }
        finally
        {
            await session.Lifetime.CancelAsync().ConfigureAwait(false);
            session.Socket.Abort();
        }

        if (session.ReceiveTask is not null)
        {
            try
            {
                await session.ReceiveTask.WaitAsync(_options.WebSocketCloseTimeout)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (session.Lifetime.IsCancellationRequested)
            {
            }
            catch (TimeoutException)
            {
                EmitDiagnostic(
                    AzureCloudShellDiagnosticSeverity.Warning,
                    AzureCloudShellStage.Lifecycle,
                    "ReceiveLoopCloseTimedOut",
                    "Azure Cloud Shell receive loop did not stop before the close timeout.",
                    session.Terminal.RequestId);
            }
        }

        PublishExit(
            session,
            reason,
            session.Socket.CloseStatus,
            session.Socket.CloseStatusDescription);
        await ReleaseSessionAsync(session).ConfigureAwait(false);
        if (cancellationError is not null)
        {
            throw cancellationError;
        }
    }

    private async Task ReceiveLoopAsync(SessionResources session)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (!session.Lifetime.IsCancellationRequested)
            {
                ValueWebSocketReceiveResult result;
                try
                {
                    result = await session.Socket.ReceiveAsync(
                        buffer,
                        session.Lifetime.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (session.Lifetime.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex) when (ex is WebSocketException or HttpRequestException)
                {
                    if (await TryReconnectAsync(session, ex).ConfigureAwait(false))
                    {
                        continue;
                    }

                    return;
                }

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    var closeStatus = session.Socket.CloseStatus;
                    var closeDescription = session.Socket.CloseStatusDescription;
                    if (session.RequestedExitReason is null &&
                        closeStatus != WebSocketCloseStatus.NormalClosure &&
                        await TryReconnectAsync(
                            session,
                            CreateWebSocketException(
                                "UnexpectedRemoteClose",
                                $"Azure Cloud Shell closed the WebSocket ({closeStatus}: {closeDescription})."))
                            .ConfigureAwait(false))
                    {
                        continue;
                    }

                    PublishExit(
                        session,
                        session.RequestedExitReason ?? TerminalExitReason.ProcessExited,
                        closeStatus,
                        closeDescription);
                    return;
                }

                if (result.Count > 0)
                {
                    OutputReceived?.Invoke(this, buffer.AsMemory(0, result.Count).ToArray());
                }
            }

            PublishExit(
                session,
                session.RequestedExitReason ?? TerminalExitReason.Closed,
                session.Socket.CloseStatus,
                session.Socket.CloseStatusDescription);
        }
        catch (Exception ex)
        {
            PublishFault(
                session,
                NormalizeException(
                    ex,
                    AzureCloudShellStage.WebSocket,
                    "ReceiveLoopFailed",
                    "Azure Cloud Shell receive loop failed."));
        }
        finally
        {
            await ReleaseSessionAsync(session).ConfigureAwait(false);
        }
    }

    private async ValueTask<bool> TryReconnectAsync(
        SessionResources session,
        Exception triggeringException)
    {
        if (session.RequestedExitReason is not null ||
            session.Lifetime.IsCancellationRequested)
        {
            PublishExit(
                session,
                session.RequestedExitReason ?? TerminalExitReason.Closed,
                session.Socket.CloseStatus,
                session.Socket.CloseStatusDescription);
            return false;
        }

        if (_options.MaximumReconnectAttempts == 0)
        {
            PublishFault(
                session,
                NormalizeException(
                    triggeringException,
                    AzureCloudShellStage.WebSocket,
                    "WebSocketDisconnected",
                    "The Azure Cloud Shell WebSocket disconnected."));
            return false;
        }

        Exception lastError = triggeringException;
        for (var attempt = 1; attempt <= _options.MaximumReconnectAttempts; attempt++)
        {
            session.ReconnectAttempts++;
            lock (_stateLock)
            {
                if (ReferenceEquals(_session, session))
                {
                    State = TerminalConnectionState.Connecting;
                }
            }

            EmitDiagnostic(
                AzureCloudShellDiagnosticSeverity.Warning,
                AzureCloudShellStage.Reconnect,
                "ReconnectAttempt",
                $"Reconnecting Azure Cloud Shell WebSocket (attempt {attempt} of {_options.MaximumReconnectAttempts}).",
                session.Terminal.RequestId);
            IAzureCloudShellWebSocket? replacement = null;
            try
            {
                if (_options.ReconnectDelay > TimeSpan.Zero)
                {
                    await Task.Delay(
                        _options.ReconnectDelay,
                        _timeProvider,
                        session.Lifetime.Token).ConfigureAwait(false);
                }

                replacement = _webSocketFactory.Create(
                    _options.WebSocketKeepAliveInterval,
                    _options.WebSocketKeepAliveTimeout);
                await replacement.ConnectAsync(
                    session.Terminal.WebSocketUri,
                    session.Lifetime.Token).ConfigureAwait(false);
                IAzureCloudShellWebSocket previous;
                await _writeLock.WaitAsync(session.Lifetime.Token).ConfigureAwait(false);
                try
                {
                    previous = session.Socket;
                    session.Socket = replacement;
                    replacement = null;
                }
                finally
                {
                    _writeLock.Release();
                }

                await previous.DisposeAsync().ConfigureAwait(false);
                lock (_stateLock)
                {
                    if (ReferenceEquals(_session, session))
                    {
                        State = TerminalConnectionState.Connected;
                        IsRunning = true;
                    }
                }

                EmitDiagnostic(
                    AzureCloudShellDiagnosticSeverity.Information,
                    AzureCloudShellStage.Reconnect,
                    "ReconnectSucceeded",
                    "Azure Cloud Shell WebSocket reconnected.",
                    session.Terminal.RequestId);
                return true;
            }
            catch (OperationCanceledException) when (session.Lifetime.IsCancellationRequested)
            {
                PublishExit(
                    session,
                    session.RequestedExitReason ?? TerminalExitReason.Closed,
                    session.Socket.CloseStatus,
                    session.Socket.CloseStatusDescription);
                return false;
            }
            catch (Exception ex) when (ex is WebSocketException or HttpRequestException)
            {
                lastError = ex;
            }
            finally
            {
                if (replacement is not null)
                {
                    await replacement.DisposeAsync().ConfigureAwait(false);
                }
            }
        }

        PublishFault(
            session,
            NormalizeException(
                lastError,
                AzureCloudShellStage.Reconnect,
                "ReconnectExhausted",
                $"Azure Cloud Shell WebSocket reconnection failed after {_options.MaximumReconnectAttempts} attempts."));
        return false;
    }

    private void Cancel(long generation)
    {
        SessionResources? session;
        lock (_stateLock)
        {
            session = _session;
            if (_disposed || session?.Generation != generation)
            {
                return;
            }

            session.RequestedExitReason = TerminalExitReason.Cancelled;
        }

        session.Lifetime.Cancel();
        session.Socket.Abort();
    }

    private void PublishStartupExit(TerminalExitReason reason)
    {
        var exit = new TerminalExitInfo(
            null,
            null,
            reason,
            false,
            _timeProvider.GetUtcNow());
        lock (_stateLock)
        {
            IsRunning = false;
            State = TerminalConnectionState.Closed;
            LastExitInfo = exit;
            LastServiceExit = new AzureCloudShellExitMetadata(
                null,
                null,
                null,
                0,
                reason,
                exit.ExitedAt);
        }
    }

    private void PublishStartupFault(AzureCloudShellException exception)
    {
        var exit = new TerminalExitInfo(
            null,
            null,
            TerminalExitReason.StartupFailure,
            false,
            _timeProvider.GetUtcNow());
        lock (_stateLock)
        {
            IsRunning = false;
            State = TerminalConnectionState.Failed;
            LastExitInfo = exit;
            LastFault = exception.ToMetadata();
            LastServiceExit = new AzureCloudShellExitMetadata(
                null,
                null,
                null,
                0,
                TerminalExitReason.StartupFailure,
                exit.ExitedAt);
        }

        EmitDiagnostic(
            AzureCloudShellDiagnosticSeverity.Error,
            exception.Stage,
            exception.Code,
            exception.Message,
            exception.RequestId);
        Faulted?.Invoke(this, exception);
    }

    private void PublishFault(
        SessionResources session,
        AzureCloudShellException exception)
    {
        lock (_stateLock)
        {
            if (session.ExitPublished)
            {
                return;
            }

            LastFault = exception.ToMetadata();
        }

        EmitDiagnostic(
            AzureCloudShellDiagnosticSeverity.Error,
            exception.Stage,
            exception.Code,
            exception.Message,
            exception.RequestId);
        PublishExit(
            session,
            TerminalExitReason.ConnectionFailure,
            session.Socket.CloseStatus,
            session.Socket.CloseStatusDescription);
        Faulted?.Invoke(this, exception);
    }

    private void PublishExit(
        SessionResources session,
        TerminalExitReason reason,
        WebSocketCloseStatus? closeStatus,
        string? closeDescription)
    {
        TerminalExitInfo exit;
        AzureCloudShellExitMetadata serviceExit;
        lock (_stateLock)
        {
            if (session.ExitPublished)
            {
                return;
            }

            session.ExitPublished = true;
            int? exitCode = reason == TerminalExitReason.ProcessExited ? 0 : null;
            var exitedAt = _timeProvider.GetUtcNow();
            exit = new TerminalExitInfo(
                session.ProcessMetadata,
                exitCode,
                reason,
                TerminalCloseOnExit.ShouldClose(
                    session.LaunchOptions.CloseOnExit,
                    reason,
                    exitCode,
                    session.LaunchOptions.IsDefaultTerminalSession),
                exitedAt);
            serviceExit = new AzureCloudShellExitMetadata(
                session.ServiceMetadata,
                closeStatus,
                closeDescription,
                session.ReconnectAttempts,
                reason,
                exitedAt);
            if (ReferenceEquals(_session, session))
            {
                IsRunning = false;
                State = reason == TerminalExitReason.ConnectionFailure
                    ? TerminalConnectionState.Failed
                    : TerminalConnectionState.Closed;
                LastExitInfo = exit;
                LastServiceExit = serviceExit;
            }
        }

        SessionExited?.Invoke(this, exit);
        Exited?.Invoke(this, exit.ExitCode ?? (reason == TerminalExitReason.ConnectionFailure ? 1 : 0));
    }

    private async ValueTask ReleaseSessionAsync(SessionResources session)
    {
        if (!session.TryBeginDispose())
        {
            return;
        }

        session.CancellationRegistration.Dispose();
        await session.Lifetime.CancelAsync().ConfigureAwait(false);
        session.Socket.Abort();
        await session.Socket.DisposeAsync().ConfigureAwait(false);
        session.Lifetime.Dispose();
        lock (_stateLock)
        {
            if (ReferenceEquals(_session, session))
            {
                _session = null;
            }
        }
    }

    private void EmitDiagnostic(
        AzureCloudShellDiagnosticSeverity severity,
        AzureCloudShellStage stage,
        string code,
        string message,
        string? requestId = null) =>
        DiagnosticEmitted?.Invoke(
            this,
            new AzureCloudShellDiagnostic(
                severity,
                stage,
                code,
                message,
                _timeProvider.GetUtcNow(),
                requestId));

    private static AzureCloudShellException NormalizeException(
        Exception exception,
        AzureCloudShellStage stage,
        string code,
        string message) =>
        exception as AzureCloudShellException ??
        new AzureCloudShellException(
            stage,
            code,
            $"{message} {exception.Message}",
            isTransient: exception is HttpRequestException or WebSocketException,
            innerException: exception);

    private static AzureCloudShellException CreateWebSocketException(
        string code,
        string message,
        Exception? innerException = null) =>
        new(
            AzureCloudShellStage.WebSocket,
            code,
            message,
            isTransient: true,
            innerException: innerException);

    private static void ValidateOptions(
        TerminalLaunchOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateDimension(options.Columns, nameof(options.Columns));
        ValidateDimension(options.Rows, nameof(options.Rows));
    }

    private static void ValidateDimension(int value, string parameterName)
    {
        if (value is < 1 or > short.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                $"Azure Cloud Shell dimensions must be between 1 and {short.MaxValue}.");
        }
    }

    private sealed class SessionResources(
        long generation,
        TerminalLaunchOptions launchOptions,
        AzureCloudShellCredential credential,
        AzureCloudShellTerminal terminal,
        TerminalProcessMetadata processMetadata,
        AzureCloudShellSessionMetadata serviceMetadata,
        IAzureCloudShellWebSocket socket,
        CancellationTokenSource lifetime)
    {
        private int _disposeStarted;

        public long Generation { get; } = generation;
        public TerminalLaunchOptions LaunchOptions { get; } = launchOptions;
        public AzureCloudShellCredential Credential { get; set; } = credential;
        public AzureCloudShellTerminal Terminal { get; } = terminal;
        public TerminalProcessMetadata ProcessMetadata { get; } = processMetadata;
        public AzureCloudShellSessionMetadata ServiceMetadata { get; } = serviceMetadata;
        public IAzureCloudShellWebSocket Socket { get; set; } = socket;
        public CancellationTokenSource Lifetime { get; } = lifetime;
        public CancellationTokenRegistration CancellationRegistration { get; set; }
        public Task? ReceiveTask { get; set; }
        public object ResizeLock { get; } = new();
        public (int Columns, int Rows)? PendingResize { get; set; }
        public Task? ResizeTask { get; set; }
        public bool ResizeWorkerRunning { get; set; }
        public TerminalExitReason? RequestedExitReason { get; set; }
        public int ReconnectAttempts { get; set; }
        public bool ExitPublished { get; set; }

        public bool TryBeginDispose() => Interlocked.Exchange(ref _disposeStarted, 1) == 0;
    }

    private sealed record CancellationState(AzureCloudShellConnection Connection, long Generation);
}
