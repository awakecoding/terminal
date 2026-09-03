using System.Runtime.InteropServices;
using System.Text.Json;

namespace Devolutions.Terminal.Ghostty;

public static class GhosttyAbi
{
    public const string Commit = "3c1ef5b32fc5ea6b93d28493fabf193f595139cf";
    public const int SchemaVersion = 1;

    private static readonly Lazy<string> ValidatedManifest = new(ValidateCore);

    public static string TypeManifest => ValidatedManifest.Value;

    public static string NativeLibraryFileName => OperatingSystem.IsWindows()
        ? "ghostty-vt.dll"
        : OperatingSystem.IsMacOS()
            ? "libghostty-vt.dylib"
            : "libghostty-vt.so";

    public static bool IsNativeLibraryPresent =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, NativeLibraryFileName));

    public static void Validate() => _ = ValidatedManifest.Value;

    private static string ValidateCore()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException(
                $"The Ghostty engine does not support {RuntimeInformation.OSDescription} {RuntimeInformation.ProcessArchitecture}.");
        }

        var libraryPath = Path.Combine(AppContext.BaseDirectory, NativeLibraryFileName);
        if (!File.Exists(libraryPath))
        {
            throw new FileNotFoundException(
                $"libghostty-vt was not restored for this RID. Build it with native/Restore-NativeLibraries.ps1 (commit {Commit}).",
                libraryPath);
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
        var expectedOs = OperatingSystem.IsWindows()
            ? "windows"
            : OperatingSystem.IsMacOS()
                ? "macos"
                : "linux";
        var expectedEnvironment = OperatingSystem.IsWindows()
            ? "msvc"
            : OperatingSystem.IsMacOS()
                ? "none"
                : "gnu";
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
