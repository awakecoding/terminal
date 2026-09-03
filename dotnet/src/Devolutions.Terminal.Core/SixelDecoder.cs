namespace Devolutions.Terminal.Core;

public sealed class SixelDecoder
{
    private const int MaximumColors = 256;
    private const int MaximumAspectRatio = 20;

    private readonly uint[] _palette = CreateDefaultPalette();

    public void Reset() => CreateDefaultPalette().CopyTo(_palette, 0);

    public bool TryDecode(
        ReadOnlySpan<byte> data,
        int macroParameter,
        int backgroundSelect,
        int backgroundColor,
        out SixelImage? image)
    {
        image = null;
        if (data.Length > TerminalImageLimits.MaximumDcsPayloadBytes)
        {
            return false;
        }

        var workingPalette = (uint[])_palette.Clone();
        var state = new DecodeState(
            workingPalette,
            MacroAspectRatio(macroParameter),
            backgroundSelect == 1,
            backgroundColor);
        var parameters = new int[5];
        var offset = 0;
        while (offset < data.Length)
        {
            var value = data[offset++];
            if (value is >= (byte)'?' and <= (byte)'~')
            {
                if (!state.WriteSixel(value - '?', 1))
                {
                    return false;
                }

                continue;
            }

            switch (value)
            {
                case (byte)'!':
                    {
                        var parameterCount = ReadParameters(data, ref offset, parameters.AsSpan(0, 1));
                        var repeat = Parameter(parameters.AsSpan(0, parameterCount), 0, 1);
                        if (offset < data.Length && data[offset] is >= (byte)'?' and <= (byte)'~')
                        {
                            if (!state.WriteSixel(data[offset++] - '?', Math.Max(1, repeat)))
                            {
                                return false;
                            }
                        }

                        break;
                    }
                case (byte)'#':
                    {
                        var parameterCount = ReadParameters(data, ref offset, parameters);
                        state.SelectColor(parameters.AsSpan(0, parameterCount));
                        break;
                    }
                case (byte)'"':
                    {
                        var parameterCount = ReadParameters(data, ref offset, parameters.AsSpan(0, 4));
                        if (!state.SetRasterAttributes(parameters.AsSpan(0, parameterCount)))
                        {
                            return false;
                        }

                        break;
                    }
                case (byte)'$':
                    state.CarriageReturn();
                    break;
                case (byte)'-':
                    if (!state.NextLine())
                    {
                        return false;
                    }

                    break;
            }
        }

        if (!state.TryCreateImage(out image))
        {
            return false;
        }

        workingPalette.CopyTo(_palette, 0);
        return true;
    }

    private sealed class DecodeState
    {
        private readonly uint[] _palette;
        private readonly bool _transparentBackground;
        private ushort[] _pixels = [];
        private int _capacityWidth;
        private int _capacityHeight;
        private int _cursorX;
        private int _cursorY;
        private int _imageWidth;
        private int _imageHeight;
        private int _rasterWidth;
        private int _rasterHeight;
        private int _aspectRatio;
        private ushort _foreground = 15;
        private readonly ushort _background;
        private long _remainingPixelWrites = TerminalImageLimits.MaximumSixelPixelWrites;

        public DecodeState(uint[] palette, int aspectRatio, bool transparentBackground, int backgroundColor)
        {
            _palette = palette;
            _aspectRatio = aspectRatio;
            _transparentBackground = transparentBackground;
            _background = (ushort)Math.Clamp(backgroundColor, 0, MaximumColors - 1);
        }

        public bool WriteSixel(int value, int repeat)
        {
            if (repeat > TerminalImageLimits.MaximumPixelDimension)
            {
                return false;
            }
            var requiredWidth = (long)_cursorX + repeat;
            var requiredHeight = (long)_cursorY + (6L * _aspectRatio);
            var pixelWrites = (long)CountSetBits(value) * _aspectRatio * repeat;
            if (!DimensionsAllowed(requiredWidth, requiredHeight) ||
                pixelWrites > _remainingPixelWrites ||
                !EnsureCapacity((int)requiredWidth, (int)requiredHeight))
            {
                return false;
            }

            _imageWidth = Math.Max(_imageWidth, (int)requiredWidth);
            _imageHeight = Math.Max(_imageHeight, (int)requiredHeight);
            _remainingPixelWrites -= pixelWrites;
            for (var bit = 0; bit < 6; bit++)
            {
                if ((value & (1 << bit)) == 0)
                {
                    continue;
                }

                var firstRow = _cursorY + (bit * _aspectRatio);
                for (var row = 0; row < _aspectRatio; row++)
                {
                    _pixels.AsSpan(((firstRow + row) * _capacityWidth) + _cursorX, repeat).Fill(_foreground);
                }
            }

            _cursorX += repeat;
            return true;
        }

        public void SelectColor(ReadOnlySpan<int> parameters)
        {
            var color = Math.Clamp(Parameter(parameters, 0, 0), 0, MaximumColors - 1);
            if (parameters.Length >= 2)
            {
                var model = Parameter(parameters, 1, 0);
                var first = Parameter(parameters, 2, 0);
                var second = Parameter(parameters, 3, 0);
                var third = Parameter(parameters, 4, 0);
                if (model == 1)
                {
                    _palette[color] = HlsToRgba(first, second, third);
                }
                else if (model == 2)
                {
                    _palette[color] = RgbPercentToRgba(first, second, third);
                }
            }

            _foreground = (ushort)color;
        }

        public bool SetRasterAttributes(ReadOnlySpan<int> parameters)
        {
            var numerator = Parameter(parameters, 0, 0);
            var denominator = Parameter(parameters, 1, 0);
            if (denominator > 0)
            {
                _aspectRatio = Math.Clamp((numerator + denominator - 1) / denominator, 1, MaximumAspectRatio);
            }

            var width = Parameter(parameters, 2, 0);
            var height = Parameter(parameters, 3, 0);
            if (width > 0)
            {
                _rasterWidth = width;
            }

            if (height > 0)
            {
                _rasterHeight = height;
            }

            CarriageReturn();
            return DimensionsAllowed(
                Math.Max(_imageWidth, _rasterWidth),
                Math.Max(_imageHeight, _rasterHeight));
        }

        public void CarriageReturn() => _cursorX = 0;

        public bool NextLine()
        {
            _cursorX = 0;
            var next = (long)_cursorY + (6L * _aspectRatio);
            if (next > TerminalImageLimits.MaximumPixelDimension)
            {
                return false;
            }

            _cursorY = (int)next;
            return true;
        }

        public bool TryCreateImage(out SixelImage? image)
        {
            image = null;
            var width = Math.Max(_imageWidth, _rasterWidth);
            var height = Math.Max(_imageHeight, _rasterHeight);
            if (width == 0 || height == 0 || !DimensionsAllowed(width, height))
            {
                return false;
            }

            var result = new ushort[checked(width * height)];
            Array.Fill(result, _transparentBackground ? SixelImage.TransparentColorIndex : _background);
            for (var row = 0; row < _imageHeight; row++)
            {
                _pixels.AsSpan(row * _capacityWidth, _imageWidth)
                    .CopyTo(result.AsSpan(row * width, _imageWidth));
            }

            if (!_transparentBackground)
            {
                for (var index = 0; index < result.Length; index++)
                {
                    if (result[index] == SixelImage.TransparentColorIndex)
                    {
                        result[index] = _background;
                    }
                }
            }

            image = new SixelImage(
                width,
                height,
                _aspectRatio,
                _transparentBackground,
                _cursorY,
                result,
                (uint[])_palette.Clone());
            return true;
        }

        private bool EnsureCapacity(int width, int height)
        {
            if (width <= _capacityWidth && height <= _capacityHeight)
            {
                return true;
            }

            var newWidth = Grow(_capacityWidth, width);
            var newHeight = Grow(_capacityHeight, height);
            if ((long)newWidth * newHeight > TerminalImageLimits.MaximumPixelCount)
            {
                newWidth = width;
                newHeight = height;
                if ((long)newWidth * newHeight > TerminalImageLimits.MaximumPixelCount)
                {
                    return false;
                }
            }

            var replacement = new ushort[checked(newWidth * newHeight)];
            Array.Fill(replacement, SixelImage.TransparentColorIndex);
            for (var row = 0; row < _capacityHeight; row++)
            {
                _pixels.AsSpan(row * _capacityWidth, _capacityWidth)
                    .CopyTo(replacement.AsSpan(row * newWidth, _capacityWidth));
            }

            _pixels = replacement;
            _capacityWidth = newWidth;
            _capacityHeight = newHeight;
            return true;
        }

        private static int Grow(int current, int required)
        {
            var value = Math.Max(8, current);
            while (value < required && value < TerminalImageLimits.MaximumPixelDimension)
            {
                value = Math.Min(value * 2, TerminalImageLimits.MaximumPixelDimension);
            }

            return value;
        }

        private static int CountSetBits(int value)
        {
            var count = 0;
            while (value != 0)
            {
                count += value & 1;
                value >>= 1;
            }

            return count;
        }
    }

    private static int ReadParameters(ReadOnlySpan<byte> data, ref int offset, Span<int> parameters)
    {
        parameters.Fill(-1);
        var count = 0;
        var hasParameterByte = false;
        var ignoreRemainder = false;
        while (offset < data.Length)
        {
            var value = data[offset];
            if (value is >= (byte)'0' and <= (byte)'9')
            {
                hasParameterByte = true;
                if (count == 0)
                {
                    count = 1;
                }

                if (!ignoreRemainder)
                {
                    parameters[count - 1] = Math.Min(
                        (Math.Max(0, parameters[count - 1]) * 10) + (value - '0'),
                        65535);
                }

                offset++;
            }
            else if (value == (byte)';')
            {
                hasParameterByte = true;
                if (count < parameters.Length)
                {
                    count++;
                }
                else
                {
                    ignoreRemainder = true;
                }

                offset++;
            }
            else
            {
                break;
            }
        }

        return hasParameterByte ? Math.Max(1, count) : 0;
    }

    private static int Parameter(ReadOnlySpan<int> parameters, int index, int defaultValue) =>
        (uint)index < (uint)parameters.Length && parameters[index] >= 0 ? parameters[index] : defaultValue;

    private static bool DimensionsAllowed(long width, long height) =>
        width is >= 0 and <= TerminalImageLimits.MaximumPixelDimension &&
        height is >= 0 and <= TerminalImageLimits.MaximumPixelDimension &&
        width * height <= TerminalImageLimits.MaximumPixelCount;

    private static int MacroAspectRatio(int value) => value switch
    {
        0 or 1 or 5 or 6 => 2,
        2 => 5,
        3 or 4 => 3,
        _ => 1,
    };

    private static uint RgbPercentToRgba(int red, int green, int blue) =>
        Pack(
            PercentToByte(red),
            PercentToByte(green),
            PercentToByte(blue));

    private static uint HlsToRgba(int hue, int lightness, int saturation)
    {
        var h = ((Math.Clamp(hue, 0, 360) + 240) % 360) / 360d;
        var l = Math.Clamp(lightness, 0, 100) / 100d;
        var s = Math.Clamp(saturation, 0, 100) / 100d;
        if (s == 0)
        {
            var gray = (byte)Math.Round(l * 255);
            return Pack(gray, gray, gray);
        }

        var q = l < 0.5 ? l * (1 + s) : l + s - (l * s);
        var p = (2 * l) - q;
        return Pack(
            UnitToByte(HueToRgb(p, q, h + (1d / 3d))),
            UnitToByte(HueToRgb(p, q, h)),
            UnitToByte(HueToRgb(p, q, h - (1d / 3d))));
    }

    private static double HueToRgb(double p, double q, double value)
    {
        if (value < 0)
        {
            value++;
        }
        else if (value > 1)
        {
            value--;
        }

        if (value < 1d / 6d)
        {
            return p + ((q - p) * 6 * value);
        }

        if (value < 0.5)
        {
            return q;
        }

        return value < 2d / 3d ? p + ((q - p) * ((2d / 3d) - value) * 6) : p;
    }

    private static byte PercentToByte(int value) =>
        (byte)Math.Round(Math.Clamp(value, 0, 100) * 255d / 100d);

    private static byte UnitToByte(double value) => (byte)Math.Round(Math.Clamp(value, 0, 1) * 255);

    private static uint Pack(byte red, byte green, byte blue) =>
        0xFF000000u | ((uint)red << 16) | ((uint)green << 8) | blue;

    private static uint[] CreateDefaultPalette()
    {
        var palette = new uint[MaximumColors];
        uint[] vt340 =
        [
            0xFF000000, 0xFF3333CC, 0xFFCC3333, 0xFF33CC33,
            0xFFCC33CC, 0xFF33CCCC, 0xFFCCCC33, 0xFF878787,
            0xFF424242, 0xFF5454FF, 0xFFFF5454, 0xFF54FF54,
            0xFFFF54FF, 0xFF54FFFF, 0xFFFFFF54, 0xFFFFFFFF,
        ];
        vt340.CopyTo(palette, 0);

        var index = 16;
        int[] levels = [0, 95, 135, 175, 215, 255];
        foreach (var red in levels)
        {
            foreach (var green in levels)
            {
                foreach (var blue in levels)
                {
                    palette[index++] = Pack((byte)red, (byte)green, (byte)blue);
                }
            }
        }

        for (var gray = 0; gray < 24; gray++)
        {
            var value = (byte)(8 + (gray * 10));
            palette[index++] = Pack(value, value, value);
        }

        return palette;
    }
}
