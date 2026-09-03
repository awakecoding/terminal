using System.Runtime.InteropServices;

namespace Devolutions.Terminal.Ghostty;

internal enum GhosttyResult
{
    Success = 0,
    OutOfMemory = -1,
    InvalidValue = -2,
    OutOfSpace = -3,
    NoValue = -4,
    IoError = -5,
    LimitExceeded = -6,
    Rejected = -7,
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct GhosttyColorRgb
{
    public byte R;
    public byte G;
    public byte B;
}

[StructLayout(LayoutKind.Explicit, Size = 16)]
internal struct GhosttyStyleColor
{
    [FieldOffset(0)]
    public int Tag;

    [FieldOffset(8)]
    public byte Palette;

    [FieldOffset(8)]
    public GhosttyColorRgb Rgb;
}

[StructLayout(LayoutKind.Explicit, Size = 72)]
internal struct GhosttyStyle
{
    [FieldOffset(0)]
    public nuint Size;

    [FieldOffset(8)]
    public GhosttyStyleColor Foreground;

    [FieldOffset(24)]
    public GhosttyStyleColor Background;

    [FieldOffset(40)]
    public GhosttyStyleColor UnderlineColor;

    [FieldOffset(56)]
    public byte Bold;

    [FieldOffset(57)]
    public byte Italic;

    [FieldOffset(58)]
    public byte Faint;

    [FieldOffset(59)]
    public byte Blink;

    [FieldOffset(60)]
    public byte Inverse;

    [FieldOffset(61)]
    public byte Invisible;

    [FieldOffset(62)]
    public byte Strikethrough;

    [FieldOffset(63)]
    public byte Overline;

    [FieldOffset(64)]
    public int Underline;
}

[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct GhosttyRenderCursor
{
    [FieldOffset(0)]
    public nuint Size;

    [FieldOffset(8)]
    public byte ViewportHasValue;

    [FieldOffset(10)]
    public ushort ViewportX;

    [FieldOffset(12)]
    public ushort ViewportY;

    [FieldOffset(14)]
    public byte WideTail;

    [FieldOffset(15)]
    public byte Visible;

    [FieldOffset(16)]
    public byte Blinking;

    [FieldOffset(17)]
    public byte PasswordInput;

    [FieldOffset(20)]
    public int VisualStyle;
}

[StructLayout(LayoutKind.Sequential)]
internal struct GhosttyString
{
    public nint Pointer;
    public nuint Length;
}

[StructLayout(LayoutKind.Explicit, Size = 4)]
internal struct GhosttyModeConfig
{
    [FieldOffset(0)]
    public ushort Mode;

    [FieldOffset(2)]
    public byte Value;
}

[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct GhosttyScrollViewport
{
    [FieldOffset(0)]
    public int Tag;

    [FieldOffset(8)]
    public nuint Row;
}

[StructLayout(LayoutKind.Sequential)]
internal struct GhosttyScrollbar
{
    public ulong Total;
    public ulong Offset;
    public ulong Length;
}

[StructLayout(LayoutKind.Sequential)]
internal struct GhosttyClipboardContent
{
    public GhosttyString Mime;
    public GhosttyString Data;
}

[StructLayout(LayoutKind.Explicit, Size = 72)]
internal unsafe struct GhosttyClipboardWrite
{
    [FieldOffset(0)]
    public nuint Size;

    [FieldOffset(16)]
    public GhosttyClipboardContent* Contents;

    [FieldOffset(24)]
    public nuint ContentsLength;

    [FieldOffset(64)]
    public nint Reply;
}

[StructLayout(LayoutKind.Explicit, Size = 16)]
internal struct GhosttyClipboardWriteReply
{
    [FieldOffset(0)]
    public nuint Size;

    [FieldOffset(8)]
    public int Result;

    [FieldOffset(12)]
    public byte Remember;
}

[StructLayout(LayoutKind.Explicit, Size = 40)]
internal struct GhosttyDesktopNotification
{
    [FieldOffset(0)]
    public nuint Size;

    [FieldOffset(8)]
    public GhosttyString Title;

    [FieldOffset(24)]
    public GhosttyString Body;
}

[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct GhosttyPoint
{
    [FieldOffset(0)]
    public int Tag;

    [FieldOffset(8)]
    public ushort X;

    [FieldOffset(12)]
    public uint Y;
}

[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct GhosttyGridRef
{
    [FieldOffset(0)]
    public nuint Size;

    [FieldOffset(8)]
    public nint Node;

    [FieldOffset(16)]
    public ushort X;

    [FieldOffset(18)]
    public ushort Y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct GhosttySizeReport
{
    public ushort Rows;
    public ushort Columns;
    public uint CellWidth;
    public uint CellHeight;
}

internal sealed class GhosttyException(string operation, GhosttyResult result)
    : InvalidOperationException($"{operation} failed with Ghostty result {result} ({(int)result}).")
{
    public GhosttyResult Result { get; } = result;
}
