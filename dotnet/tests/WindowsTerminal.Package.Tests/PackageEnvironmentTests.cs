using WindowsTerminal.Interop;
using WindowsTerminal.Package;
using Xunit;

namespace WindowsTerminal.Package.Tests;

public sealed class PackageEnvironmentTests
{
    [Fact]
    public void UnpackagedEnvironmentDoesNotExposeIdentityCapabilities()
    {
        var environment = PackageEnvironment.FromIdentity(PackageIdentity.Unpackaged);

        Assert.Equal(PackageCapability.None, environment.AvailableCapabilities);
        Assert.False(environment.Supports(PackageCapability.ExecutionAlias));
        Assert.Contains("requires MSIX package identity", environment.GetUnavailableReason(PackageCapability.ExecutionAlias));
    }

    [Fact]
    public void PackagedEnvironmentExposesOnlyInitialManifestIntegrations()
    {
        var environment = PackageEnvironment.FromIdentity(
            PackageIdentity.Packaged("Awakecoding.WindowsTerminal.Dev_0.1.0.0_x64__test"));

        Assert.True(environment.Supports(PackageCapability.PackageIdentity));
        Assert.True(environment.Supports(PackageCapability.ExecutionAlias));
        Assert.True(environment.Supports(PackageCapability.ProtocolActivation));
        Assert.False(environment.Supports(PackageCapability.Notifications));
        Assert.False(environment.Supports(PackageCapability.JumpList));
        Assert.False(environment.Supports(PackageCapability.DefaultTerminal));
        Assert.False(environment.Supports(PackageCapability.ShellVerb));
        Assert.Contains(
            "architecture-matched shell helper",
            environment.GetUnavailableReason(PackageCapability.Notifications));
    }

    [Fact]
    public void CurrentIdentityHasAFullNameOnlyWhenPackaged()
    {
        var identity = PackageIdentityDetector.GetCurrent();

        Assert.Equal(identity.IsPackaged, !string.IsNullOrWhiteSpace(identity.FullName));
    }
}
