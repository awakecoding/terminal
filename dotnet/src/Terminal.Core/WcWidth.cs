using System.Globalization;
using System.Text;

namespace Microsoft.Terminal.Core;

public static class WcWidth
{
    public static int Width(Rune rune)
    {
        var value = rune.Value;
        if (value == 0)
        {
            return 0;
        }

        if (value < 0x20 || (value >= 0x7F && value < 0xA0))
        {
            return 0;
        }

        if (value is >= 0x1F3FB and <= 0x1F3FF)
        {
            return 0;
        }

        var category = Rune.GetUnicodeCategory(rune);
        if (category is UnicodeCategory.NonSpacingMark
            or UnicodeCategory.EnclosingMark
            or UnicodeCategory.SpacingCombiningMark
            or UnicodeCategory.Control
            or UnicodeCategory.Format
            or UnicodeCategory.Surrogate)
        {
            return 0;
        }

        if (IsWide(value))
        {
            return 2;
        }

        return 1;
    }

    private static bool IsWide(int v) =>
        v is >= 0x1100 and <= 0x115F
        || v is 0x2329 or 0x232A
        || v is >= 0x2E80 and <= 0xA4CF && v is not 0x303F
        || v is >= 0xAC00 and <= 0xD7A3
        || v is >= 0xF900 and <= 0xFAFF
        || v is >= 0xFE10 and <= 0xFE19
        || v is >= 0xFE30 and <= 0xFE6F
        || v is >= 0xFF00 and <= 0xFF60
        || v is >= 0xFFE0 and <= 0xFFE6
        || v is >= 0x1F1E6 and <= 0x1F1FF
        || v is >= 0x1F300 and <= 0x1F64F
        || v is >= 0x1F900 and <= 0x1F9FF
        || v is >= 0x1FA70 and <= 0x1FAFF
        || v is >= 0x20000 and <= 0x3FFFD;
}
