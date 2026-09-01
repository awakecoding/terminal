using System.IO.Pipes;
using System.Text.Json;

namespace WindowsTerminal.Broker;

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
            return BrokerResponse.Unavailable("No Windows Terminal broker is running.");
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
        var connected = false;
        try
        {
            await using var pipe = new NamedPipeClientStream(
                ".",
                endpoint.PipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(linked.Token).ConfigureAwait(false);
            connected = true;
            var request = new BrokerRequest(
                BrokerProtocol.Version,
                Guid.NewGuid().ToString("N"),
                BrokerIdentity.CurrentUser,
                endpoint.AuthenticationToken,
                targetWindow,
                payload);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(request, BrokerJsonContext.Default.BrokerRequest);
            await BrokerHost.WriteFrameAsync(pipe, bytes, linked.Token).ConfigureAwait(false);
            var responseBytes = await BrokerHost.ReadFrameAsync(pipe, linked.Token).ConfigureAwait(false);
            return JsonSerializer.Deserialize(responseBytes, BrokerJsonContext.Default.BrokerResponse)
                ?? BrokerResponse.Unavailable("The broker returned an empty response.");
        }
        catch (OperationCanceledException ex)
        {
            if (!connected)
            {
                _endpointStore.DeleteIfMatches(endpoint);
            }

            return BrokerResponse.Unavailable($"The broker request timed out or was canceled: {ex.Message}");
        }
        catch (Exception ex) when (ex is IOException or TimeoutException)
        {
            _endpointStore.DeleteIfMatches(endpoint);
            return BrokerResponse.Unavailable($"The broker endpoint is stale or unavailable: {ex.Message}");
        }
    }
}
