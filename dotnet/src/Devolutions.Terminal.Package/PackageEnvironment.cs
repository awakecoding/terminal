using Devolutions.Terminal.Interop;

namespace Devolutions.Terminal.Package;

public sealed record PackageEnvironment(
    PackageIdentity Identity,
    PackageCapability AvailableCapabilities)
{
    private const PackageCapability ManifestCapabilities =
        PackageCapability.PackageIdentity |
        PackageCapability.ExecutionAlias |
        PackageCapability.ProtocolActivation;

    public static PackageEnvironment DetectCurrent() =>
        FromIdentity(
            PackageIdentityDetector.GetCurrent(),
            OperatingSystem.IsWindows()
                ? new WindowsShellIntegrationClient().GetCapabilities()
                : ShellIntegrationResult.Unsupported("Windows shell integrations are only available on Windows."));

    public static PackageEnvironment FromIdentity(
        PackageIdentity identity,
        ShellIntegrationResult? shell = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var capabilities = identity.IsPackaged ? ManifestCapabilities : PackageCapability.None;
        if (shell?.Succeeded == true)
        {
            if ((shell.Capabilities & ShellIntegrationCapability.SystemToast) != 0)
            {
                capabilities |= PackageCapability.Notifications;
            }
            if ((shell.Capabilities & ShellIntegrationCapability.JumpList) != 0)
            {
                capabilities |= PackageCapability.JumpList;
            }
            if (identity.IsPackaged &&
                (shell.Capabilities & ShellIntegrationCapability.ExplorerCommand) != 0)
            {
                capabilities |= PackageCapability.ShellVerb;
            }
            if ((shell.Capabilities & ShellIntegrationCapability.DefaultTerminalDelegation) != 0)
            {
                capabilities |= PackageCapability.DefaultTerminal;
            }
        }
        return new(
            identity,
            capabilities);
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

        if (!Identity.IsPackaged &&
            capability is PackageCapability.PackageIdentity or
                PackageCapability.ExecutionAlias or
                PackageCapability.ProtocolActivation or
                PackageCapability.ShellVerb or
                PackageCapability.DefaultTerminal)
        {
            return $"Capability '{capability}' requires MSIX package identity.";
        }

        return capability == PackageCapability.DefaultTerminal
            ? "Default-terminal delegation requires the OpenConsole handoff v3 proxy/stub and host, which are not bundled."
            : $"Capability '{capability}' requires the architecture-matched shell helper and a supported application identity.";
    }
}
