namespace WindowsTerminal.Interop;

[Flags]
public enum WindowsCapabilities
{
    None = 0,
    PackageIdentity = 1,
    Notifications = 2,
    DefaultTerminal = 4,
    ShellIntegration = 8,
}
