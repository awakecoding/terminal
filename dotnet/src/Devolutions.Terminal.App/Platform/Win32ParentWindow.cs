using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Controls;

namespace Devolutions.Terminal.App.Platform;

public static partial class Win32ParentWindow
{
    public const string EnvironmentVariable = "WT_PARENT_WINDOW_HANDLE";

    public static bool IsRequested =>
        OperatingSystem.IsWindows() &&
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvironmentVariable));

    public static bool TryParseHandle(string? value, out nint handle)
    {
        handle = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();
        try
        {
            ulong parsed;
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                parsed = Convert.ToUInt64(text[2..], 16);
            }
            else
            {
                parsed = ulong.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
            }

            handle = unchecked((nint)parsed);
            return handle != 0;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    public static bool TryGetParentHandle(out nint handle)
    {
        handle = 0;
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        if (!TryParseHandle(Environment.GetEnvironmentVariable(EnvironmentVariable), out handle))
        {
            return false;
        }

        return ParentWindowExists(handle);
    }

    [SupportedOSPlatform("windows")]
    private static bool ParentWindowExists(nint handle) => IsWindow(handle);

    public static void Attach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!OperatingSystem.IsWindows() || !TryGetParentHandle(out var parent))
        {
            return;
        }

        ApplyEmbeddedChrome(window);
        window.Opened += (_, _) =>
        {
            if (OperatingSystem.IsWindows())
            {
                ApplyParent(window, parent);
            }
        };
    }

    public static void ApplyEmbeddedChrome(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.ShowInTaskbar = false;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.ExtendClientAreaToDecorationsHint = false;
        window.CanResize = true;
    }

    [SupportedOSPlatform("windows")]
    private static void ApplyParent(Window window, nint parent)
    {
        var hwnd = window.TryGetPlatformHandle()?.Handle ?? 0;
        if (hwnd == 0 || !IsWindow(parent))
        {
            return;
        }

        SetParent(hwnd, parent);
        var style = GetWindowLongPtr(hwnd, GwlStyle);
        style = (nint)(((nuint)style & ~(WsPopup | WsCaption | WsThickFrame | WsSysMenu | WsMinimizeBox | WsMaximizeBox))
            | WsChild | WsVisible | WsBorder | WsClipSiblings | WsClipChildren);
        SetWindowLongPtr(hwnd, GwlStyle, style);
        var exStyle = GetWindowLongPtr(hwnd, GwlExStyle);
        exStyle = (nint)((nuint)exStyle & ~(WsExAppWindow | WsExTopmost));
        SetWindowLongPtr(hwnd, GwlExStyle, exStyle);
        if (GetClientRect(parent, out var rect))
        {
            SetWindowPos(
                hwnd,
                0,
                0,
                0,
                rect.Right - rect.Left,
                rect.Bottom - rect.Top,
                SwpNoZOrder | SwpFrameChanged);
        }
    }

    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;
    private const nuint WsChild = 0x40000000;
    private const nuint WsVisible = 0x10000000;
    private const nuint WsBorder = 0x00800000;
    private const nuint WsClipSiblings = 0x04000000;
    private const nuint WsClipChildren = 0x02000000;
    private const nuint WsPopup = 0x80000000;
    private const nuint WsCaption = 0x00C00000;
    private const nuint WsThickFrame = 0x00040000;
    private const nuint WsSysMenu = 0x00080000;
    private const nuint WsMinimizeBox = 0x00020000;
    private const nuint WsMaximizeBox = 0x00010000;
    private const nuint WsExAppWindow = 0x00040000;
    private const nuint WsExTopmost = 0x00000008;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpFrameChanged = 0x0020;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindow(nint hWnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint SetParent(nint hWndChild, nint hWndNewParent);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static partial nint GetWindowLongPtr(nint hWnd, int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static partial nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetClientRect(nint hWnd, out Rect rect);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(
        nint hWnd,
        nint hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
