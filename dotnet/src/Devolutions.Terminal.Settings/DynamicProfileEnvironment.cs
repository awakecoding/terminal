using System.Runtime.InteropServices;

namespace Devolutions.Terminal.Settings;

public sealed class DynamicProfileEnvironment
{
    public bool IsWindows { get; init; } = OperatingSystem.IsWindows();
    public bool IsLinux { get; init; } = OperatingSystem.IsLinux();
    public string? Shell { get; init; } = Environment.GetEnvironmentVariable("SHELL");
    public Func<string, bool> FileExists { get; init; } = File.Exists;
    public Func<string, IEnumerable<string>> EnumerateDirectories { get; init; } =
        path => Directory.Exists(path) ? Directory.EnumerateDirectories(path) : [];
    public Func<string, IEnumerable<string>> ReadLines { get; init; } = File.ReadLines;
    public Func<string, string> ExpandEnvironmentVariables { get; init; } =
        Environment.ExpandEnvironmentVariables;
    public Func<string, string?> ResolveExecutable { get; init; } = ResolveFromPath;
    public string ProgramFiles { get; init; } =
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    public string ProgramFilesX86 { get; init; } =
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
    public string UserProfile { get; init; } =
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public string LocalApplicationData { get; init; } =
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    public string ProgramData { get; init; } =
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    public string SystemDirectory { get; init; } = Environment.SystemDirectory;
    public Architecture ProcessArchitecture { get; init; } = RuntimeInformation.ProcessArchitecture;
    public bool EnableSshProfiles { get; init; } =
        string.Equals(
            Environment.GetEnvironmentVariable("WT_ENABLE_SSH_PROFILES"),
            "1",
            StringComparison.Ordinal);

    private static string? ResolveFromPath(string executable)
    {
        if (Path.IsPathFullyQualified(executable))
        {
            return File.Exists(executable) ? executable : null;
        }

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(directory.Trim('"'), executable);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
