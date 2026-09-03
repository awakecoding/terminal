namespace WindowsTerminal.Package;

[Flags]
public enum ShellIntegrationCapability
{
    None = 0,
    ExplorerCommand = 1 << 0,
    JumpList = 1 << 1,
    SystemToast = 1 << 2,
    DefaultTerminalDelegation = 1 << 3,
}

public enum ShellIntegrationStatus
{
    Success,
    Unsupported,
    Failed,
    InvalidRequest,
    Unauthorized,
    VersionMismatch,
}

public sealed record ShellIntegrationResult(
    ShellIntegrationStatus Status,
    string Diagnostic,
    ShellIntegrationCapability Capabilities = ShellIntegrationCapability.None)
{
    public bool Succeeded => Status == ShellIntegrationStatus.Success;

    public static ShellIntegrationResult Unsupported(string diagnostic) =>
        new(ShellIntegrationStatus.Unsupported, diagnostic);
}

public sealed record JumpListProfile(string Name, string Guid, string? Icon = null);

public sealed record SystemToastRequest(
    string Title,
    string Body,
    string TargetWindow = "use-any",
    string? NotificationId = null);

public interface IWindowsShellIntegrationService
{
    ShellIntegrationResult GetCapabilities();
    ShellIntegrationResult RefreshJumpList(IEnumerable<JumpListProfile> profiles);
    ShellIntegrationResult PublishToast(SystemToastRequest request);
    ShellIntegrationResult DiagnoseDefaultTerminalDelegation();
}

public sealed record ShellHelperInvocation(
    string ExecutablePath,
    string AuthenticationToken,
    string Request,
    TimeSpan Timeout);

public sealed record ShellHelperProcessResult(
    bool Started,
    int ExitCode,
    string StandardOutput,
    string StandardError,
    string? Diagnostic = null);

public interface IShellHelperProcessRunner
{
    ShellHelperProcessResult Run(ShellHelperInvocation invocation);
}
