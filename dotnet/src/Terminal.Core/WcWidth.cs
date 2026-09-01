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
        || IsEmojiPresentationWide(v)
        || v is >= 0x20000 and <= 0x3FFFD;

    private static bool IsEmojiPresentationWide(int v) =>
        v is 0x231A or 0x231B or 0x23F0 or 0x23F3 or
            0x25FD or 0x25FE or 0x2614 or 0x2615 or 0x267F or 0x2693 or
            0x26A1 or 0x26AA or 0x26AB or 0x26BD or 0x26BE or 0x26C4 or
            0x26C5 or 0x26CE or 0x26D4 or 0x26EA or 0x26F2 or 0x26F3 or
            0x26F5 or 0x26FA or 0x26FD or 0x2705 or 0x270A or 0x270B or
            0x2728 or 0x274C or 0x274E or 0x2753 or 0x2754 or 0x2755 or
            0x2757 or 0x2795 or 0x2796 or 0x2797 or 0x27B0 or 0x27BF or
            >= 0x23E9 and <= 0x23EC or
            >= 0x2648 and <= 0x2653 or
            >= 0x1F1E6 and <= 0x1F1FF or
            >= 0x1F300 and <= 0x1FAFF;
}
