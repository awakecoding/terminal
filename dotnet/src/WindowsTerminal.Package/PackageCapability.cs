namespace WindowsTerminal.Package;

[Flags]
public enum PackageCapability
{
    None = 0,
    PackageIdentity = 1 << 0,
    ExecutionAlias = 1 << 1,
    ProtocolActivation = 1 << 2,
    Notifications = 1 << 3,
    JumpList = 1 << 4,
    DefaultTerminal = 1 << 5,
    ShellVerb = 1 << 6,
}
