using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;

namespace Devolutions.Terminal.Broker;

public sealed class BrokerClient
{
    private readonly BrokerEndpointStore _endpointStore;

    public BrokerClient(string instanceKey = "default", string? endpointDirectory = null)
    {
        _endpointStore = new BrokerEndpointStore(endpointDirectory, instanceKey);
    }

    public async ValueTask<BrokerResponse> SendAsync(
        string targetWindow,
        string payload,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var endpoint = _endpointStore.Read();
        if (endpoint is null)
        {
            return BrokerResponse.Unavailable("No Devolutions Terminal broker is running.");
        }

        if (endpoint.ProtocolVersion != BrokerProtocol.Version)
        {
            return new(
                BrokerProtocol.Version,
                string.Empty,
                BrokerStatus.VersionMismatch,
                $"Endpoint protocol {endpoint.ProtocolVersion} is not supported.");
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout ?? TimeSpan.FromSeconds(2));
        var request = new BrokerRequest(
            BrokerProtocol.Version,
            Guid.NewGuid().ToString("N"),
            BrokerIdentity.CurrentUser,
            endpoint.AuthenticationToken,
            targetWindow,
            payload);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            request,
            BrokerJsonContext.Default.BrokerRequest);
        while (true)
        {
            try
            {
                await using var pipe = new NamedPipeClientStream(
                    ".",
                    endpoint.PipeName,
                    PipeDirection.InOut,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.ConnectAsync(linked.Token).ConfigureAwait(false);
                await BrokerHost.WriteFrameAsync(pipe, bytes, linked.Token).ConfigureAwait(false);
                var responseBytes = await BrokerHost.ReadFrameAsync(pipe, linked.Token)
                    .ConfigureAwait(false);
                return JsonSerializer.Deserialize(
                        responseBytes,
                        BrokerJsonContext.Default.BrokerResponse)
                    ?? BrokerResponse.Unavailable("The broker returned an empty response.");
            }
            catch (IOException) when (!linked.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(10), linked.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException ex)
                {
                    DeleteStaleEndpoint(endpoint);
                    return BrokerResponse.Unavailable(
                        $"The broker request timed out or was canceled: {ex.Message}");
                }
            }
            catch (OperationCanceledException ex)
            {
                DeleteStaleEndpoint(endpoint);
                return BrokerResponse.Unavailable(
                    $"The broker request timed out or was canceled: {ex.Message}");
            }
            catch (Exception ex) when (ex is IOException or TimeoutException)
            {
                DeleteStaleEndpoint(endpoint);
                return BrokerResponse.Unavailable(
                    $"The broker endpoint is stale or unavailable: {ex.Message}");
            }
        }
    }

    private void DeleteStaleEndpoint(BrokerEndpoint endpoint)
    {
        try
        {
            using var process = Process.GetProcessById(endpoint.ProcessId);
            if (!process.HasExited)
            {
                return;
            }
        }
        catch (ArgumentException)
        {
        }

        _endpointStore.DeleteIfMatches(endpoint);
    }
}
