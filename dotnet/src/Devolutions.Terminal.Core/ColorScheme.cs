namespace Devolutions.Terminal.Core;

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
        if ((uint)index < (uint)Table.Length)
        {
            return Table[index];
        }

        if ((uint)index < 256)
        {
            return _xterm256[index];
        }

        return Foreground;
    }

    public ColorScheme WithColorTableEntry(int index, uint color)
    {
        var table = new uint[256];
        for (var i = 0; i < table.Length; i++)
        {
            table[i] = Resolve(i);
        }

        if ((uint)index < (uint)table.Length)
        {
            table[index] = ForceOpaque(color);
        }

        return Copy(table: table);
    }

    public ColorScheme WithForeground(uint color) => Copy(foreground: ForceOpaque(color));

    public ColorScheme WithBackground(uint color) => Copy(background: ForceOpaque(color));

    public ColorScheme WithCursor(uint color) => Copy(cursor: ForceOpaque(color));

    public static bool TryParseXtermColor(ReadOnlySpan<char> value, out uint color)
    {
        color = 0;
        if (value.StartsWith('#') && value.Length is 4 or 7)
        {
            if (value.Length == 4 &&
                TryHex(value[1..2], out var r4) &&
                TryHex(value[2..3], out var g4) &&
                TryHex(value[3..4], out var b4))
            {
                color = 0xFF000000u | (r4 * 17u << 16) | (g4 * 17u << 8) | (b4 * 17u);
                return true;
            }

            if (value.Length == 7 &&
                TryHex(value[1..3], out var r8) &&
                TryHex(value[3..5], out var g8) &&
                TryHex(value[5..7], out var b8))
            {
                color = 0xFF000000u | (r8 << 16) | (g8 << 8) | b8;
                return true;
            }
        }

        if (value.StartsWith("rgb:", StringComparison.OrdinalIgnoreCase))
        {
            var components = value[4..].ToString().Split('/');
            if (components.Length == 3 &&
                TryScaleHex(components[0], out var r) &&
                TryScaleHex(components[1], out var g) &&
                TryScaleHex(components[2], out var b))
            {
                color = 0xFF000000u | (r << 16) | (g << 8) | b;
                return true;
            }
        }

        return false;
    }

    public static string FormatXtermColor(uint color)
    {
        var r = (byte)(color >> 16);
        var g = (byte)(color >> 8);
        var b = (byte)color;
        return $"rgb:{r:x2}{r:x2}/{g:x2}{g:x2}/{b:x2}{b:x2}";
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

    private ColorScheme Copy(
        uint? foreground = null,
        uint? background = null,
        uint? cursor = null,
        uint[]? table = null) => new()
    {
        Name = Name,
        Foreground = foreground ?? Foreground,
        Background = background ?? Background,
        Cursor = cursor ?? Cursor,
        SelectionBackground = SelectionBackground,
        Table = table ?? (uint[])Table.Clone(),
    };

    private static uint ForceOpaque(uint color) => color | 0xFF000000u;

    private static bool TryHex(ReadOnlySpan<char> value, out uint parsed) =>
        uint.TryParse(value, System.Globalization.NumberStyles.HexNumber, null, out parsed);

    private static bool TryScaleHex(string value, out uint component)
    {
        component = 0;
        if (value.Length is < 1 or > 4 || !TryHex(value, out var parsed))
        {
            return false;
        }

        var maximum = (1u << (value.Length * 4)) - 1u;
        component = (parsed * 255u + (maximum / 2u)) / maximum;
        return true;
    }
}
