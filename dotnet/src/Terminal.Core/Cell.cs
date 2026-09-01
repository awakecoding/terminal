using System.Text;

namespace Microsoft.Terminal.Core;

[Flags]
public enum CellFlags : ushort
{
    None = 0,
    Bold = 1,
    Faint = 2,
    Italic = 4,
    Underline = 8,
    Blink = 16,
    Inverse = 32,
    Invisible = 64,
    Strikethrough = 128,
}

public enum ColorKind : byte
{
    Default = 0,
    Indexed = 1,
    Rgb = 2,
}

public enum ShellIntegrationKind : byte
{
    None,
    Prompt,
    Command,
    Output,
}

public sealed class ShellMark
{
    public ShellMark(int startColumn, uint? exitCode = null)
    {
        StartColumn = startColumn;
        ExitCode = exitCode;
    }

    public int StartColumn { get; internal set; }
    public uint? ExitCode { get; internal set; }
}

public readonly record struct TermColor(ColorKind Kind, byte Index, byte R, byte G, byte B)
{
    public static TermColor Default { get; } = new(ColorKind.Default, 0, 0, 0, 0);

    public static TermColor FromIndex(int index) =>
        new(ColorKind.Indexed, (byte)Math.Clamp(index, 0, 255), 0, 0, 0);

    public static TermColor FromRgb(byte r, byte g, byte b) =>
        new(ColorKind.Rgb, 0, r, g, b);

    public uint ToArgb(ColorScheme scheme, bool foreground)
    {
        return Kind switch
        {
            ColorKind.Indexed => scheme.Resolve(Index),
            ColorKind.Rgb => 0xFF000000u | ((uint)R << 16) | ((uint)G << 8) | B,
            _ => foreground ? scheme.Foreground : scheme.Background,
        };
    }
}

public struct CellAttributes
{
    public TermColor Foreground;
    public TermColor Background;
    public CellFlags Flags;

    public static CellAttributes Default { get; } = new()
    {
        Foreground = TermColor.Default,
        Background = TermColor.Default,
        Flags = CellFlags.None,
    };

    public CellAttributes WithSgrReset() => Default;
}

public struct Cell
{
    public Rune Rune;
    public CellAttributes Attributes;
    public bool IsWideContinuation;
    public byte StoredWidth;
    public string? CombiningCharacters;
    public string? HyperlinkUri;
    public ShellIntegrationKind ShellIntegration;

    public static Cell Blank => new()
    {
        Rune = new Rune(' '),
        Attributes = CellAttributes.Default,
        StoredWidth = 1,
    };

    public bool IsBlank =>
        !IsWideContinuation &&
        Rune.Value == ' ' &&
        string.IsNullOrEmpty(CombiningCharacters) &&
        Attributes.Flags == CellFlags.None &&
        Attributes.Foreground.Kind == ColorKind.Default &&
        Attributes.Background.Kind == ColorKind.Default;

    public readonly string Text =>
        IsWideContinuation ? string.Empty : Rune + (CombiningCharacters ?? string.Empty);

    public readonly int DisplayWidth =>
        IsWideContinuation
            ? 0
            : StoredWidth == 0
                ? Math.Max(1, WcWidth.Width(Rune))
                : StoredWidth;
}
