using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WindowsTerminal.Interop;

public enum PackageDeploymentKind
{
    Unpackaged,
    Packaged,
}

public sealed record PackageIdentity(PackageDeploymentKind DeploymentKind, string? FullName)
{
    public bool IsPackaged => DeploymentKind == PackageDeploymentKind.Packaged;

    public static PackageIdentity Unpackaged { get; } =
        new(PackageDeploymentKind.Unpackaged, null);

    public static PackageIdentity Packaged(string fullName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullName);
        return new(PackageDeploymentKind.Packaged, fullName);
    }
}

public static partial class PackageIdentityDetector
{
    private const int ErrorSuccess = 0;
    private const int ErrorInsufficientBuffer = 122;
    private const int AppModelErrorNoPackage = 15700;

    public static PackageIdentity GetCurrent()
    {
        if (!OperatingSystem.IsWindows())
        {
            return PackageIdentity.Unpackaged;
        }

        uint length = 0;
        int result;
        unsafe
        {
            result = GetCurrentPackageFullName(&length, null);
        }

        if (result == AppModelErrorNoPackage)
        {
            return PackageIdentity.Unpackaged;
        }

        if (result != ErrorInsufficientBuffer)
        {
            throw new Win32Exception(result, "Unable to query the current package identity.");
        }

        var packageFullName = new char[checked((int)length)];
        unsafe
        {
            fixed (char* packageFullNamePointer = packageFullName)
            {
                result = GetCurrentPackageFullName(&length, packageFullNamePointer);
            }
        }

        if (result != ErrorSuccess)
        {
            throw new Win32Exception(result, "Unable to read the current package identity.");
        }

        var contentLength = Array.IndexOf(packageFullName, '\0');
        if (contentLength < 0)
        {
            contentLength = packageFullName.Length;
        }

        return PackageIdentity.Packaged(new string(packageFullName, 0, contentLength));
    }

    [LibraryImport("kernel32.dll")]
    private static unsafe partial int GetCurrentPackageFullName(
        uint* packageFullNameLength,
        char* packageFullName);
}
