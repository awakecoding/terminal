using System.IO.Pipes;
using WindowsTerminal.Broker;
using Xunit;

namespace WindowsTerminal.Broker.Tests;

public sealed class BrokerTests
{
    [Fact]
    public async Task ConcurrentClientsAreServedBySinglePrimary()
    {
        using var fixture = new BrokerFixture();
        await using var host = Assert.IsType<BrokerHost>(
            BrokerHost.TryCreate(new EchoHandler(), fixture.Key, fixture.Directory));
        Assert.Null(BrokerHost.TryCreate(new EchoHandler(), fixture.Key, fixture.Directory));

        var clients = Enumerable.Range(0, 24)
            .Select(async index => await new BrokerClient(fixture.Key, fixture.Directory)
                .SendAsync("use-any", $"payload-{index}"))
            .ToArray();
        var responses = await Task.WhenAll(clients);

        Assert.All(responses, response => Assert.Equal(BrokerStatus.Success, response.Status));
        Assert.Equal(
            Enumerable.Range(0, 24).Select(index => $"payload-{index}").Order(),
            responses.Select(response => response.Message).Order());
    }

    [Fact]
    public async Task AuthenticationTokenMismatchIsRejected()
    {
        using var fixture = new BrokerFixture();
        await using var host = Assert.IsType<BrokerHost>(
            BrokerHost.TryCreate(new EchoHandler(), fixture.Key, fixture.Directory));
        var store = new BrokerEndpointStore(fixture.Directory, fixture.Key);
        store.Write(host.Endpoint with { AuthenticationToken = "BADTOKEN" });

        var response = await new BrokerClient(fixture.Key, fixture.Directory)
            .SendAsync("use-any", "payload");

        Assert.Equal(BrokerStatus.Unauthorized, response.Status);
    }

    [Fact]
    public async Task StaleEndpointIsRemovedAndReported()
    {
        using var fixture = new BrokerFixture();
        var store = new BrokerEndpointStore(fixture.Directory, fixture.Key);
        store.Write(new(
            BrokerProtocol.Version,
            $"missing-{Guid.NewGuid():N}",
            "token",
            int.MaxValue));

        var response = await new BrokerClient(fixture.Key, fixture.Directory)
            .SendAsync("use-any", "payload", TimeSpan.FromMilliseconds(100));

        Assert.Equal(BrokerStatus.Unavailable, response.Status);
        Assert.False(File.Exists(store.EndpointPath));
    }

    [Fact]
    public void ServerEnforcesCurrentUserOnlyPipeOption()
    {
        Assert.True(BrokerHost.SecurePipeOptions.HasFlag(PipeOptions.CurrentUserOnly));
    }

    [Fact]
    public async Task RequestTimeoutDoesNotDeleteHealthyEndpoint()
    {
        using var fixture = new BrokerFixture();
        await using var host = Assert.IsType<BrokerHost>(
            BrokerHost.TryCreate(new DelayedHandler(), fixture.Key, fixture.Directory));
        var client = new BrokerClient(fixture.Key, fixture.Directory);

        var timedOut = await client.SendAsync(
            "use-any",
            "first",
            TimeSpan.FromMilliseconds(20));
        var second = await client.SendAsync(
            "use-any",
            "second",
            TimeSpan.FromSeconds(2));

        Assert.Equal(BrokerStatus.Unavailable, timedOut.Status);
        Assert.Equal(BrokerStatus.Success, second.Status);
        Assert.True(File.Exists(new BrokerEndpointStore(fixture.Directory, fixture.Key).EndpointPath));
    }

    [Fact]
    public async Task ProtocolMismatchReturnsCapabilities()
    {
        using var fixture = new BrokerFixture();
        await using var host = Assert.IsType<BrokerHost>(
            BrokerHost.TryCreate(new EchoHandler(), fixture.Key, fixture.Directory));
        var store = new BrokerEndpointStore(fixture.Directory, fixture.Key);
        store.Write(host.Endpoint with { ProtocolVersion = BrokerProtocol.Version + 1 });

        var response = await new BrokerClient(fixture.Key, fixture.Directory)
            .SendAsync("use-any", "payload");

        Assert.Equal(BrokerStatus.VersionMismatch, response.Status);
    }

    private sealed class EchoHandler : IBrokerRequestHandler
    {
        public ValueTask<BrokerDispatchResult> HandleAsync(
            string targetWindow,
            string payload,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new BrokerDispatchResult(BrokerStatus.Success, payload, 1, "test"));
    }

    private sealed class DelayedHandler : IBrokerRequestHandler
    {
        public async ValueTask<BrokerDispatchResult> HandleAsync(
            string targetWindow,
            string payload,
            CancellationToken cancellationToken)
        {
            await Task.Delay(100, cancellationToken);
            return new(BrokerStatus.Success, payload);
        }
    }

    private sealed class BrokerFixture : IDisposable
    {
        public BrokerFixture()
        {
            Directory = Path.Combine(Path.GetTempPath(), $"wt-broker-tests-{Guid.NewGuid():N}");
            System.IO.Directory.CreateDirectory(Directory);
        }

        public string Directory { get; }
        public string Key { get; } = Guid.NewGuid().ToString("N");

        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
}
