namespace Devolutions.Terminal.Connection;

[Flags]
public enum TerminalConnectionCapabilities
{
    None = 0,
    Resize = 1 << 0,
    Restart = 1 << 1,
    ProcessMetadata = 1 << 2,
    WslPathTranslation = 1 << 3,
    Elevation = 1 << 4,
}

public enum TerminalConnectionState
{
    NotConnected,
    Connecting,
    Connected,
    Closing,
    Closed,
    Failed,
    Disposed,
}

public enum TerminalCloseOnExitPolicy
{
    Never,
    Graceful,
    Always,
    Automatic,
}

public enum TerminalExitReason
{
    ProcessExited,
    Cancelled,
    Closed,
    Disposed,
    StartupFailure,
    ConnectionFailure,
}

public sealed record TerminalProcessMetadata(
    Guid SessionId,
    int ProcessId,
    string CommandLine,
    string WorkingDirectory,
    DateTimeOffset StartedAt);

public sealed record TerminalExitInfo(
    TerminalProcessMetadata? Process,
    int? ExitCode,
    TerminalExitReason Reason,
    bool ShouldClose,
    DateTimeOffset ExitedAt)
{
    public bool ExitedGracefully =>
        Reason == TerminalExitReason.ProcessExited && ExitCode == 0;
}

public static class TerminalCloseOnExit
{
    public static bool ShouldClose(
        TerminalCloseOnExitPolicy policy,
        TerminalExitReason reason,
        int? exitCode,
        bool isDefaultTerminalSession = false)
    {
        if (reason is TerminalExitReason.StartupFailure or
            TerminalExitReason.Cancelled or
            TerminalExitReason.Closed or
            TerminalExitReason.Disposed)
        {
            return false;
        }

        return policy switch
        {
            TerminalCloseOnExitPolicy.Never => false,
            TerminalCloseOnExitPolicy.Always => true,
            TerminalCloseOnExitPolicy.Graceful =>
                reason == TerminalExitReason.ProcessExited && exitCode == 0,
            TerminalCloseOnExitPolicy.Automatic =>
                (reason == TerminalExitReason.ProcessExited && exitCode == 0) ||
                isDefaultTerminalSession,
            _ => false,
        };
    }
}
