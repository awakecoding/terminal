using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Microsoft.Terminal.Ghostty;

public static class GhosttyAbi
{
    public const string Commit = "3c1ef5b32fc5ea6b93d28493fabf193f595139cf";
    public const int SchemaVersion = 1;

    private static readonly Lazy<string> ValidatedManifest = new(ValidateCore);

    public static string TypeManifest => ValidatedManifest.Value;

    public static void Validate() => _ = ValidatedManifest.Value;

    private static string ValidateCore()
    {
        var expectedHash = (OperatingSystem.IsWindows(), RuntimeInformation.ProcessArchitecture) switch
        {
            (true, Architecture.X64) => "DCB3274F9D8C945AC765A11903614C5DA4BC0CC2EF4EBC23E8CD70C130B7B458",
            (true, Architecture.Arm64) => "691A331E92D0CE17B8407DD370D26394090B14AB8A7C398DF497442293D4ED72",
            (false, Architecture.X64) when OperatingSystem.IsLinux() =>
                "46AC64A83F91542D38D60BC0DC169157E9475958566349D7D0B4EEE621C5F929",
            (false, Architecture.Arm64) when OperatingSystem.IsLinux() =>
                "0D2CB6B391592CF772A166D9393DA859884C46614B219B64E3792B4BC989DADC",
            _ => throw new PlatformNotSupportedException(
                $"The Ghostty engine does not support {RuntimeInformation.ProcessArchitecture}."),
        };
        var libraryName = OperatingSystem.IsWindows()
            ? "ghostty-vt.dll"
            : "libghostty-vt.so";
        var libraryPath = Path.Combine(AppContext.BaseDirectory, libraryName);
        using (var stream = File.OpenRead(libraryPath))
        {
            var actualHash = Convert.ToHexString(SHA256.HashData(stream));
            if (!actualHash.Equals(expectedHash, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"ghostty-vt.dll does not match pinned commit {Commit}. Expected {expectedHash}, got {actualHash}.");
            }
        }

        var pointer = GhosttyNative.TypeJson();
        var manifest = Marshal.PtrToStringUTF8(pointer)
            ?? throw new InvalidOperationException("libghostty-vt returned no ABI type manifest.");
        using var document = JsonDocument.Parse(manifest);
        var root = document.RootElement;
        if (root.GetProperty("schema").GetInt32() != SchemaVersion)
        {
            throw new InvalidOperationException(
                $"Unsupported libghostty-vt ABI schema. Expected schema {SchemaVersion} from {Commit}.");
        }

        var abi = root.GetProperty("abi");
        var expectedTarget = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? "aarch64"
            : "x86_64";
        var expectedOs = OperatingSystem.IsWindows() ? "windows" : "linux";
        var expectedEnvironment = OperatingSystem.IsWindows() ? "msvc" : "gnu";
        if (abi.GetProperty("target").GetString() != expectedTarget ||
            abi.GetProperty("os").GetString() != expectedOs ||
            abi.GetProperty("environment").GetString() != expectedEnvironment ||
            abi.GetProperty("pointer_size").GetInt32() != IntPtr.Size)
        {
            throw new InvalidOperationException("ghostty-vt.dll ABI target does not match this process.");
        }

        var types = root.GetProperty("types");
        ValidateSize(types, "GhosttyStyle", 72);
        ValidateSize(types, "GhosttyRenderStateCursor", 24);
        ValidateSize(types, "GhosttyClipboardWrite", 72);
        ValidateSize(types, "GhosttyPoint", 24);
        ValidateSize(types, "GhosttyGridRef", 24);
        ValidateEnum(types, "GhosttyTerminalOption", "SCROLLBACK_MAX_LINES", 28);
        ValidateEnum(types, "GhosttyTerminalData", "MODE", 37);
        ValidateEnum(types, "GhosttyRenderStateData", "CURSOR", 18);
        ValidateEnum(types, "GhosttyRenderStateRowCellsData", "GRAPHEMES_UTF8", 9);
        return manifest;
    }

    private static void ValidateSize(JsonElement types, string name, int expected)
    {
        if (types.GetProperty(name).GetProperty("size").GetInt32() != expected)
        {
            throw new InvalidOperationException($"Unexpected {name} ABI size.");
        }
    }

    private static void ValidateEnum(
        JsonElement types,
        string type,
        string name,
        int expected)
    {
        if (types.GetProperty(type).GetProperty("values").GetProperty(name).GetInt32() != expected)
        {
            throw new InvalidOperationException($"Unexpected {type}.{name} ABI value.");
        }
    }
}
