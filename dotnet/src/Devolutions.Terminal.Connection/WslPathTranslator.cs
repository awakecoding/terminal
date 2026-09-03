using System.Text;

namespace Devolutions.Terminal.Connection;

public sealed record WslPath(string Distribution, string LinuxPath);

public static class WslPathTranslator
{
    private const string LocalhostPrefix = @"\\wsl.localhost\";
    private const string LegacyPrefix = @"\\wsl$\";

    public static bool TryParseWindowsPath(string path, out WslPath? result)
    {
        ArgumentNullException.ThrowIfNull(path);

        var prefixLength = path.StartsWith(LocalhostPrefix, StringComparison.OrdinalIgnoreCase)
            ? LocalhostPrefix.Length
            : path.StartsWith(LegacyPrefix, StringComparison.OrdinalIgnoreCase)
                ? LegacyPrefix.Length
                : 0;
        if (prefixLength == 0)
        {
            result = null;
            return false;
        }

        var remainder = path[prefixLength..];
        var separator = remainder.IndexOf('\\');
        var distribution = separator < 0 ? remainder : remainder[..separator];
        if (string.IsNullOrWhiteSpace(distribution))
        {
            result = null;
            return false;
        }

        var linuxPath = separator < 0
            ? "/"
            : "/" + remainder[(separator + 1)..].Replace('\\', '/').TrimStart('/');
        result = new WslPath(distribution, linuxPath);
        return true;
    }

    public static bool TryToLinuxPath(string path, out string? linuxPath)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (path.StartsWith('/'))
        {
            linuxPath = path;
            return true;
        }

        if (TryParseWindowsPath(path, out var wslPath))
        {
            linuxPath = wslPath!.LinuxPath;
            return true;
        }

        if (path.Length >= 3 &&
            char.IsAsciiLetter(path[0]) &&
            path[1] == ':' &&
            (path[2] == '\\' || path[2] == '/'))
        {
            linuxPath = $"/mnt/{char.ToLowerInvariant(path[0])}/{path[3..].Replace('\\', '/')}";
            return true;
        }

        linuxPath = null;
        return false;
    }

    public static string ToWindowsPath(string distribution, string linuxPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(distribution);
        ArgumentException.ThrowIfNullOrWhiteSpace(linuxPath);
        if (!linuxPath.StartsWith('/'))
        {
            throw new ArgumentException("A WSL path must be absolute.", nameof(linuxPath));
        }

        var suffix = linuxPath.TrimStart('/').Replace('/', '\\');
        return suffix.Length == 0
            ? LocalhostPrefix + distribution
            : LocalhostPrefix + distribution + "\\" + suffix;
    }

    public static string BuildCommandLine(
        string? distribution = null,
        string? workingDirectory = null,
        string? command = null)
    {
        var builder = new StringBuilder("wsl.exe");
        if (!string.IsNullOrWhiteSpace(distribution))
        {
            builder.Append(" --distribution ").Append(Quote(distribution));
        }

        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            if (!TryToLinuxPath(workingDirectory, out var linuxPath))
            {
                throw new ArgumentException(
                    "The WSL working directory must be an absolute Linux, drive, or WSL UNC path.",
                    nameof(workingDirectory));
            }

            builder.Append(" --cd ").Append(Quote(linuxPath!));
        }

        if (!string.IsNullOrWhiteSpace(command))
        {
            builder.Append(" --exec ").Append(command);
        }

        return builder.ToString();
    }

    private static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
