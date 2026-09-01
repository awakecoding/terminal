using System.Runtime.Versioning;
using System.Text;
using Microsoft.Terminal.Connection;
using Xunit;

namespace Terminal.Connection.Tests;

[SupportedOSPlatform("windows")]
public sealed class ConnectionContractTests
{
    [Fact]
    public async Task ConPtyStartsStopped()
    {
        await using var connection = new ConPtyConnection();

        Assert.False(connection.IsRunning);
        Assert.Equal(0, connection.Columns);
        Assert.Equal(0, connection.Rows);
    }

    [Fact]
    public async Task WriteBeforeStartFails()
    {
        await using var connection = new ConPtyConnection();

        Assert.Throws<InvalidOperationException>(() => connection.Write("input"));
    }

    [Theory]
    [InlineData(0, 24)]
    [InlineData(80, 0)]
    [InlineData(32768, 24)]
    [InlineData(80, 32768)]
    public async Task RejectsInvalidDimensions(int columns, int rows)
    {
        await using var connection = new ConPtyConnection();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => connection.StartAsync("cmd.exe", null, columns, rows));
    }

    [Fact]
    public async Task CannotStartTwice()
    {
        await using var connection = new ConPtyConnection();
        await connection.StartAsync(EchoCommand("ready"), null, 80, 24);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => connection.StartAsync(EchoCommand("again"), null, 80, 24));
    }

    [Fact]
    public async Task CapturesUnicodeOutputAndExitCode()
    {
        await using var connection = new ConPtyConnection();
        var output = new List<byte>();
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var faulted = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.OutputReceived += (_, bytes) =>
        {
            lock (output)
            {
                output.AddRange(bytes.ToArray());
            }
        };
        connection.Exited += (_, code) => exited.TrySetResult(code);
        connection.Faulted += (_, error) => faulted.TrySetResult(error);

        await connection.StartAsync(CommandPrompt(), null, 80, 24);
        connection.Write("chcp 65001>nul\r");
        connection.Write("echo héllo\r");
        connection.Write("exit\r");
        var exitCode = await exited.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, exitCode);
        await WaitForOutputAsync(output, "héllo");

        Assert.False(faulted.Task.IsCompleted);
        Assert.False(connection.IsRunning);
    }

    [Fact]
    public async Task ResizesRunningPseudoConsole()
    {
        await using var connection = new ConPtyConnection();
        var output = new List<byte>();
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.OutputReceived += (_, bytes) =>
        {
            lock (output)
            {
                output.AddRange(bytes.ToArray());
            }
        };
        connection.Exited += (_, code) => exited.TrySetResult(code);
        await connection.StartAsync(CommandPrompt(), null, 80, 24);

        connection.Resize(132, 43);
        connection.Write("mode con\r");
        connection.Write("exit\r");
        _ = await exited.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await WaitForOutputAsync(output, "132");
        await WaitForOutputAsync(output, "43");

        Assert.Equal(132, connection.Columns);
        Assert.Equal(43, connection.Rows);
    }

    [Fact]
    public async Task CancellationStopsProcess()
    {
        using var cancellation = new CancellationTokenSource();
        await using var connection = new ConPtyConnection();
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Exited += (_, code) => exited.TrySetResult(code);
        await connection.StartAsync(LongRunningCommand(), null, 80, 24, cancellation.Token);

        await cancellation.CancelAsync();
        _ = await exited.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(connection.IsRunning);
    }

    [Fact]
    public async Task AppliesEnvironmentOverrides()
    {
        await using var connection = new ConPtyConnection();
        var output = new List<byte>();
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.OutputReceived += (_, bytes) =>
        {
            lock (output)
            {
                output.AddRange(bytes.ToArray());
            }
        };
        connection.Exited += (_, code) => exited.TrySetResult(code);

        await connection.StartAsync(new TerminalLaunchOptions
        {
            CommandLine = CommandPrompt(),
            Columns = 80,
            Rows = 24,
            EnvironmentVariables = new Dictionary<string, string?>
            {
                ["WT_DOTNET_TEST_VALUE"] = "profile-value",
            },
        });
        connection.Write("echo %WT_DOTNET_TEST_VALUE%\r");
        connection.Write("exit\r");

        Assert.Equal(0, await exited.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        await WaitForOutputAsync(output, "profile-value");
    }

    [Fact]
    public async Task ConcurrentStartAndDisposeCannotPublishAfterDisposal()
    {
        for (var iteration = 0; iteration < 20; iteration++)
        {
            var connection = new ConPtyConnection();
            var start = Task.Run(async () =>
            {
                try
                {
                    await connection.StartAsync(LongRunningCommand(), null, 80, 24);
                }
                catch (ObjectDisposedException)
                {
                }
            });
            var dispose = Task.Run(async () => await connection.DisposeAsync());

            await Task.WhenAll(start, dispose).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(connection.IsRunning);
        }
    }

    private static string EchoCommand(string value)
    {
        var comSpec = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
        return $"\"{comSpec}\" /d /s /c \"echo {value}\"";
    }

    private static string CommandPrompt()
    {
        var comSpec = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
        return $"\"{comSpec}\" /d /q";
    }

    private static string LongRunningCommand()
    {
        var comSpec = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
        return $"\"{comSpec}\" /d /s /c \"ping 127.0.0.1 -n 30 > nul\"";
    }

    private static async Task WaitForOutputAsync(List<byte> output, string expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            lock (output)
            {
                if (Encoding.UTF8.GetString([.. output]).Contains(expected, StringComparison.Ordinal))
                {
                    return;
                }
            }

            await Task.Delay(25);
        }

        lock (output)
        {
            Assert.Contains(expected, Encoding.UTF8.GetString([.. output]), StringComparison.Ordinal);
        }
    }
}
