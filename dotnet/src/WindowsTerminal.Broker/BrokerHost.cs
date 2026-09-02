using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WindowsTerminal.Broker;

public sealed class BrokerHost : IAsyncDisposable
{
    private const int AcceptLoopCount = 8;
    private const int MaximumCachedResponses = 1024;
    private static readonly TimeSpan ResponseRetryWindow = TimeSpan.FromSeconds(5);

    private readonly BrokerEndpointStore _endpointStore;
    private readonly IBrokerRequestHandler _handler;
    private readonly BrokerElection _election;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<int, Task> _connections = [];
    private readonly ConcurrentDictionary<string, CachedResponse> _responses = [];
    private readonly ConcurrentQueue<string> _responseOrder = [];
    private readonly Task[] _acceptLoops;
    private int _connectionId;

    private BrokerHost(
        BrokerEndpointStore endpointStore,
        IBrokerRequestHandler handler,
        BrokerElection election,
        BrokerEndpoint endpoint)
    {
        _endpointStore = endpointStore;
        _handler = handler;
        _election = election;
        Endpoint = endpoint;
        _endpointStore.Write(endpoint);
        _acceptLoops = Enumerable.Range(0, AcceptLoopCount)
            .Select(_ => AcceptLoopAsync(_shutdown.Token))
            .ToArray();
    }

    public BrokerEndpoint Endpoint { get; }

    public static PipeOptions SecurePipeOptions =>
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.WriteThrough;

    public static BrokerHost? TryCreate(
        IBrokerRequestHandler handler,
        string instanceKey = "default",
        string? endpointDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var election = BrokerElection.TryAcquire(ElectionName(instanceKey));
        if (election is null)
        {
            return null;
        }

        var store = new BrokerEndpointStore(endpointDirectory, instanceKey);
        var endpoint = new BrokerEndpoint(
            BrokerProtocol.Version,
            $"WindowsTerminal.DotNet.v{BrokerProtocol.Version}.{RandomNumberGenerator.GetHexString(16)}",
            RandomNumberGenerator.GetHexString(32),
            Environment.ProcessId);
        return new BrokerHost(store, handler, election, endpoint);
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        try
        {
            try
            {
                await Task.WhenAll(_acceptLoops).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            try
            {
                await Task.WhenAll(_connections.Values).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
        finally
        {
            _endpointStore.DeleteIfMatches(Endpoint);
            _election.Dispose();
            _shutdown.Dispose();
        }
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(
                Endpoint.PipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                SecurePipeOptions,
                16 * 1024,
                16 * 1024);
            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                pipe.Dispose();
                throw;
            }

            var id = Interlocked.Increment(ref _connectionId);
            var task = HandleConnectionAsync(pipe, cancellationToken);
            _connections[id] = task;
            _ = task.ContinueWith(
                (completedTask, state) =>
                {
                    _ = completedTask.Exception;
                    ((ConcurrentDictionary<int, Task>)state!).TryRemove(id, out var ignored);
                    _ = ignored;
                },
                _connections,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        await using (pipe.ConfigureAwait(false))
        {
            BrokerResponse response;
            try
            {
                var request = await ReadAsync(pipe, cancellationToken).ConfigureAwait(false);
                response = await DispatchOnceAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidDataException ex)
            {
                response = new(
                    BrokerProtocol.Version,
                    string.Empty,
                    BrokerStatus.InvalidRequest,
                    ex.Message);
            }
            catch (JsonException ex)
            {
                response = new(
                    BrokerProtocol.Version,
                    string.Empty,
                    BrokerStatus.InvalidRequest,
                    ex.Message);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                response = new(
                    BrokerProtocol.Version,
                    string.Empty,
                    BrokerStatus.Failed,
                    ex.Message);
            }

            try
            {
                await WriteAsync(pipe, response, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // The client may time out or exit after sending a valid request.
            }
        }
    }

    private async ValueTask<BrokerResponse> DispatchOnceAsync(
        BrokerRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.RequestId))
        {
            return await DispatchAsync(request, cancellationToken).ConfigureAwait(false);
        }

        var candidate = new CachedResponse(
            new Lazy<Task<BrokerResponse>>(
                () => DispatchAsync(request, _shutdown.Token).AsTask(),
                LazyThreadSafetyMode.ExecutionAndPublication),
            DateTimeOffset.UtcNow);
        var operation = _responses.GetOrAdd(request.RequestId, candidate);
        if (ReferenceEquals(operation, candidate))
        {
            _responseOrder.Enqueue(request.RequestId);
            while (_responses.Count > MaximumCachedResponses &&
                   _responseOrder.TryDequeue(out var expired))
            {
                if (_responses.TryGetValue(expired, out var cached) &&
                    (!cached.Operation.IsValueCreated ||
                     !cached.Operation.Value.IsCompleted ||
                     DateTimeOffset.UtcNow - cached.CreatedAt < ResponseRetryWindow))
                {
                    _responseOrder.Enqueue(expired);
                    break;
                }

                _responses.TryRemove(expired, out _);
            }
        }

        return await operation.Operation.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<BrokerResponse> DispatchAsync(
        BrokerRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ProtocolVersion != BrokerProtocol.Version)
        {
            return new(
                BrokerProtocol.Version,
                request.RequestId,
                BrokerStatus.VersionMismatch,
                $"Broker protocol {request.ProtocolVersion} is not supported.",
                Capabilities: BrokerProtocol.Capabilities);
        }

        if (!string.Equals(request.UserIdentity, BrokerIdentity.CurrentUser, StringComparison.Ordinal) ||
            !FixedTimeEquals(request.AuthenticationToken, Endpoint.AuthenticationToken))
        {
            return new(
                BrokerProtocol.Version,
                request.RequestId,
                BrokerStatus.Unauthorized,
                "Broker authentication failed.");
        }

        var result = await _handler.HandleAsync(
            request.TargetWindow,
            request.Payload,
            cancellationToken).ConfigureAwait(false);
        return new(
            BrokerProtocol.Version,
            request.RequestId,
            result.Status,
            result.Message,
            result.WindowId,
            result.WindowName,
            BrokerProtocol.Capabilities);
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length &&
               CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static string ElectionName(string instanceKey)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{BrokerIdentity.CurrentUser}|{instanceKey}|v{BrokerProtocol.Version}"));
        return $"Local\\WindowsTerminal.DotNet.Broker.{Convert.ToHexString(hash.AsSpan(0, 12))}";
    }

    private static async ValueTask<BrokerRequest> ReadAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var bytes = await ReadFrameAsync(stream, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize(bytes, BrokerJsonContext.Default.BrokerRequest)
            ?? throw new InvalidDataException("The broker request was empty.");
    }

    private static async ValueTask WriteAsync(
        Stream stream,
        BrokerResponse response,
        CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(response, BrokerJsonContext.Default.BrokerResponse);
        await WriteFrameAsync(stream, bytes, cancellationToken).ConfigureAwait(false);
    }

    internal static async ValueTask<byte[]> ReadFrameAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var header = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        var length = BitConverter.ToInt32(header);
        if (length <= 0 || length > BrokerProtocol.MaximumMessageBytes)
        {
            throw new InvalidDataException($"Invalid broker message length: {length}.");
        }

        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        return bytes;
    }

    internal static async ValueTask WriteFrameAsync(
        Stream stream,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken)
    {
        if (bytes.Length <= 0 || bytes.Length > BrokerProtocol.MaximumMessageBytes)
        {
            throw new InvalidDataException($"Invalid broker message length: {bytes.Length}.");
        }

        await stream.WriteAsync(BitConverter.GetBytes(bytes.Length), cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed record CachedResponse(
        Lazy<Task<BrokerResponse>> Operation,
        DateTimeOffset CreatedAt);
}

internal static class BrokerIdentity
{
    public static string CurrentUser { get; } =
        $"{Environment.UserDomainName}\\{Environment.UserName}";
}

internal sealed class BrokerElection : IDisposable
{
    private readonly ManualResetEventSlim _ready = new();
    private readonly ManualResetEventSlim _release = new();
    private readonly Thread _thread;
    private bool _acquired;

    private BrokerElection(string name)
    {
        _thread = new Thread(() => HoldMutex(name))
        {
            IsBackground = true,
            Name = "Windows Terminal broker election",
        };
        _thread.Start();
        _ready.Wait();
    }

    public static BrokerElection? TryAcquire(string name)
    {
        var election = new BrokerElection(name);
        if (election._acquired)
        {
            return election;
        }

        election.Dispose();
        return null;
    }

    public void Dispose()
    {
        _release.Set();
        _thread.Join();
        _ready.Dispose();
        _release.Dispose();
    }

    private void HoldMutex(string name)
    {
        using var mutex = new Mutex(initiallyOwned: false, name);
        try
        {
            try
            {
                _acquired = mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                _acquired = true;
            }

            _ready.Set();
            if (_acquired)
            {
                _release.Wait();
                mutex.ReleaseMutex();
            }
        }
        finally
        {
            _ready.Set();
        }
    }
}
