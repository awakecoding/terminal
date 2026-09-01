using System.Diagnostics;
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
        Assert.Equal(TerminalConnectionState.NotConnected, connection.State);
        Assert.True(connection.Capabilities.HasFlag(TerminalConnectionCapabilities.Restart));
        Assert.False(connection.Capabilities.HasFlag(TerminalConnectionCapabilities.Elevation));
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
        Assert.Equal(TerminalConnectionState.Closed, connection.State);
        Assert.NotNull(connection.ProcessMetadata);
        Assert.Equal(exitCode, connection.LastExitInfo?.ExitCode);
        Assert.Equal(TerminalExitReason.ProcessExited, connection.LastExitInfo?.Reason);
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
        Assert.Equal(TerminalExitReason.Cancelled, connection.LastExitInfo?.Reason);
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
            Assert.Equal(TerminalConnectionState.Disposed, connection.State);
        }
    }

    [Theory]
    [InlineData(TerminalCloseOnExitPolicy.Never, 0, false, false)]
    [InlineData(TerminalCloseOnExitPolicy.Graceful, 0, false, true)]
    [InlineData(TerminalCloseOnExitPolicy.Graceful, 1, false, false)]
    [InlineData(TerminalCloseOnExitPolicy.Always, 1, false, true)]
    [InlineData(TerminalCloseOnExitPolicy.Automatic, 1, false, false)]
    [InlineData(TerminalCloseOnExitPolicy.Automatic, 1, true, true)]
    public void EvaluatesCloseOnExitPolicy(
        TerminalCloseOnExitPolicy policy,
        int exitCode,
        bool isDefaultTerminalSession,
        bool expected)
    {
        Assert.Equal(
            expected,
            TerminalCloseOnExit.ShouldClose(
                policy,
                TerminalExitReason.ProcessExited,
                exitCode,
                isDefaultTerminalSession));
        Assert.False(
            TerminalCloseOnExit.ShouldClose(
                policy,
                TerminalExitReason.StartupFailure,
                exitCode,
                isDefaultTerminalSession));
    }

    [Theory]
    [InlineData(TerminalCloseOnExitPolicy.Never, false, false)]
    [InlineData(TerminalCloseOnExitPolicy.Graceful, false, false)]
    [InlineData(TerminalCloseOnExitPolicy.Always, false, true)]
    [InlineData(TerminalCloseOnExitPolicy.Automatic, false, false)]
    [InlineData(TerminalCloseOnExitPolicy.Automatic, true, true)]
    public void EvaluatesCloseOnConnectionFailure(
        TerminalCloseOnExitPolicy policy,
        bool isDefaultTerminalSession,
        bool expected)
    {
        Assert.Equal(
            expected,
            TerminalCloseOnExit.ShouldClose(
                policy,
                TerminalExitReason.ConnectionFailure,
                null,
                isDefaultTerminalSession));
    }

    [Fact]
    public async Task ExitInfoCarriesProcessMetadataAndCloseDecision()
    {
        await using var connection = new ConPtyConnection();
        var exited = new TaskCompletionSource<TerminalExitInfo>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        connection.SessionExited += (_, exit) => exited.TrySetResult(exit);

        await connection.StartAsync(new TerminalLaunchOptions
        {
            CommandLine = ExitCommand(7),
            WorkingDirectory = Environment.CurrentDirectory,
            Columns = 80,
            Rows = 24,
            CloseOnExit = TerminalCloseOnExitPolicy.Always,
        });
        var result = await exited.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(7, result.ExitCode);
        Assert.Equal(TerminalExitReason.ProcessExited, result.Reason);
        Assert.True(result.ShouldClose);
        Assert.Equal(Environment.CurrentDirectory, result.Process?.WorkingDirectory);
        Assert.True(result.Process?.ProcessId > 0);
        Assert.Equal(connection.ProcessMetadata, result.Process);
        Assert.Equal(TerminalConnectionState.Failed, connection.State);
    }

    [Fact]
    public async Task RestartsExitedSessionWithNewIdentity()
    {
        await using var connection = new ConPtyConnection();
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
        await connection.StartAsync(EchoCommand("first"), null, 80, 24);
        var first = await firstExit.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var secondExit = EnqueueExit(exits);
        await connection.RestartAsync(new TerminalLaunchOptions
        {
            CommandLine = EchoCommand("second"),
            Columns = 100,
            Rows = 40,
        });
        var second = await secondExit.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.NotEqual(first.Process?.SessionId, second.Process?.SessionId);
        Assert.NotEqual(first.Process?.ProcessId, second.Process?.ProcessId);
        Assert.Equal(100, connection.Columns);
        Assert.Equal(40, connection.Rows);
        Assert.Equal(TerminalConnectionState.Closed, connection.State);
    }

    [Fact]
    public async Task CloseDoesNotRequestPolicyDrivenTabClose()
    {
        await using var connection = new ConPtyConnection();
        var exited = new TaskCompletionSource<TerminalExitInfo>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        connection.SessionExited += (_, exit) => exited.TrySetResult(exit);
        await connection.StartAsync(new TerminalLaunchOptions
        {
            CommandLine = LongRunningCommand(),
            Columns = 80,
            Rows = 24,
            CloseOnExit = TerminalCloseOnExitPolicy.Always,
        });

        await connection.CloseAsync();
        var result = await exited.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(TerminalExitReason.Closed, result.Reason);
        Assert.False(result.ShouldClose);
        Assert.Equal(TerminalConnectionState.Closed, connection.State);
    }

    [Fact]
    public async Task CanRetryAfterStartupFailure()
    {
        await using var connection = new ConPtyConnection();

        await Assert.ThrowsAnyAsync<Exception>(
            () => connection.StartAsync(
                "\"Z:\\path-that-does-not-exist\\missing.exe\"",
                null,
                80,
                24));
        Assert.Equal(TerminalExitReason.StartupFailure, connection.LastExitInfo?.Reason);
        Assert.False(connection.LastExitInfo?.ShouldClose);

        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Exited += (_, code) => exited.TrySetResult(code);
        await connection.StartAsync(EchoCommand("recovered"), null, 80, 24);

        Assert.Equal(0, await exited.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task RepeatedSessionsDoNotLeakProcessHandles()
    {
        await RunShortSessionAsync();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using var currentProcess = Process.GetCurrentProcess();
        var baseline = currentProcess.HandleCount;

        for (var iteration = 0; iteration < 30; iteration++)
        {
            await RunShortSessionAsync();
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        currentProcess.Refresh();
        var final = currentProcess.HandleCount;
        Assert.InRange(final - baseline, int.MinValue, 12);
    }

    [Fact]
    public async Task ExitedSessionsReleaseHandlesBeforeConnectionDisposal()
    {
        await RunShortSessionAsync();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using var currentProcess = Process.GetCurrentProcess();
        var baseline = currentProcess.HandleCount;
        var connections = new List<ConPtyConnection>();

        try
        {
            for (var iteration = 0; iteration < 20; iteration++)
            {
                var connection = new ConPtyConnection();
                connections.Add(connection);
                var exited = new TaskCompletionSource<int>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                connection.Exited += (_, code) => exited.TrySetResult(code);
                await connection.StartAsync(ExitCommand(0), null, 80, 24);
                Assert.Equal(0, await exited.Task.WaitAsync(TimeSpan.FromSeconds(10)));
            }

            var deadline = DateTime.UtcNow.AddSeconds(10);
            int handleCount;
            do
            {
                await Task.Delay(25);
                currentProcess.Refresh();
                handleCount = currentProcess.HandleCount;
            }
            while (handleCount > baseline + 12 && DateTime.UtcNow < deadline);

            Assert.InRange(handleCount - baseline, int.MinValue, 12);
        }
        finally
        {
            foreach (var connection in connections)
            {
                await connection.DisposeAsync();
            }
        }
    }

    [Theory]
    [InlineData(@"\\wsl.localhost\Ubuntu\home\user\src", "Ubuntu", "/home/user/src")]
    [InlineData(@"\\wsl$\Debian", "Debian", "/")]
    public void ParsesWslUncPaths(string path, string distribution, string linuxPath)
    {
        Assert.True(WslPathTranslator.TryParseWindowsPath(path, out var result));
        Assert.Equal(distribution, result?.Distribution);
        Assert.Equal(linuxPath, result?.LinuxPath);
    }

    [Theory]
    [InlineData(@"C:\Users\name\source", "/mnt/c/Users/name/source")]
    [InlineData(@"D:\", "/mnt/d/")]
    [InlineData("/home/name", "/home/name")]
    public void TranslatesPathsForWsl(string path, string expected)
    {
        Assert.True(WslPathTranslator.TryToLinuxPath(path, out var linuxPath));
        Assert.Equal(expected, linuxPath);
    }

    [Fact]
    public void BuildsWslCommandLineWithTranslatedWorkingDirectory()
    {
        var commandLine = WslPathTranslator.BuildCommandLine(
            "Ubuntu",
            @"C:\Users\name\source",
            "bash -l");

        Assert.Equal(
            "wsl.exe --distribution \"Ubuntu\" --cd \"/mnt/c/Users/name/source\" --exec bash -l",
            commandLine);
        Assert.Equal(
            @"\\wsl.localhost\Ubuntu\home\name",
            WslPathTranslator.ToWindowsPath("Ubuntu", "/home/name"));
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

    private static string ExitCommand(int exitCode)
    {
        var comSpec = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
        return $"\"{comSpec}\" /d /s /c \"exit {exitCode}\"";
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

    private static async Task RunShortSessionAsync()
    {
        var connection = new ConPtyConnection();
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Exited += (_, code) => exited.TrySetResult(code);
        await connection.StartAsync(ExitCommand(0), null, 80, 24);
        Assert.Equal(0, await exited.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        await connection.DisposeAsync();
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
