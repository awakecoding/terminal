using System.Diagnostics;
using System.Text;

namespace Microsoft.Terminal.Settings;

public sealed record DynamicProfileCommand(
    string FileName,
    IReadOnlyList<string> Arguments,
    TimeSpan Timeout,
    Encoding? StandardOutputEncoding = null);

public sealed record DynamicProfileCommandResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut);

public interface IDynamicProfileCommandRunner
{
    ValueTask<DynamicProfileCommandResult> RunAsync(
        DynamicProfileCommand command,
        CancellationToken cancellationToken);
}

public sealed class DynamicProfileCommandRunner : IDynamicProfileCommandRunner
{
    public async ValueTask<DynamicProfileCommandResult> RunAsync(
        DynamicProfileCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        using var process = new Process
        {
            StartInfo = CreateStartInfo(command),
        };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Could not start '{command.FileName}'.");
        }

        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = new CancellationTokenSource(command.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            await Task.WhenAll(output, error).WaitAsync(linked.Token).ConfigureAwait(false);
            return new DynamicProfileCommandResult(
                process.ExitCode,
                output.Result,
                error.Result,
                TimedOut: false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            return new DynamicProfileCommandResult(
                -1,
                output.IsCompletedSuccessfully ? output.Result : string.Empty,
                error.IsCompletedSuccessfully ? error.Result : string.Empty,
                TimedOut: true);
        }
        catch
        {
            TryKill(process);
            throw;
        }
    }

    private static ProcessStartInfo CreateStartInfo(DynamicProfileCommand command)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = command.FileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = command.StandardOutputEncoding ?? Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in command.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }
}
