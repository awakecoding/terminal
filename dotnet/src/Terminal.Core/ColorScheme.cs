namespace Microsoft.Terminal.Core;

public sealed class ColorScheme
{
    public string Name { get; init; } = "Campbell";
    public uint Foreground { get; init; }
    public uint Background { get; init; }
    public uint Cursor { get; init; }
    public uint SelectionBackground { get; init; }
    public uint[] Table { get; init; } = new uint[16];

    private readonly uint[] _xterm256 = new uint[256];

    public ColorScheme()
    {
        BuildXtermCube();
    }

    public uint Resolve(int index)
    {
        if ((uint)index < 16)
        {
            return Table[index];
        }

        if ((uint)index < 256)
        {
            return _xterm256[index];
        }

        return Foreground;
    }

    public static ColorScheme Campbell { get; } = new()
    {
        Name = "Campbell",
        Foreground = 0xFFCCCCCC,
        Background = 0xFF0C0C0C,
        Cursor = 0xFFFFFFFF,
        SelectionBackground = 0x80FFFFFF,
        Table =
        [
            0xFF0C0C0C, // black
            0xFFC50F1F, // red
            0xFF13A10E, // green
            0xFFC19C00, // yellow
            0xFF0037DA, // blue
            0xFF881798, // purple
            0xFF3A96DD, // cyan
            0xFFCCCCCC, // white
            0xFF767676, // bright black
            0xFFE74856, // bright red
            0xFF16C60C, // bright green
            0xFFF9F1A5, // bright yellow
            0xFF3B78FF, // bright blue
            0xFFB4009E, // bright purple
            0xFF61D6D6, // bright cyan
            0xFFF2F2F2, // bright white
        ],
    };

    public static ColorScheme OneHalfDark { get; } = new()
    {
        Name = "One Half Dark",
        Foreground = 0xFFDCDFE4,
        Background = 0xFF282C34,
        Cursor = 0xFFFFFFFF,
        SelectionBackground = 0x80FFFFFF,
        Table =
        [
            0xFF282C34,
            0xFFE06C75,
            0xFF98C379,
            0xFFE5C07B,
            0xFF61AFEF,
            0xFFC678DD,
            0xFF56B6C2,
            0xFFDCDFE4,
            0xFF5A6374,
            0xFFE06C75,
            0xFF98C379,
            0xFFE5C07B,
            0xFF61AFEF,
            0xFFC678DD,
            0xFF56B6C2,
            0xFFDCDFE4,
        ],
    };

    public static ColorScheme SolarizedDark { get; } = new()
    {
        Name = "Solarized Dark",
        Foreground = 0xFF839496,
        Background = 0xFF002B36,
        Cursor = 0xFFFFFFFF,
        SelectionBackground = 0x802C4D57,
        Table =
        [
            0xFF002B36,
            0xFFDC322F,
            0xFF859900,
            0xFFB58900,
            0xFF268BD2,
            0xFFD33682,
            0xFF2AA198,
            0xFFEEE8D5,
            0xFF073642,
            0xFFCB4B16,
            0xFF586E75,
            0xFF657B83,
            0xFF839496,
            0xFF6C71C4,
            0xFF93A1A1,
            0xFFFDF6E3,
        ],
    };

    public static ColorScheme FromName(string? name) => name switch
    {
        "One Half Dark" => OneHalfDark,
        "Solarized Dark" => SolarizedDark,
        _ => Campbell,
    };

    private void BuildXtermCube()
    {
        for (var i = 0; i < 16 && i < Table.Length; i++)
        {
            _xterm256[i] = Table[i];
        }

        var cube = new byte[] { 0, 95, 135, 175, 215, 255 };
        for (var r = 0; r < 6; r++)
        {
            for (var g = 0; g < 6; g++)
            {
                for (var b = 0; b < 6; b++)
                {
                    var idx = 16 + (r * 36) + (g * 6) + b;
                    _xterm256[idx] = 0xFF000000u | ((uint)cube[r] << 16) | ((uint)cube[g] << 8) | cube[b];
                }
            }
        }

        for (var i = 0; i < 24; i++)
        {
            var level = (byte)(8 + (i * 10));
            _xterm256[232 + i] = 0xFF000000u | ((uint)level << 16) | ((uint)level << 8) | level;
        }
    }
}
