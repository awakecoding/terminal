using WindowsTerminal.Interop;

namespace WindowsTerminal.Package;

public sealed record PackageEnvironment(
    PackageIdentity Identity,
    PackageCapability AvailableCapabilities)
{
    private const PackageCapability InitialPackagedCapabilities =
        PackageCapability.PackageIdentity |
        PackageCapability.ExecutionAlias |
        PackageCapability.ProtocolActivation;

    public static PackageEnvironment DetectCurrent() =>
        FromIdentity(PackageIdentityDetector.GetCurrent());

    public static PackageEnvironment FromIdentity(PackageIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return new(
            identity,
            identity.IsPackaged ? InitialPackagedCapabilities : PackageCapability.None);
    }

    public bool Supports(PackageCapability capability) =>
        capability != PackageCapability.None &&
        (AvailableCapabilities & capability) == capability;

    public string GetUnavailableReason(PackageCapability capability)
    {
        if (Supports(capability))
        {
            throw new ArgumentException(
                $"Capability '{capability}' is available in the current environment.",
                nameof(capability));
        }

        if (!Identity.IsPackaged)
        {
            return $"Capability '{capability}' requires MSIX package identity.";
        }

        return $"Capability '{capability}' is not wired by the initial MSIX integration.";
    }
}
