using System.Runtime.Versioning;
using System.Text;
using Microsoft.Terminal.Connection;
using Xunit;

namespace Terminal.Connection.Tests;

[SupportedOSPlatform("linux")]
public sealed class LinuxPtyConnectionTests
{
    public static bool IsLinux => OperatingSystem.IsLinux();

    [Fact]
    public async Task RealPtySupportsInputResizeAndExit()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        await using var connection = new LinuxPtyConnection();
        var output = new StringBuilder();
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.OutputReceived += (_, data) =>
        {
            lock (output)
            {
                output.Append(Encoding.UTF8.GetString(data.Span));
                changed.TrySetResult();
            }
        };
        await connection.StartAsync(new TerminalLaunchOptions
        {
            CommandLine = "/bin/bash --noprofile --norc",
            WorkingDirectory = "/tmp",
            Columns = 80,
            Rows = 24,
            CloseOnExit = TerminalCloseOnExitPolicy.Never,
        }, TestContext.Current.CancellationToken);

        connection.Write("printf 'LINUX_PTY_OK:%s\\n' \"$PWD\"\r");
        await WaitForAsync(
            () => Snapshot().Contains("LINUX_PTY_OK:/tmp", StringComparison.Ordinal),
            changed,
            TestContext.Current.CancellationToken);

        changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Resize(100, 40);
        connection.Write("stty size\r");
        await WaitForAsync(
            () => Snapshot().Contains("40 100", StringComparison.Ordinal),
            changed,
            TestContext.Current.CancellationToken);

        Assert.True(connection.IsRunning);
        Assert.Equal(100, connection.Columns);
        Assert.Equal(40, connection.Rows);
        await connection.CloseAsync(TestContext.Current.CancellationToken);
        Assert.False(connection.IsRunning);

        string Snapshot()
        {
            lock (output)
            {
                return output.ToString();
            }
        }
    }

    [Fact(Skip = "Linux PTY is Linux-only.", SkipUnless = nameof(IsLinux))]
    public async Task ReportsExitCodeAndRestartsWithNewSession()
    {
        await using var connection = new LinuxPtyConnection();
        var exits = new Queue<TaskCompletionSource<TerminalExitInfo>>();
        connection.SessionExited += (_, exit) =>
        {
            lock (exits)
            {
                if (exits.Count > 0)
                {
                    exits.Dequeue().TrySetResult(exit);
                }
            }
        };

        var firstExit = EnqueueExit(exits);
        await connection.StartAsync(
            "printf 'FIRST_SESSION\\n'",
            "/tmp",
            80,
            24,
            TestContext.Current.CancellationToken);
        var first = await firstExit.Task.WaitAsync(
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);

        var secondExit = EnqueueExit(exits);
        await connection.RestartAsync(
            new TerminalLaunchOptions
            {
                CommandLine = "exit 7",
                WorkingDirectory = "/tmp",
                Columns = 100,
                Rows = 40,
            },
            TestContext.Current.CancellationToken);
        var second = await secondExit.Task.WaitAsync(
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, first.ExitCode);
        Assert.Equal(7, second.ExitCode);
        Assert.NotEqual(first.Process?.SessionId, second.Process?.SessionId);
        Assert.Equal(TerminalExitReason.ProcessExited, second.Reason);
        Assert.Equal(TerminalConnectionState.Failed, connection.State);
        Assert.Equal(100, connection.Columns);
        Assert.Equal(40, connection.Rows);
    }

    [Fact(Skip = "Linux PTY is Linux-only.", SkipUnless = nameof(IsLinux))]
    public async Task LaunchCancellationTerminatesProcessTree()
    {
        using var cancellation = new CancellationTokenSource();
        await using var connection = new LinuxPtyConnection();
        var exited = new TaskCompletionSource<TerminalExitInfo>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        connection.SessionExited += (_, exit) => exited.TrySetResult(exit);

        await connection.StartAsync(
            "sleep 30",
            "/tmp",
            80,
            24,
            cancellation.Token);
        await cancellation.CancelAsync();
        var result = await exited.Task.WaitAsync(
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);

        Assert.Equal(TerminalExitReason.Cancelled, result.Reason);
        Assert.False(connection.IsRunning);
        Assert.Equal(TerminalConnectionState.Closed, connection.State);
    }

    [Theory(Skip = "Linux PTY is Linux-only.", SkipUnless = nameof(IsLinux))]
    [InlineData(0, 24)]
    [InlineData(80, 0)]
    [InlineData(65536, 24)]
    [InlineData(80, 65536)]
    public async Task RejectsInvalidDimensions(int columns, int rows)
    {
        await using var connection = new LinuxPtyConnection();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => connection.StartAsync(
                "true",
                "/tmp",
                columns,
                rows,
                TestContext.Current.CancellationToken));
    }

    private static TaskCompletionSource<TerminalExitInfo> EnqueueExit(
        Queue<TaskCompletionSource<TerminalExitInfo>> exits)
    {
        var completion = new TaskCompletionSource<TerminalExitInfo>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (exits)
        {
            exits.Enqueue(completion);
        }

        return completion;
    }

    private static async Task WaitForAsync(
        Func<bool> condition,
        TaskCompletionSource changed,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("Timed out waiting for Linux PTY output.");
            }

            await Task.WhenAny(
                changed.Task,
                Task.Delay(100, cancellationToken)).ConfigureAwait(false);
        }
    }
}
