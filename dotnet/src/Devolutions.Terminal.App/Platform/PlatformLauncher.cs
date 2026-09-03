using System.Diagnostics;
using Devolutions.Terminal.Settings;
using Devolutions.Terminal.Package;

namespace Devolutions.Terminal.App.Platform;

public enum DesktopPlatform
{
    Auto,
    Windows,
    Linux,
    MacOS,
    Other,
}

public interface IPlatformLauncher
{
    void Open(string target);
    void OpenDirectory(string path);
    DesktopNotificationResult ShowNotification(string title, string body);
    ShellIntegrationResult RefreshJumpList(AppSettings settings);
    string GetCapabilityReport();
}

public sealed record DesktopNotificationResult(
    bool Attempted,
    bool Succeeded,
    string? Diagnostic = null);

public sealed class PlatformLauncher : IPlatformLauncher
{
    private readonly DesktopPlatform _platform;
    private readonly Func<ProcessStartInfo, Process?>? _startProcess;
    private readonly IDesktopCommandRunner _commandRunner;
    private readonly LinuxDesktopIntegration? _linux;
    private readonly IWindowsShellIntegrationService? _windowsShell;

    public PlatformLauncher(
        DesktopPlatform platform = DesktopPlatform.Auto,
        Func<ProcessStartInfo, Process?>? startProcess = null,
        LinuxDesktopCapabilities? linuxCapabilities = null,
        IDesktopCommandRunner? commandRunner = null,
        IWindowsShellIntegrationService? windowsShell = null)
    {
        _platform = platform == DesktopPlatform.Auto ? DetectPlatform() : platform;
        _startProcess = startProcess;
        _commandRunner = commandRunner ?? new BoundedDesktopCommandRunner();
        if (_platform == DesktopPlatform.Linux)
        {
            _linux = new LinuxDesktopIntegration(
                linuxCapabilities ?? LinuxDesktopCapabilities.Detect(),
                startProcess is null ? _commandRunner : new StartDelegateCommandRunner(startProcess));
        }
        else if (_platform == DesktopPlatform.Windows)
        {
            _windowsShell = windowsShell ?? new WindowsShellIntegrationClient();
        }
    }

    public void Open(string target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        if (_linux is not null)
        {
            _linux.Open(target);
            return;
        }

        using var process = (_startProcess ?? Process.Start)(CreateStartInfo(target))
            ?? throw new InvalidOperationException($"The desktop could not open '{target}'.");
    }

    public void OpenDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException(
                $"The directory '{fullPath}' does not exist.");
        }

        Open(fullPath);
    }

    public DesktopNotificationResult ShowNotification(string title, string body)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(body);
        if (_linux is not null)
        {
            return _linux.ShowNotification(title, body);
        }
        if (_windowsShell is not null)
        {
            var result = _windowsShell.PublishToast(new(title, body));
            return new DesktopNotificationResult(true, result.Succeeded, result.Diagnostic);
        }

        if (_platform == DesktopPlatform.MacOS)
        {
            return ShowMacOsNotification(title, body);
        }

        return new DesktopNotificationResult(false, false, "System notifications are unavailable on this platform.");
    }

    public ShellIntegrationResult RefreshJumpList(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (_windowsShell is null)
        {
            return ShellIntegrationResult.Unsupported(
                "Windows jump lists are unavailable on this platform.");
        }

        return _windowsShell.RefreshJumpList(settings.Profiles
            .Where(static profile => !profile.Hidden && !profile.Orphaned)
            .Select(static profile => new JumpListProfile(
                profile.Name,
                profile.Guid ?? string.Empty,
                profile.Icon)));
    }

    public string GetCapabilityReport()
    {
        if (_linux is not null)
        {
            return _linux.GetCapabilityReport();
        }

        return _platform switch
        {
            DesktopPlatform.Windows =>
                "Desktop platform: Windows\n" +
                "Open URI/file/directory: available through the registered shell\n" +
                "Global summon hotkeys: available through RegisterHotKey; collisions are reported per binding\n" +
                "Virtual desktop movement: unsupported by a stable public API; summon continues in place with a diagnostic\n" +
                "System menu: available through the native window menu\n" +
                GetWindowsCapabilityReport(),
            DesktopPlatform.MacOS =>
                "Desktop platform: macOS\n" +
                "Open URI/file/directory: available through open(1)\n" +
                "System notifications: available through osascript display notification\n" +
                "Global summon hotkeys: unsupported; use the broker or dt -w\n" +
                "Unix PTY: dt-pty-host (forkpty)\n" +
                "Ghostty engine: not bundled yet",
            _ => "Desktop platform: unsupported\nOpen and notification integrations are unavailable.",
        };
    }

    private string GetWindowsCapabilityReport()
    {
        var capability = _windowsShell!.GetCapabilities();
        var defaultTerminal = _windowsShell.DiagnoseDefaultTerminalDelegation();
        return
            $"Native shell helper: {capability.Status} ({capability.Diagnostic})\n" +
            $"Explorer command: {Availability(capability, ShellIntegrationCapability.ExplorerCommand)}\n" +
            $"Profile jump lists: {Availability(capability, ShellIntegrationCapability.JumpList)}\n" +
            $"System notifications: {Availability(capability, ShellIntegrationCapability.SystemToast)}\n" +
            $"Default terminal: {defaultTerminal.Status} ({defaultTerminal.Diagnostic})";
    }

    private static string Availability(
        ShellIntegrationResult result,
        ShellIntegrationCapability capability) =>
        result.Succeeded && (result.Capabilities & capability) != 0
            ? "available"
            : $"unavailable ({result.Diagnostic})";

    public ProcessStartInfo CreateStartInfo(string target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        if (_platform == DesktopPlatform.Linux)
        {
            return _linux!.CreatePreferredOpenStartInfo(target);
        }

        if (_platform == DesktopPlatform.MacOS)
        {
            var startInfo = CreateDirectStartInfo("open");
            startInfo.ArgumentList.Add(target);
            return startInfo;
        }

        return new ProcessStartInfo
        {
            FileName = target,
            UseShellExecute = true,
        };
    }

    private DesktopNotificationResult ShowMacOsNotification(string title, string body)
    {
        var startInfo = CreateDirectStartInfo("osascript");
        startInfo.ArgumentList.Add("-e");
        startInfo.ArgumentList.Add(
            $"display notification \"{EscapeAppleScript(body)}\" with title \"{EscapeAppleScript(title)}\"");
        try
        {
            var result = _commandRunner.Run(startInfo, TimeSpan.FromSeconds(3));
            return new DesktopNotificationResult(
                true,
                result.Succeeded,
                result.Succeeded ? null : result.Diagnostic);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new DesktopNotificationResult(true, false, ex.Message);
        }
    }

    private static string EscapeAppleScript(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);

    private static ProcessStartInfo CreateDirectStartInfo(string fileName) => new()
    {
        FileName = fileName,
        UseShellExecute = false,
        CreateNoWindow = true,
    };

    private static DesktopPlatform DetectPlatform()
    {
        if (OperatingSystem.IsWindows())
        {
            return DesktopPlatform.Windows;
        }

        if (OperatingSystem.IsLinux())
        {
            return DesktopPlatform.Linux;
        }

        if (OperatingSystem.IsMacOS())
        {
            return DesktopPlatform.MacOS;
        }

        return DesktopPlatform.Other;
    }

    private sealed class StartDelegateCommandRunner(
        Func<ProcessStartInfo, Process?> startProcess) : IDesktopCommandRunner
    {
        public DesktopCommandResult Run(ProcessStartInfo startInfo, TimeSpan timeout)
        {
            using var process = startProcess(startInfo);
            return process is null
                ? DesktopCommandResult.Failure($"Failed to start '{startInfo.FileName}'.")
                : DesktopCommandResult.Success();
        }
    }
}
