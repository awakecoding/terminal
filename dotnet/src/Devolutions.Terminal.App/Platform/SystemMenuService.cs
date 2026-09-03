using System.Runtime.InteropServices;

namespace Devolutions.Terminal.App.Platform;

public interface ISystemMenuService
{
    SystemMenuResult Open(nint windowHandle, int fallbackX, int fallbackY);
}

public sealed record SystemMenuResult(bool Succeeded, bool Supported, string Diagnostic)
{
    public static SystemMenuResult Success() => new(true, true, string.Empty);
    public static SystemMenuResult Unsupported(string diagnostic) => new(false, false, diagnostic);
    public static SystemMenuResult Failure(string diagnostic) => new(false, true, diagnostic);
}

public static class SystemMenuService
{
    public static ISystemMenuService CreateDefault() =>
        OperatingSystem.IsWindows()
            ? new WindowsSystemMenuService()
            : new UnsupportedSystemMenuService();
}

public sealed class UnsupportedSystemMenuService : ISystemMenuService
{
    public SystemMenuResult Open(nint windowHandle, int fallbackX, int fallbackY) =>
        SystemMenuResult.Unsupported(
            "The native system menu is unavailable; the Avalonia window menu fallback will be used.");
}

internal sealed partial class WindowsSystemMenuService : ISystemMenuService
{
    private const uint TpmRightButton = 0x0002;
    private const uint TpmReturnCommand = 0x0100;
    private const uint WmSysCommand = 0x0112;

    public SystemMenuResult Open(nint windowHandle, int fallbackX, int fallbackY)
    {
        if (windowHandle == 0)
        {
            return SystemMenuResult.Failure("The native window handle is not available.");
        }

        var menu = GetSystemMenu(windowHandle, false);
        if (menu == 0)
        {
            return SystemMenuResult.Failure("Windows did not provide a system menu.");
        }

        var point = GetCursorPos(out var cursor)
            ? cursor
            : new Point { X = fallbackX, Y = fallbackY };
        var command = TrackPopupMenu(
            menu,
            TpmRightButton | TpmReturnCommand,
            point.X,
            point.Y,
            0,
            windowHandle,
            0);
        if (command != 0)
        {
            _ = PostMessage(windowHandle, WmSysCommand, (nuint)command, 0);
        }
        return SystemMenuResult.Success();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [LibraryImport("user32.dll", EntryPoint = "GetSystemMenu")]
    private static partial nint GetSystemMenu(nint windowHandle, [MarshalAs(UnmanagedType.Bool)] bool revert);

    [LibraryImport("user32.dll", EntryPoint = "GetCursorPos")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out Point point);

    [LibraryImport("user32.dll", EntryPoint = "TrackPopupMenu")]
    private static partial uint TrackPopupMenu(
        nint menu,
        uint flags,
        int x,
        int y,
        int reserved,
        nint windowHandle,
        nint rectangle);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessage(nint windowHandle, uint message, nuint wParam, nint lParam);
}
