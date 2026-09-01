using Microsoft.Terminal.Connection;
using System.Runtime.Versioning;
using Xunit;

namespace Terminal.Connection.Tests;

public sealed class ConnectionContractTests
{
    [SupportedOSPlatform("windows")]
    [Fact]
    public void ConPtyStartsStopped()
    {
        var connection = new ConPtyConnection();

        Assert.False(connection.IsRunning);
        Assert.Equal(0, connection.Columns);
        Assert.Equal(0, connection.Rows);
    }
}
