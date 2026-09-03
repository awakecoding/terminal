using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Devolutions.Terminal.App.Platform;

[Flags]
public enum LinuxDesktopCapability
{
    None = 0,
    PortalOpen = 1 << 0,
    XdgOpen = 1 << 1,
    PortalNotifications = 1 << 2,
    NotifySend = 1 << 3,
    XdgTerminalExec = 1 << 4,
    UpdateDesktopDatabase = 1 << 5,
    UpdateMimeDatabase = 1 << 6,
    DebianAlternatives = 1 << 7,
    GtkUpdateIconCache = 1 << 8,
    XdgMime = 1 << 9,
}

public sealed record LinuxDesktopCapabilities(
    LinuxDesktopCapability Available,
    string DesktopName,
    IReadOnlyList<string> Diagnostics)
{
    public bool Supports(LinuxDesktopCapability capability) =>
        (Available & capability) == capability;

    public static LinuxDesktopCapabilities Detect(
        Func<string, string?>? getEnvironmentVariable = null,
        Func<string, bool>? commandExists = null)
    {
        getEnvironmentVariable ??= Environment.GetEnvironmentVariable;
        commandExists ??= LinuxCommandLocator.IsAvailable;

        var available = LinuxDesktopCapability.None;
        var diagnostics = new List<string>();
        var hasSessionBus = !string.IsNullOrWhiteSpace(
            getEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS"));
        var hasGdbus = commandExists("gdbus");
        if (hasSessionBus && hasGdbus)
        {
            available |= LinuxDesktopCapability.PortalOpen |
                         LinuxDesktopCapability.PortalNotifications;
        }
        else
        {
            diagnostics.Add(hasSessionBus
                ? "Freedesktop portals are unavailable because gdbus is not installed."
                : "Freedesktop portals are unavailable because no D-Bus session bus was detected.");
        }

        AddCommandCapability(
            "xdg-open",
            LinuxDesktopCapability.XdgOpen,
            "Install xdg-utils to open URIs and files without a portal.",
            commandExists,
            ref available,
            diagnostics);
        AddCommandCapability(
            "notify-send",
            LinuxDesktopCapability.NotifySend,
            "Install a notify-send provider such as libnotify-bin for fallback notifications.",
            commandExists,
            ref available,
            diagnostics);
        AddCommandCapability(
            "xdg-terminal-exec",
            LinuxDesktopCapability.XdgTerminalExec,
            "xdg-terminal-exec is not installed; default-terminal selection may require distro alternatives.",
            commandExists,
            ref available,
            diagnostics);
        AddCommandCapability(
            "xdg-mime",
            LinuxDesktopCapability.XdgMime,
            "xdg-mime is not installed; dterm URI handler registration is unavailable.",
            commandExists,
            ref available,
            diagnostics);
        AddCommandCapability(
            "update-desktop-database",
            LinuxDesktopCapability.UpdateDesktopDatabase,
            "desktop-file-utils is not installed; application cache refresh is unavailable.",
            commandExists,
            ref available,
            diagnostics);
        AddCommandCapability(
            "update-mime-database",
            LinuxDesktopCapability.UpdateMimeDatabase,
            "shared-mime-info is not installed; MIME cache refresh is unavailable.",
            commandExists,
            ref available,
            diagnostics);
        AddCommandCapability(
            "update-alternatives",
            LinuxDesktopCapability.DebianAlternatives,
            "Debian alternatives are unavailable on this distribution.",
            commandExists,
            ref available,
            diagnostics);
        AddCommandCapability(
            "gtk-update-icon-cache",
            LinuxDesktopCapability.GtkUpdateIconCache,
            "gtk-update-icon-cache is not installed; icon cache refresh is unavailable.",
            commandExists,
            ref available,
            diagnostics);

        return new LinuxDesktopCapabilities(
            available,
            getEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "unknown",
            diagnostics);
    }

    private static void AddCommandCapability(
        string command,
        LinuxDesktopCapability capability,
        string unavailableDiagnostic,
        Func<string, bool> commandExists,
        ref LinuxDesktopCapability available,
        List<string> diagnostics)
    {
        if (commandExists(command))
        {
            available |= capability;
        }
        else
        {
            diagnostics.Add(unavailableDiagnostic);
        }
    }
}

public static class LinuxCommandLocator
{
    public static bool IsAvailable(string command) =>
        IsAvailable(command, Environment.GetEnvironmentVariable("PATH"), File.Exists);

    public static bool IsAvailable(
        string command,
        string? path,
        Func<string, bool> fileExists)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentNullException.ThrowIfNull(fileExists);
        if (command.Contains(Path.DirectorySeparatorChar) ||
            command.Contains(Path.AltDirectorySeparatorChar))
        {
            return false;
        }

        foreach (var directory in (path ?? string.Empty).Split(
                     Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (fileExists(Path.Combine(directory, command)))
            {
                return true;
            }
        }

        return false;
    }
}

public sealed record DesktopCommandResult(bool Succeeded, int? ExitCode, string Diagnostic)
{
    public static DesktopCommandResult Success() => new(true, 0, string.Empty);

    public static DesktopCommandResult Failure(string diagnostic, int? exitCode = null) =>
        new(false, exitCode, diagnostic);
}

public interface IDesktopCommandRunner
{
    DesktopCommandResult Run(ProcessStartInfo startInfo, TimeSpan timeout);
}

public sealed class BoundedDesktopCommandRunner : IDesktopCommandRunner
{
    public DesktopCommandResult Run(ProcessStartInfo startInfo, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.RedirectStandardError = true;
        startInfo.RedirectStandardOutput = true;

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return DesktopCommandResult.Failure(
                $"Could not start '{startInfo.FileName}': {ex.Message}");
        }

        if (process is null)
        {
            return DesktopCommandResult.Failure(
                $"Could not start '{startInfo.FileName}'.");
        }

        using (process)
        {
            if (!process.WaitForExit((int)Math.Min(int.MaxValue, timeout.TotalMilliseconds)))
            {
                string? terminationError = null;
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
                {
                    terminationError = $" The timed-out process could not be terminated: {ex.Message}";
                }

                return DesktopCommandResult.Failure(
                    $"'{startInfo.FileName}' did not finish within {timeout.TotalSeconds:0.#} seconds." +
                    terminationError);
            }

            var standardError = process.StandardError.ReadToEnd().Trim();
            var standardOutput = process.StandardOutput.ReadToEnd().Trim();
            if (process.ExitCode == 0)
            {
                return DesktopCommandResult.Success();
            }

            var detail = string.IsNullOrWhiteSpace(standardError)
                ? standardOutput
                : standardError;
            return DesktopCommandResult.Failure(
                string.IsNullOrWhiteSpace(detail)
                    ? $"'{startInfo.FileName}' exited with code {process.ExitCode}."
                    : $"'{startInfo.FileName}' exited with code {process.ExitCode}: {detail}",
                process.ExitCode);
        }
    }
}

public sealed class LinuxDesktopIntegration(
    LinuxDesktopCapabilities capabilities,
    IDesktopCommandRunner commandRunner)
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(3);

    public LinuxDesktopCapabilities Capabilities { get; } =
        capabilities ?? throw new ArgumentNullException(nameof(capabilities));

    public static bool TryNormalizeProtocolActivation(
        IReadOnlyList<string> args,
        out string[] normalized,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);
        normalized = args.ToArray();
        error = null;
        if (args.Count != 1 ||
            !Uri.TryCreate(args[0], UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals("dterm", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var action = string.IsNullOrWhiteSpace(uri.Host)
            ? uri.AbsolutePath.Trim('/')
            : uri.Host;
        if (!string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            (action.Length > 0 &&
             !action.Equals("open", StringComparison.OrdinalIgnoreCase) &&
             !action.Equals("new-tab", StringComparison.OrdinalIgnoreCase)))
        {
            error = "Unsupported dterm URI activation. Use dterm:, dterm:open, or dterm://new-tab.";
            return true;
        }

        normalized = [];
        return true;
    }

    public void Open(string target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        var attempts = new List<string>();
        if (Capabilities.Supports(LinuxDesktopCapability.PortalOpen))
        {
            var result = commandRunner.Run(CreatePortalOpenStartInfo(target), CommandTimeout);
            if (result.Succeeded)
            {
                return;
            }

            attempts.Add(result.Diagnostic);
        }

        if (Capabilities.Supports(LinuxDesktopCapability.XdgOpen))
        {
            var result = commandRunner.Run(CreateXdgOpenStartInfo(target), CommandTimeout);
            if (result.Succeeded)
            {
                return;
            }

            attempts.Add(result.Diagnostic);
        }

        var detail = attempts.Count == 0
            ? "Neither a freedesktop portal nor xdg-open is available. Install xdg-utils or start a desktop portal."
            : $"All desktop open providers failed: {string.Join(" ", attempts)}";
        throw new InvalidOperationException($"The Linux desktop could not open '{target}'. {detail}");
    }

    public DesktopNotificationResult ShowNotification(string title, string body)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(body);
        var attempts = new List<string>();
        if (Capabilities.Supports(LinuxDesktopCapability.PortalNotifications))
        {
            var result = commandRunner.Run(
                CreatePortalNotificationStartInfo(title, body),
                CommandTimeout);
            if (result.Succeeded)
            {
                return new DesktopNotificationResult(true, true);
            }

            attempts.Add(result.Diagnostic);
        }

        if (Capabilities.Supports(LinuxDesktopCapability.NotifySend))
        {
            var result = commandRunner.Run(
                CreateNotifySendStartInfo(title, body),
                CommandTimeout);
            if (result.Succeeded)
            {
                return new DesktopNotificationResult(true, true);
            }

            attempts.Add(result.Diagnostic);
        }

        var diagnostic = attempts.Count == 0
            ? "System notifications are unavailable. Start a freedesktop portal or install notify-send."
            : $"All Linux notification providers failed: {string.Join(" ", attempts)}";
        return new DesktopNotificationResult(attempts.Count > 0, false, diagnostic);
    }

    public ProcessStartInfo CreatePreferredOpenStartInfo(string target) =>
        Capabilities.Supports(LinuxDesktopCapability.PortalOpen)
            ? CreatePortalOpenStartInfo(target)
            : CreateXdgOpenStartInfo(target);

    public string GetCapabilityReport()
    {
        var report = new StringBuilder();
        report.AppendLine("Desktop platform: Linux");
        report.AppendLine($"Desktop environment: {Capabilities.DesktopName}");
        AppendCapability(
            report,
            "Open URI/file/directory",
            LinuxDesktopCapability.PortalOpen,
            LinuxDesktopCapability.XdgOpen);
        AppendCapability(
            report,
            "System notifications",
            LinuxDesktopCapability.PortalNotifications,
            LinuxDesktopCapability.NotifySend);
        report.AppendLine(
            $"xdg-terminal-exec default-terminal registration: " +
            $"{Availability(LinuxDesktopCapability.XdgTerminalExec)}");
        report.AppendLine(
            $"Debian alternatives default-terminal registration: " +
            $"{Availability(LinuxDesktopCapability.DebianAlternatives)}");
        report.AppendLine(
            $"dterm URI handler registration: {Availability(LinuxDesktopCapability.XdgMime)} " +
            "(metadata supplied; registration is an explicit installer action)");
        report.AppendLine(
            "Tray icon: desktop/backend dependent; freedesktop has no reliable capability probe");
        report.AppendLine(
            "Global summon/desktop shortcut portal: unsupported " +
            "(no reflection-free interactive GlobalShortcuts portal session provider is bundled; " +
            "configure a desktop shortcut to invoke 'wt -w <name>' or use broker/manual summon)");
        report.AppendLine(
            "Virtual desktop movement: compositor-specific and unsupported; summon continues in place with a diagnostic");
        foreach (var diagnostic in Capabilities.Diagnostics)
        {
            report.AppendLine($"- {diagnostic}");
        }

        return report.ToString().TrimEnd();
    }

    public static ProcessStartInfo CreatePortalOpenStartInfo(string target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        var startInfo = CreateDirectStartInfo("gdbus");
        startInfo.ArgumentList.Add("call");
        startInfo.ArgumentList.Add("--session");
        startInfo.ArgumentList.Add("--dest");
        startInfo.ArgumentList.Add("org.freedesktop.portal.Desktop");
        startInfo.ArgumentList.Add("--object-path");
        startInfo.ArgumentList.Add("/org/freedesktop/portal/desktop");
        startInfo.ArgumentList.Add("--method");
        startInfo.ArgumentList.Add("org.freedesktop.portal.OpenURI.OpenURI");
        startInfo.ArgumentList.Add(string.Empty);
        startInfo.ArgumentList.Add(ToPortalUri(target));
        startInfo.ArgumentList.Add("{}");
        return startInfo;
    }

    public static ProcessStartInfo CreateXdgOpenStartInfo(string target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        var startInfo = CreateDirectStartInfo("xdg-open");
        startInfo.ArgumentList.Add(target);
        return startInfo;
    }

    public static ProcessStartInfo CreatePortalNotificationStartInfo(
        string title,
        string body)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(body);
        var startInfo = CreateDirectStartInfo("gdbus");
        startInfo.ArgumentList.Add("call");
        startInfo.ArgumentList.Add("--session");
        startInfo.ArgumentList.Add("--dest");
        startInfo.ArgumentList.Add("org.freedesktop.portal.Desktop");
        startInfo.ArgumentList.Add("--object-path");
        startInfo.ArgumentList.Add("/org/freedesktop/portal/desktop");
        startInfo.ArgumentList.Add("--method");
        startInfo.ArgumentList.Add("org.freedesktop.portal.Notification.AddNotification");
        startInfo.ArgumentList.Add("devolutions-terminal");
        startInfo.ArgumentList.Add(
            $"{{'title': <'{EscapeGVariantString(title)}'>, " +
            $"'body': <'{EscapeGVariantString(body)}'>, 'priority': <'normal'>}}");
        return startInfo;
    }

    public static ProcessStartInfo CreateNotifySendStartInfo(string title, string body)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(body);
        var startInfo = CreateDirectStartInfo("notify-send");
        startInfo.ArgumentList.Add("--app-name=Devolutions Terminal");
        startInfo.ArgumentList.Add("--icon=com.devolutions.Terminal");
        startInfo.ArgumentList.Add(title);
        startInfo.ArgumentList.Add(body);
        return startInfo;
    }

    public static string EscapeGVariantString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var result = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            result.Append(character switch
            {
                '\\' => "\\\\",
                '\'' => "\\'",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                < ' ' => " ",
                _ => character.ToString(),
            });
        }

        return result.ToString();
    }

    private static string ToPortalUri(string target)
    {
        if (Uri.TryCreate(target, UriKind.Absolute, out var uri) &&
            uri.Scheme.Length > 1)
        {
            return uri.AbsoluteUri;
        }

        return new Uri(Path.GetFullPath(target)).AbsoluteUri;
    }

    private void AppendCapability(
        StringBuilder report,
        string name,
        LinuxDesktopCapability portal,
        LinuxDesktopCapability fallback)
    {
        var provider = Capabilities.Supports(portal)
            ? "available (freedesktop portal preferred)"
            : Capabilities.Supports(fallback)
                ? "available (fallback command)"
                : "unavailable";
        report.AppendLine($"{name}: {provider}");
    }

    private string Availability(LinuxDesktopCapability capability) =>
        Capabilities.Supports(capability) ? "available" : "unavailable";

    private static ProcessStartInfo CreateDirectStartInfo(string fileName) => new()
    {
        FileName = fileName,
        UseShellExecute = false,
        CreateNoWindow = true,
    };
}
