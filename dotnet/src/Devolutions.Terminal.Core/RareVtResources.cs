using System.Collections.ObjectModel;
using System.Text;

namespace Devolutions.Terminal.Core;

public static class VtResourceLimits
{
    public const int MaximumDrcsCharacters = 96;
    public const int MaximumDrcsGlyphWidth = 16;
    public const int MaximumDrcsGlyphHeight = 32;
    public const int MaximumMacros = 64;
    public const int MaximumMacroBytes = 256 * 1024;
    public const int MaximumMacroRecursionDepth = 16;
}

public sealed record DrcsGlyph(
    int SourceCharacter,
    Rune PrivateUseRune,
    int Width,
    int Height,
    ReadOnlyMemory<byte> AlphaMask);

internal static class DrcsDecoder
{
    public static bool TryDecode(
        ReadOnlySpan<int> parameters,
        ReadOnlySpan<byte> data,
        out string designator,
        out int eraseControl,
        out bool is96Character,
        out IReadOnlyDictionary<int, DrcsGlyph> glyphs)
    {
        designator = string.Empty;
        eraseControl = Parameter(parameters, 2, 0);
        is96Character = false;
        glyphs = ReadOnlyDictionary<int, DrcsGlyph>.Empty;
        var fontNumber = Parameter(parameters, 0, 0);
        var charsetSize = Parameter(parameters, 7, 0);
        if (fontNumber is not (0 or 1) ||
            eraseControl is < 0 or > 2 ||
            charsetSize is < 0 or > 1 ||
            data.IsEmpty)
        {
            return false;
        }

        var offset = 0;
        if (data[offset] is >= 0x20 and <= 0x2F)
        {
            if (data.Length < 2 || data[1] is < 0x30 or > 0x7E)
            {
                return false;
            }

            designator = string.Create(2, data[..2].ToArray(), static (chars, bytes) =>
            {
                chars[0] = (char)bytes[0];
                chars[1] = (char)bytes[1];
            });
            offset = 2;
        }
        else if (data[offset] is >= 0x30 and <= 0x7E)
        {
            designator = ((char)data[offset]).ToString();
            offset = 1;
        }
        else
        {
            return false;
        }

        var requestedWidth = Parameter(parameters, 3, 0);
        var width = requestedWidth switch
        {
            0 => VtResourceLimits.MaximumDrcsGlyphWidth,
            2 => 5,
            3 => 6,
            4 => 7,
            > 1 and <= VtResourceLimits.MaximumDrcsGlyphWidth => requestedWidth,
            _ => 0,
        };
        var requestedHeight = Parameter(parameters, 6, 0);
        var height = requestedHeight == 0 ? VtResourceLimits.MaximumDrcsGlyphHeight : requestedHeight;
        if (width == 0 || height is < 1 or > VtResourceLimits.MaximumDrcsGlyphHeight)
        {
            return false;
        }

        var startCharacter = Parameter(parameters, 1, charsetSize == 0 ? 1 : 0);
        var maximumCharacter = charsetSize == 0 ? 94 : 95;
        if (startCharacter < (charsetSize == 0 ? 1 : 0) || startCharacter > maximumCharacter)
        {
            return false;
        }

        is96Character = charsetSize == 1;
        var decoded = new Dictionary<int, DrcsGlyph>();
        var glyphIndex = startCharacter;
        var mask = new byte[width * height];
        var x = 0;
        var sixelRow = 0;
        var usedWidth = 0;
        var usedHeight = 0;

        void FinishGlyph()
        {
            if (glyphIndex > maximumCharacter || decoded.Count >= VtResourceLimits.MaximumDrcsCharacters)
            {
                return;
            }

            var finalWidth = width;
            var finalHeight = Math.Max(1, Math.Min(height, usedHeight));
            var cropped = new byte[finalWidth * finalHeight];
            for (var y = 0; y < finalHeight; y++)
            {
                mask.AsSpan(y * width, finalWidth).CopyTo(cropped.AsSpan(y * finalWidth));
            }

            var privateUse = new Rune(0xEF20 + glyphIndex);
            decoded[glyphIndex] = new DrcsGlyph(
                0x20 + glyphIndex,
                privateUse,
                finalWidth,
                finalHeight,
                cropped);
            glyphIndex++;
            Array.Clear(mask);
            x = 0;
            sixelRow = 0;
            usedWidth = 0;
            usedHeight = 0;
        }

        for (; offset < data.Length && glyphIndex <= maximumCharacter; offset++)
        {
            var value = data[offset];
            if (value == (byte)';')
            {
                FinishGlyph();
            }
            else if (value == (byte)'/')
            {
                sixelRow += 6;
                x = 0;
            }
            else if (value is >= (byte)'?' and <= (byte)'~')
            {
                if (x < width && sixelRow < height)
                {
                    var bits = value - (byte)'?';
                    for (var bit = 0; bit < 6 && sixelRow + bit < height; bit++)
                    {
                        if ((bits & (1 << bit)) != 0)
                        {
                            mask[((sixelRow + bit) * width) + x] = 255;
                            usedHeight = Math.Max(usedHeight, sixelRow + bit + 1);
                        }
                    }

                    usedWidth = Math.Max(usedWidth, x + 1);
                }

                x++;
            }
            else
            {
                return false;
            }
        }

        FinishGlyph();
        glyphs = new ReadOnlyDictionary<int, DrcsGlyph>(decoded);
        return decoded.Count > 0;
    }

    private static int Parameter(ReadOnlySpan<int> parameters, int index, int defaultValue) =>
        (uint)index >= (uint)parameters.Length || parameters[index] < 0 ? defaultValue : parameters[index];
}

internal static class MacroDecoder
{
    public static bool TryDecode(ReadOnlySpan<byte> data, int encoding, out byte[] decoded)
    {
        decoded = [];
        if (encoding == 0)
        {
            decoded = data
                .ToArray()
                .Where(static value => value >= 0x20 && value != 0x7F)
                .ToArray();
            return decoded.Length <= VtResourceLimits.MaximumMacroBytes;
        }

        if (encoding != 1)
        {
            return false;
        }

        var output = new List<byte>(Math.Min(data.Length / 2, VtResourceLimits.MaximumMacroBytes));
        var offset = 0;
        while (offset < data.Length)
        {
            if (data[offset] == (byte)'!')
            {
                offset++;
                if (!TryReadDecimal(data, ref offset, out var count) ||
                    offset >= data.Length ||
                    data[offset++] != (byte)';')
                {
                    return false;
                }

                var end = data[offset..].IndexOf((byte)';');
                var unterminated = end < 0;
                if (unterminated)
                {
                    end = data.Length - offset;
                }

                if (!TryDecodeHex(data.Slice(offset, end), out var repeated))
                {
                    return false;
                }

                offset += end + (unterminated ? 0 : 1);
                var repetitions = Math.Max(1, count);
                if (repetitions > VtResourceLimits.MaximumMacroBytes ||
                    repeated.Length > 0 &&
                    repetitions > (VtResourceLimits.MaximumMacroBytes - output.Count) / repeated.Length)
                {
                    return false;
                }

                for (var repeat = 0; repeat < repetitions; repeat++)
                {
                    output.AddRange(repeated);
                }
            }
            else
            {
                var nextRepeat = data[offset..].IndexOf((byte)'!');
                var length = nextRepeat < 0 ? data.Length - offset : nextRepeat;
                if (!TryDecodeHex(data.Slice(offset, length), out var bytes) ||
                    output.Count + bytes.Length > VtResourceLimits.MaximumMacroBytes)
                {
                    return false;
                }

                output.AddRange(bytes);
                offset += length;
            }
        }

        decoded = output.ToArray();
        return true;
    }

    private static bool TryReadDecimal(ReadOnlySpan<byte> data, ref int offset, out int value)
    {
        value = 0;
        var start = offset;
        while (offset < data.Length && data[offset] is >= (byte)'0' and <= (byte)'9')
        {
            value = Math.Min(VtResourceLimits.MaximumMacroBytes + 1, (value * 10) + data[offset] - (byte)'0');
            offset++;
        }

        return offset > start;
    }

    private static bool TryDecodeHex(ReadOnlySpan<byte> data, out byte[] decoded)
    {
        decoded = [];
        if ((data.Length & 1) != 0)
        {
            return false;
        }

        decoded = new byte[data.Length / 2];
        for (var index = 0; index < decoded.Length; index++)
        {
            var high = Hex(data[index * 2]);
            var low = Hex(data[(index * 2) + 1]);
            if (high < 0 || low < 0)
            {
                decoded = [];
                return false;
            }

            decoded[index] = (byte)((high << 4) | low);
        }

        return true;
    }

    private static int Hex(byte value) => value switch
    {
        >= (byte)'0' and <= (byte)'9' => value - (byte)'0',
        >= (byte)'A' and <= (byte)'F' => value - (byte)'A' + 10,
        >= (byte)'a' and <= (byte)'f' => value - (byte)'a' + 10,
        _ => -1,
    };
}
