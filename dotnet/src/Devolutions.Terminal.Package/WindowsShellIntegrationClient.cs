using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Devolutions.Terminal.Interop;

namespace Devolutions.Terminal.Package;

public sealed class WindowsShellIntegrationClient : IWindowsShellIntegrationService
{
    public const int ProtocolVersion = 1;
    public const int MaximumResponseBytes = 64 * 1024;
    public const string PackagedApplicationId = "Terminal";
    private const int MaximumProfiles = 64;

    private readonly IShellHelperProcessRunner _runner;
    private readonly string _helperPath;
    private readonly Func<PackageIdentity> _identity;
    private readonly Func<string?> _packageFamilyName;
    private readonly Func<string, string?> _environment;

    public WindowsShellIntegrationClient(
        IShellHelperProcessRunner? runner = null,
        string? helperPath = null,
        Func<PackageIdentity>? identity = null,
        Func<string?>? packageFamilyName = null,
        Func<string, string?>? environment = null)
    {
        _runner = runner ?? new ShellHelperProcessRunner();
        _helperPath = helperPath ?? Path.Combine(AppContext.BaseDirectory, "dt-shell-integration.exe");
        _identity = identity ?? PackageIdentityDetector.GetCurrent;
        _packageFamilyName = packageFamilyName ?? PackageIdentityDetector.GetCurrentPackageFamilyName;
        _environment = environment ?? Environment.GetEnvironmentVariable;
    }

    public ShellIntegrationResult GetCapabilities()
    {
        var result = Invoke("capabilities");
        if (!result.Succeeded)
        {
            return result;
        }

        var capabilities = result.Capabilities;
        var diagnostics = new List<string> { result.Diagnostic };
        if (!TryGetApplicationUserModelId(
                requireShortcut: false,
                out _,
                out var jumpListUnavailable))
        {
            capabilities &= ~(ShellIntegrationCapability.JumpList | ShellIntegrationCapability.SystemToast);
            diagnostics.Add(jumpListUnavailable.Diagnostic);
        }
        else if (!_identity().IsPackaged &&
                 !TryGetApplicationUserModelId(
                     requireShortcut: true,
                     out _,
                     out var toastUnavailable))
        {
            capabilities &= ~ShellIntegrationCapability.SystemToast;
            diagnostics.Add(toastUnavailable.Diagnostic);
        }

        return result with
        {
            Capabilities = capabilities,
            Diagnostic = string.Join(" ", diagnostics),
        };
    }

    public ShellIntegrationResult DiagnoseDefaultTerminalDelegation() =>
        Invoke("default-terminal");

    public ShellIntegrationResult RefreshJumpList(IEnumerable<JumpListProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        if (!TryGetApplicationUserModelId(requireShortcut: false, out var appId, out var unavailable))
        {
            return unavailable;
        }

        var normalized = new List<JumpListProfile>();
        foreach (var profile in profiles)
        {
            if (normalized.Count == MaximumProfiles)
            {
                break;
            }
            if (string.IsNullOrWhiteSpace(profile.Name) ||
                profile.Name.Length > 128 ||
                !Guid.TryParse(profile.Guid, out var guid))
            {
                return new(
                    ShellIntegrationStatus.InvalidRequest,
                    "Every jump-list profile requires a name of at most 128 characters and a valid GUID.");
            }
            normalized.Add(profile with { Guid = guid.ToString("B") });
        }

        if (normalized.Count == 0)
        {
            return new(
                ShellIntegrationStatus.InvalidRequest,
                "At least one visible profile is required to refresh the jump list.");
        }

        var request = NewRequest("jump-list");
        AddEncoded(request, "aumid", appId);
        AddEncoded(request, "executable", Path.Combine(AppContext.BaseDirectory, "Devolutions.Terminal.exe"));
        foreach (var profile in normalized)
        {
            request.Append("profile=")
                .Append(Encode(profile.Name)).Append('|')
                .Append(Encode(profile.Guid)).Append('|')
                .Append(Encode(profile.Icon ?? string.Empty)).AppendLine();
        }
        return Invoke(request);
    }

    public ShellIntegrationResult PublishToast(SystemToastRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Title) ||
            request.Title.Length > 256 ||
            request.Body.Length > 4096)
        {
            return new(
                ShellIntegrationStatus.InvalidRequest,
                "Toast title/body limits are 256 and 4096 characters.");
        }
        if (!TryGetApplicationUserModelId(requireShortcut: true, out var appId, out var unavailable))
        {
            return unavailable;
        }

        string activation;
        try
        {
            activation = ToastActivationCodec.Create(request.TargetWindow, request.NotificationId);
        }
        catch (ArgumentException ex)
        {
            return new(ShellIntegrationStatus.InvalidRequest, ex.Message);
        }

        var requestText = NewRequest("toast");
        AddEncoded(requestText, "aumid", appId);
        AddEncoded(requestText, "title", request.Title);
        AddEncoded(requestText, "body", request.Body);
        AddEncoded(requestText, "activation", activation);
        AddEncoded(
            requestText,
            "tag",
            request.NotificationId ?? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.Title)))[..16]);
        return Invoke(requestText);
    }

    internal static ShellIntegrationResult ParseResponse(string response)
    {
        if (Encoding.UTF8.GetByteCount(response) > MaximumResponseBytes)
        {
            return new(ShellIntegrationStatus.Failed, "The shell helper response exceeds 64 KiB.");
        }

        var protocol = 0;
        string? status = null;
        string? diagnostic = null;
        var capabilities = ShellIntegrationCapability.None;
        var ended = false;
        foreach (var rawLine in response.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }
            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                return new(ShellIntegrationStatus.Failed, "The shell helper returned a malformed line.");
            }
            var key = line[..separator];
            var value = line[(separator + 1)..];
            switch (key)
            {
                case "protocol" when protocol == 0 && int.TryParse(value, out var version):
                    protocol = version;
                    break;
                case "status" when status is null:
                    status = value;
                    break;
                case "diagnostic" when diagnostic is null:
                    try
                    {
                        diagnostic = Encoding.UTF8.GetString(Base64Url.Decode(value));
                    }
                    catch (FormatException ex)
                    {
                        return new(ShellIntegrationStatus.Failed, $"The helper diagnostic is invalid: {ex.Message}");
                    }
                    break;
                case "capability":
                    capabilities |= value switch
                    {
                        "explorer-command.v1" => ShellIntegrationCapability.ExplorerCommand,
                        "jump-list.v1" => ShellIntegrationCapability.JumpList,
                        "toast.v1" => ShellIntegrationCapability.SystemToast,
                        "default-terminal-delegation.v1" => ShellIntegrationCapability.DefaultTerminalDelegation,
                        _ => ShellIntegrationCapability.None,
                    };
                    break;
                case "end" when value == "1":
                    ended = true;
                    break;
                default:
                    return new(ShellIntegrationStatus.Failed, $"The shell helper returned an unexpected '{key}' field.");
            }
        }

        if (!ended || status is null || diagnostic is null)
        {
            return new(ShellIntegrationStatus.Failed, "The shell helper response is incomplete.");
        }
        if (protocol != ProtocolVersion)
        {
            return new(
                ShellIntegrationStatus.VersionMismatch,
                $"Shell helper protocol {protocol} is not supported.",
                capabilities);
        }

        var parsedStatus = status switch
        {
            "success" => ShellIntegrationStatus.Success,
            "unsupported" => ShellIntegrationStatus.Unsupported,
            "failed" => ShellIntegrationStatus.Failed,
            "invalid" => ShellIntegrationStatus.InvalidRequest,
            "unauthorized" => ShellIntegrationStatus.Unauthorized,
            "version-mismatch" => ShellIntegrationStatus.VersionMismatch,
            _ => ShellIntegrationStatus.Failed,
        };
        return new(parsedStatus, diagnostic, capabilities);
    }

    private ShellIntegrationResult Invoke(string operation) => Invoke(NewRequest(operation));

    private ShellIntegrationResult Invoke(StringBuilder request)
    {
        if (!OperatingSystem.IsWindows())
        {
            return ShellIntegrationResult.Unsupported("Windows shell integrations are only available on Windows.");
        }
        if (!File.Exists(_helperPath))
        {
            return ShellIntegrationResult.Unsupported(
                $"The architecture-matched shell helper is missing at '{_helperPath}'.");
        }

        var token = RandomNumberGenerator.GetHexString(32);
        request.Insert(request.ToString().IndexOf("operation=", StringComparison.Ordinal), $"auth={token}\n");
        request.AppendLine("end=1");
        var process = _runner.Run(new(
            _helperPath,
            token,
            request.ToString(),
            TimeSpan.FromSeconds(5)));
        if (!process.Started)
        {
            return new(
                ShellIntegrationStatus.Failed,
                process.Diagnostic ?? "The shell helper process could not be started.");
        }
        if (string.IsNullOrWhiteSpace(process.StandardOutput))
        {
            return new(
                ShellIntegrationStatus.Failed,
                $"The shell helper returned no response (exit {process.ExitCode}): {process.StandardError}");
        }

        var parsed = ParseResponse(process.StandardOutput);
        if (process.ExitCode != 0 && parsed.Succeeded)
        {
            return new(
                ShellIntegrationStatus.Failed,
                $"The shell helper returned success with nonzero exit code {process.ExitCode}.");
        }
        return parsed;
    }

    private bool TryGetApplicationUserModelId(
        bool requireShortcut,
        out string appId,
        out ShellIntegrationResult unavailable)
    {
        var identity = _identity();
        if (identity.IsPackaged)
        {
            var familyName = _packageFamilyName();
            if (!string.IsNullOrWhiteSpace(familyName))
            {
                appId = $"{familyName}!{PackagedApplicationId}";
                unavailable = null!;
                return true;
            }
            appId = string.Empty;
            unavailable = ShellIntegrationResult.Unsupported(
                "The package family name could not be resolved for shell integration.");
            return false;
        }

        appId = FirstNonEmpty(_environment("DTERM_AUMID"), _environment("WT_DOTNET_AUMID")) ?? string.Empty;
        var shortcut = FirstNonEmpty(_environment("DTERM_TOAST_SHORTCUT"), _environment("WT_DOTNET_TOAST_SHORTCUT"));
        if (string.IsNullOrWhiteSpace(appId) ||
            (requireShortcut && (string.IsNullOrWhiteSpace(shortcut) || !File.Exists(shortcut))))
        {
            unavailable = ShellIntegrationResult.Unsupported(
                requireShortcut
                    ? "Unpackaged system toasts require DTERM_AUMID and DTERM_TOAST_SHORTCUT pointing to a registered Start-menu shortcut."
                    : "Unpackaged jump lists require DTERM_AUMID for an application with a registered Start-menu shortcut.");
            return false;
        }

        unavailable = null!;
        return true;
    }

    private static StringBuilder NewRequest(string operation) =>
        new StringBuilder()
            .Append("protocol=").Append(ProtocolVersion).AppendLine()
            .Append("operation=").Append(operation).AppendLine();

    private static void AddEncoded(StringBuilder request, string key, string value) =>
        request.Append(key).Append('=').Append(Encode(value)).AppendLine();

    private static string Encode(string value) =>
        Base64Url.Encode(Encoding.UTF8.GetBytes(value));

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }
}

public sealed class ShellHelperProcessRunner : IShellHelperProcessRunner
{
    public ShellHelperProcessResult Run(ShellHelperInvocation invocation) =>
        RunAsync(invocation).GetAwaiter().GetResult();

    private static async Task<ShellHelperProcessResult> RunAsync(ShellHelperInvocation invocation)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = invocation.ExecutablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        startInfo.Environment["WT_SHELL_HELPER_AUTH_TOKEN"] = invocation.AuthenticationToken;

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return new(false, -1, string.Empty, string.Empty, "Process.Start returned null.");
            }

            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(invocation.Timeout);
            try
            {
                await process.StandardInput.WriteAsync(
                    invocation.Request.AsMemory(),
                    timeout.Token).ConfigureAwait(false);
                process.StandardInput.Close();
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                return new(false, -1, string.Empty, string.Empty, "The shell helper timed out.");
            }

            return new(
                true,
                process.ExitCode,
                await output.ConfigureAwait(false),
                await error.ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is
            InvalidOperationException or
            System.ComponentModel.Win32Exception or
            IOException or
            UnauthorizedAccessException)
        {
            return new(false, -1, string.Empty, string.Empty, ex.Message);
        }
    }
}
