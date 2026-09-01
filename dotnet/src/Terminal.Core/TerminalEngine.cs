using System.Text;
using System.Globalization;

namespace Microsoft.Terminal.Core;

public sealed record TerminalSnapshot(
    TextBufferSnapshot Buffer,
    string Title,
    string? WorkingDirectory,
    bool AlternateBufferActive,
    bool CursorVisible,
    bool ApplicationCursorKeys,
    bool BracketedPaste,
    bool MouseTracking,
    bool SgrMouse,
    bool AutoWrap,
    bool InsertMode,
    bool ReverseVideo)
{
    public IReadOnlyList<TerminalImageOverlay> Images { get; init; } = [];
}

public sealed record TerminalNotification(string? Title, string Body);

public sealed class TerminalEngine : IVtDispatch
{
    private readonly VtParser _parser;
    private readonly SixelDecoder _sixelDecoder = new();
    private readonly TextBuffer _primary;
    private readonly TextBuffer _alternate;
    private readonly List<TerminalImageOverlay> _images = [];
    private TextBuffer _active;
    private CellAttributes _sgr = CellAttributes.Default;
    private ColorScheme _scheme;
    private ColorScheme _defaultScheme;
    private Rune _lastPrintedRune;
    private bool _hasLastPrintedRune;
    private long _nextImageId;
    private long _retainedImageBytes;

    public TerminalEngine(int columns = 120, int rows = 30, int historySize = 9001)
    {
        _primary = new TextBuffer(columns, rows, historySize, hasHistory: true);
        _alternate = new TextBuffer(columns, rows, 0, hasHistory: false);
        _active = _primary;
        _parser = new VtParser(this);
        _scheme = ColorScheme.Campbell;
        _defaultScheme = _scheme;
        CursorVisible = true;
        CursorBlinking = true;
        AutoWrap = true;
    }

    public TextBuffer Buffer => _active;
    public ColorScheme Scheme
    {
        get => _scheme;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _scheme = value;
            _defaultScheme = value;
        }
    }

    public string Title { get; private set; } = "Windows Terminal";
    public string? WorkingDirectory { get; private set; }
    public bool AlternateBufferActive => ReferenceEquals(_active, _alternate);
    public bool CursorVisible { get; private set; }
    public bool CursorBlinking { get; private set; }
    public bool ApplicationCursorKeys { get; private set; }
    public bool BracketedPaste { get; private set; }
    public bool MouseTracking { get; private set; }
    public bool SgrMouse { get; private set; }
    public bool FocusTracking { get; private set; }
    public bool AutoWrap { get; private set; }
    public bool InsertMode { get; private set; }
    public bool NewLineMode { get; private set; }
    public bool ReverseVideo { get; private set; }
    public bool AllowClipboardWrite { get; private set; }
    public bool AllowNotifications { get; private set; }
    public int Columns => Buffer.Columns;
    public int Rows => Buffer.Rows;
    public int CursorX => Buffer.CursorX;
    public int CursorY => Buffer.CursorY;
    public IReadOnlyList<TerminalImageOverlay> Images => _images;

    public event EventHandler? Invalidated;
    public event EventHandler<string>? TitleChanged;
    public event EventHandler<string?>? WorkingDirectoryChanged;
    public event EventHandler? ShellIntegrationChanged;
    public event EventHandler<string>? ClipboardWriteRequested;
    public event EventHandler<TerminalNotification>? NotificationRequested;
    public event EventHandler<TerminalImageOverlay>? ImageAdded;
    public event EventHandler? Bell;
    public event EventHandler<byte[]>? ResponseReady;

    public void Feed(ReadOnlySpan<byte> data)
    {
        _parser.Process(data);
        Invalidated?.Invoke(this, EventArgs.Empty);
    }

    public void Feed(string text) => Feed(Encoding.UTF8.GetBytes(text));

    public void Resize(int columns, int rows)
    {
        _primary.Resize(columns, rows);
        _alternate.Resize(columns, rows);
        Invalidated?.Invoke(this, EventArgs.Empty);
    }

    public void Reset()
    {
        _parser.Reset();
        _sgr = CellAttributes.Default;
        _active = _primary;
        CursorVisible = true;
        CursorBlinking = true;
        ApplicationCursorKeys = false;
        BracketedPaste = false;
        MouseTracking = false;
        SgrMouse = false;
        FocusTracking = false;
        AutoWrap = true;
        InsertMode = false;
        NewLineMode = false;
        ReverseVideo = false;
        _scheme = _defaultScheme;
        _hasLastPrintedRune = false;
        _sixelDecoder.Reset();
        _images.Clear();
        _retainedImageBytes = 0;
        _primary.Reset(keepHistory: false);
        _alternate.Reset(keepHistory: false);
        Invalidated?.Invoke(this, EventArgs.Empty);
    }

    public void ConfigureOptionalFeatures(bool allowClipboardWrite, bool allowNotifications)
    {
        AllowClipboardWrite = allowClipboardWrite;
        AllowNotifications = allowNotifications;
    }

    public TerminalSnapshot CreateSnapshot(bool includeHistory = false) => new(
            Buffer.CreateSnapshot(includeHistory),
            Title,
            WorkingDirectory,
            AlternateBufferActive,
            CursorVisible,
            ApplicationCursorKeys,
            BracketedPaste,
            MouseTracking,
            SgrMouse,
            AutoWrap,
            InsertMode,
            ReverseVideo)
    {
        Images = _images
            .Select(image => image with
            {
                AnchorRow = image.AnchorRow - (Buffer.ViewportStart - Buffer.ScrollOffset),
            })
            .ToArray(),
    };

    public string CopySelection(int x1, int y1, int x2, int y2) => Buffer.GetText(x1, y1, x2, y2);

    public string WrapPaste(string text)
    {
        if (!BracketedPaste)
        {
            return text;
        }

        return "\u001b[200~" + text.Replace("\u001b", string.Empty, StringComparison.Ordinal) + "\u001b[201~";
    }

    void IVtDispatch.Print(Rune rune)
    {
        if (!AutoWrap && Buffer.WrapPending)
        {
            Buffer.WrapPending = false;
        }

        if (InsertMode && WcWidth.Width(rune) > 0)
        {
            Buffer.InsertCharacters(WcWidth.Width(rune));
        }

        Buffer.Print(rune);
        if (WcWidth.Width(rune) > 0)
        {
            _lastPrintedRune = rune;
            _hasLastPrintedRune = true;
        }
    }

    void IVtDispatch.ExecuteC0(byte control)
    {
        switch (control)
        {
            case 0x07:
                Bell?.Invoke(this, EventArgs.Empty);
                break;
            case 0x08:
                Buffer.Backspace();
                break;
            case 0x09:
                Buffer.Tab();
                break;
            case 0x0A:
            case 0x0B:
            case 0x0C:
                Buffer.LineFeed(alsoCarriageReturn: NewLineMode);
                break;
            case 0x0D:
                Buffer.CarriageReturn();
                break;
        }
    }

    void IVtDispatch.EscDispatch(char final, byte intermediate)
    {
        switch (final)
        {
            case '7':
                Buffer.SaveCursor();
                break;
            case '8':
                Buffer.RestoreCursor();
                _sgr = Buffer.CurrentAttributes;
                break;
            case 'c':
                Reset();
                break;
            case 'D':
                Buffer.LineFeed();
                break;
            case 'E':
                Buffer.LineFeed(alsoCarriageReturn: true);
                break;
            case 'H':
                Buffer.SetTabStop();
                break;
            case 'M':
                Buffer.ReverseIndex();
                break;
        }
    }

    void IVtDispatch.CsiDispatch(char final, ReadOnlySpan<int> parameters, byte intermediate, bool privateMarker) =>
        DispatchCsi(final, parameters, intermediate, privateMarker ? (byte)'?' : (byte)0);

    void IVtDispatch.CsiDispatch(char final, ReadOnlySpan<int> parameters, byte intermediate, byte privateMarker) =>
        DispatchCsi(final, parameters, intermediate, privateMarker);

    void IVtDispatch.DcsDispatch(
        char final,
        ReadOnlySpan<int> parameters,
        ReadOnlySpan<byte> intermediates,
        byte privateMarker,
        ReadOnlySpan<byte> data)
    {
        if (privateMarker != 0)
        {
            return;
        }

        if (final == 'q' && intermediates.IsEmpty)
        {
            DispatchSixel(parameters, data);
        }
        else if (final == 'q' && intermediates.SequenceEqual([(byte)'$']))
        {
            DispatchRequestSetting(data);
        }
        else if (final == 'q' && intermediates.SequenceEqual([(byte)'+']))
        {
            DispatchTermcapRequest(data);
        }
    }

    void IVtDispatch.OscDispatch(int command, ReadOnlySpan<char> data)
    {
        switch (command)
        {
            case 0:
            case 2:
                Title = data.ToString();
                TitleChanged?.Invoke(this, Title);
                break;
            case 4:
                DispatchColorTable(data);
                break;
            case 7:
                WorkingDirectory = data.ToString();
                WorkingDirectoryChanged?.Invoke(this, WorkingDirectory);
                break;
            case 8:
                DispatchHyperlink(data);
                break;
            case 9:
                DispatchWindowsNotification(data);
                break;
            case 10:
            case 11:
            case 12:
                DispatchDynamicColors(command, data);
                break;
            case 52:
                DispatchClipboard(data);
                break;
            case 133:
                DispatchShellIntegration(data);
                break;
            case 1337:
                DispatchInlineImage(data);
                break;
            case 777:
                DispatchRxvtNotification(data);
                break;
        }
    }

    private void DispatchSixel(ReadOnlySpan<int> parameters, ReadOnlySpan<byte> data)
    {
        if (!_sixelDecoder.TryDecode(
                data,
                Param(parameters, 0, 0),
                Param(parameters, 1, 0),
                Param(parameters, 2, 0),
                out var image) ||
            image is null)
        {
            return;
        }

        AddImage(new TerminalImageOverlay(
            ++_nextImageId,
            TerminalImageProtocol.Sixel,
            AlternateBufferActive,
            Buffer.CursorX,
            Buffer.ViewportStart + Buffer.CursorY,
            image,
            null));
    }

    private void DispatchRequestSetting(ReadOnlySpan<byte> data)
    {
        var request = Encoding.ASCII.GetString(data);
        var response = request switch
        {
            "m" => $"1$r{FormatSgrSetting()}m",
            "r" => $"1$r{Buffer.ScrollTop + 1};{Buffer.ScrollBottom + 1}r",
            "s" => $"1$r1;{Buffer.Columns}s",
            " q" => "1$r0 q",
            "\"q" => "1$r0\"q",
            "*x" => "1$r1*x",
            _ => "0$r",
        };
        RespondDcs(response);
    }

    private void DispatchTermcapRequest(ReadOnlySpan<byte> data)
    {
        const int maximumRequests = 32;
        var offset = 0;
        for (var requestCount = 0; requestCount < maximumRequests && offset < data.Length; requestCount++)
        {
            var remaining = data[offset..];
            var separator = remaining.IndexOf((byte)';');
            var requestBytes = separator < 0 ? remaining : remaining[..separator];
            offset += separator < 0 ? remaining.Length : separator + 1;
            if (requestBytes.IsEmpty)
            {
                continue;
            }

            if (requestBytes.Length > 128)
            {
                RespondDcs("0+r");
                continue;
            }

            var request = Encoding.ASCII.GetString(requestBytes);
            if (!TryDecodeHexAscii(request, out var name))
            {
                RespondDcs($"0+r{request}");
                continue;
            }

            var value = name switch
            {
                "TN" => "xterm-256color",
                "Co" => "256",
                "RGB" or "Tc" => "1",
                _ => null,
            };
            RespondDcs(value is null
                ? $"0+r{request}"
                : $"1+r{request}={Convert.ToHexString(Encoding.ASCII.GetBytes(value))}");
        }
    }

    private string FormatSgrSetting()
    {
        var values = new List<string> { "0" };
        AddSgrFlag(values, CellFlags.Bold, "1");
        AddSgrFlag(values, CellFlags.Faint, "2");
        AddSgrFlag(values, CellFlags.Italic, "3");
        AddSgrFlag(values, CellFlags.Underline, "4");
        AddSgrFlag(values, CellFlags.Blink, "5");
        AddSgrFlag(values, CellFlags.Inverse, "7");
        AddSgrFlag(values, CellFlags.Invisible, "8");
        AddSgrFlag(values, CellFlags.Strikethrough, "9");
        AddSgrColor(values, _sgr.Foreground, foreground: true);
        AddSgrColor(values, _sgr.Background, foreground: false);
        return string.Join(';', values);
    }

    private void AddSgrFlag(List<string> values, CellFlags flag, string parameter)
    {
        if ((_sgr.Flags & flag) != 0)
        {
            values.Add(parameter);
        }
    }

    private static void AddSgrColor(List<string> values, TermColor color, bool foreground)
    {
        if (color.Kind == ColorKind.Indexed)
        {
            if (color.Index < 16)
            {
                values.Add((color.Index < 8
                    ? (foreground ? 30 : 40) + color.Index
                    : (foreground ? 90 : 100) + color.Index - 8).ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                values.Add($"{(foreground ? 38 : 48)}:5:{color.Index}");
            }
        }
        else if (color.Kind == ColorKind.Rgb)
        {
            values.Add($"{(foreground ? 38 : 48)}:2::{color.R}:{color.G}:{color.B}");
        }
    }

    private static bool TryDecodeHexAscii(ReadOnlySpan<char> value, out string decoded)
    {
        decoded = string.Empty;
        if (value.Length is 0 or > 128 || (value.Length & 1) != 0)
        {
            return false;
        }

        try
        {
            var bytes = Convert.FromHexString(value);
            if (bytes.Any(static value => value is < 0x20 or > 0x7E))
            {
                return false;
            }

            decoded = Encoding.ASCII.GetString(bytes);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private void DispatchInlineImage(ReadOnlySpan<char> data)
    {
        if (!data.StartsWith("File="))
        {
            return;
        }

        var separator = data.IndexOf(':');
        if (separator < 0)
        {
            return;
        }

        var header = data[5..separator];
        var payload = data[(separator + 1)..];
        if (payload.Length > ((TerminalImageLimits.MaximumInlineImageBytes * 4 / 3) + 4) ||
            !IsStrictBase64(payload))
        {
            return;
        }

        string? name = null;
        long? declaredSize = null;
        var width = TerminalImageDimension.Auto;
        var height = TerminalImageDimension.Auto;
        var preserveAspectRatio = true;
        var inline = false;
        const int maximumMetadataItems = 32;
        var headerOffset = 0;
        for (var itemCount = 0; itemCount < maximumMetadataItems && headerOffset < header.Length; itemCount++)
        {
            var remaining = header[headerOffset..];
            var itemSeparator = remaining.IndexOf(';');
            var item = itemSeparator < 0 ? remaining : remaining[..itemSeparator];
            headerOffset += itemSeparator < 0 ? remaining.Length : itemSeparator + 1;
            if (item.IsEmpty)
            {
                continue;
            }

            var equals = item.IndexOf('=');
            if (equals < 0)
            {
                continue;
            }

            var key = item[..equals];
            var value = item[(equals + 1)..];
            if (key.SequenceEqual("inline"))
            {
                inline = value.SequenceEqual("1");
            }
            else if (key.SequenceEqual("name"))
            {
                name = DecodeInlineName(value);
            }
            else if (key.SequenceEqual("size"))
            {
                if (long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedSize))
                {
                    declaredSize = parsedSize;
                }
            }
            else if (key.SequenceEqual("width"))
            {
                width = ParseInlineDimension(value);
            }
            else if (key.SequenceEqual("height"))
            {
                height = ParseInlineDimension(value);
            }
            else if (key.SequenceEqual("preserveAspectRatio"))
            {
                preserveAspectRatio = !value.SequenceEqual("0");
            }
        }

        if (!inline || declaredSize is > TerminalImageLimits.MaximumInlineImageBytes)
        {
            return;
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(payload.ToString());
        }
        catch (FormatException)
        {
            return;
        }

        if (bytes.Length > TerminalImageLimits.MaximumInlineImageBytes)
        {
            return;
        }

        var inlineImage = new InlineImage(
            new InlineImageMetadata(name, declaredSize, width, height, preserveAspectRatio),
            bytes);
        AddImage(new TerminalImageOverlay(
            ++_nextImageId,
            TerminalImageProtocol.Iterm2Inline,
            AlternateBufferActive,
            Buffer.CursorX,
            Buffer.ViewportStart + Buffer.CursorY,
            null,
            inlineImage));
    }

    private static string? DecodeInlineName(ReadOnlySpan<char> value)
    {
        if (value.Length > 4096 || !IsStrictBase64(value))
        {
            return null;
        }

        try
        {
            return new UTF8Encoding(false, true).GetString(Convert.FromBase64String(value.ToString()));
        }
        catch (FormatException)
        {
            return null;
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    private static TerminalImageDimension ParseInlineDimension(ReadOnlySpan<char> value)
    {
        if (value.SequenceEqual("auto"))
        {
            return TerminalImageDimension.Auto;
        }

        var kind = TerminalImageDimensionKind.Cells;
        if (value.EndsWith("px", StringComparison.Ordinal))
        {
            kind = TerminalImageDimensionKind.Pixels;
            value = value[..^2];
        }
        else if (value.EndsWith("%", StringComparison.Ordinal))
        {
            kind = TerminalImageDimensionKind.Percent;
            value = value[..^1];
        }

        var maximum = kind == TerminalImageDimensionKind.Percent
            ? 100
            : TerminalImageLimits.MaximumPixelDimension;
        return double.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) &&
               double.IsFinite(number) &&
               number >= 0 &&
               number <= maximum
            ? new TerminalImageDimension(kind, number)
            : TerminalImageDimension.Auto;
    }

    private void AddImage(TerminalImageOverlay image)
    {
        var imageBytes = image.Sixel?.EstimatedByteSize ?? image.InlineImage?.EstimatedByteSize ?? 0;
        if (imageBytes > TerminalImageLimits.MaximumRetainedImageBytes)
        {
            return;
        }

        while (_images.Count > 0 &&
               (_images.Count >= TerminalImageLimits.MaximumRetainedImages ||
                _retainedImageBytes + imageBytes > TerminalImageLimits.MaximumRetainedImageBytes))
        {
            var removed = _images[0];
            _retainedImageBytes -=
                removed.Sixel?.EstimatedByteSize ?? removed.InlineImage?.EstimatedByteSize ?? 0;
            _images.RemoveAt(0);
        }

        _images.Add(image);
        _retainedImageBytes += imageBytes;
        ImageAdded?.Invoke(this, image);
    }

    private void DispatchClipboard(ReadOnlySpan<char> data)
    {
        if (!AllowClipboardWrite)
        {
            return;
        }

        var separator = data.IndexOf(';');
        if (separator < 0)
        {
            return;
        }

        var payload = data[(separator + 1)..];
        if (payload.SequenceEqual("?"))
        {
            return;
        }

        const int maximumEncodedLength = (1024 * 1024 * 4 / 3) + 4;
        if (payload.Length > maximumEncodedLength)
        {
            return;
        }

        if (!IsStrictBase64(payload))
        {
            return;
        }

        string text;
        try
        {
            var bytes = Convert.FromBase64String(payload.ToString());
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes);
        }
        catch (FormatException)
        {
            return;
        }
        catch (DecoderFallbackException)
        {
            return;
        }

        ClipboardWriteRequested?.Invoke(this, text);
    }

    private void DispatchWindowsNotification(ReadOnlySpan<char> data)
    {
        if (data.StartsWith("9;"))
        {
            WorkingDirectory = data[2..].ToString();
            WorkingDirectoryChanged?.Invoke(this, WorkingDirectory);
            return;
        }

        if (AllowNotifications && !data.IsEmpty)
        {
            NotificationRequested?.Invoke(this, new TerminalNotification(null, data.ToString()));
        }
    }

    private void DispatchRxvtNotification(ReadOnlySpan<char> data)
    {
        if (!AllowNotifications || !data.StartsWith("notify;"))
        {
            return;
        }

        var content = data[7..];
        var separator = content.IndexOf(';');
        if (separator < 0)
        {
            return;
        }

        var title = content[..separator].ToString();
        var body = content[(separator + 1)..].ToString();
        NotificationRequested?.Invoke(this, new TerminalNotification(title, body));
    }

    private static bool IsStrictBase64(ReadOnlySpan<char> payload)
    {
        if (payload.IsEmpty)
        {
            return true;
        }

        if (payload.Length % 4 != 0)
        {
            return false;
        }

        var padding = 0;
        for (var index = 0; index < payload.Length; index++)
        {
            var value = payload[index];
            if (value == '=')
            {
                padding++;
                if (index < payload.Length - 2 || padding > 2)
                {
                    return false;
                }
            }
            else if (padding > 0 ||
                     !(value is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '+' or '/'))
            {
                return false;
            }
        }

        return true;
    }

    private void DispatchShellIntegration(ReadOnlySpan<char> data)
    {
        if (data.IsEmpty)
        {
            return;
        }

        switch (data[0])
        {
            case 'A':
                Buffer.StartPrompt();
                break;
            case 'B':
                Buffer.StartCommand();
                break;
            case 'C':
                Buffer.StartOutput();
                break;
            case 'D':
                uint? exitCode = null;
                if (data.Length >= 2 && data[1] == ';')
                {
                    exitCode = uint.TryParse(
                        data[2..],
                        System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var parsed)
                        ? parsed
                        : uint.MaxValue;
                }

                Buffer.EndCommand(exitCode);
                break;
            default:
                return;
        }

        ShellIntegrationChanged?.Invoke(this, EventArgs.Empty);
    }

    private void DispatchCsi(char final, ReadOnlySpan<int> parameters, byte intermediate, byte privateMarker)
    {
        if (intermediate == (byte)'$' && final == 'p')
        {
            ReportMode(Param(parameters, 0, 0), privateMarker == (byte)'?');
            return;
        }

        if (privateMarker == (byte)'?' && final is 'h' or 'l')
        {
            DispatchPrivateModes(final == 'h', parameters);
            return;
        }

        if (privateMarker == (byte)'?' && final == 'n')
        {
            DeviceStatus(Param(parameters, 0, 0), privateReport: true);
            return;
        }

        if (final == 'c')
        {
            if (privateMarker == (byte)'>')
            {
                Respond("\u001b[>0;10;1c");
            }
            else if (privateMarker == 0 && Param(parameters, 0, 0) == 0)
            {
                Respond("\u001b[?61;4;6;7;14;21;22;23;24;28;32;42c");
            }

            return;
        }

        if (privateMarker != 0)
        {
            return;
        }

        switch (final)
        {
            case 'A':
                Buffer.MoveCursor(0, -Count(parameters), respectMargins: true);
                break;
            case 'B':
            case 'e':
                Buffer.MoveCursor(0, Count(parameters), respectMargins: true);
                break;
            case 'C':
            case 'a':
                Buffer.MoveCursor(Count(parameters), 0);
                break;
            case 'D':
                Buffer.MoveCursor(-Count(parameters), 0);
                break;
            case 'E':
                Buffer.MoveCursor(0, Count(parameters), respectMargins: true);
                Buffer.CarriageReturn();
                break;
            case 'F':
                Buffer.MoveCursor(0, -Count(parameters), respectMargins: true);
                Buffer.CarriageReturn();
                break;
            case 'G':
            case '`':
                Buffer.SetCursor(Buffer.CursorY, OneBased(parameters, 0), relativeToOrigin: false);
                break;
            case 'H':
            case 'f':
                Buffer.SetCursor(OneBased(parameters, 0), OneBased(parameters, 1));
                break;
            case 'I':
                Buffer.Tab(Count(parameters));
                break;
            case 'J':
                Buffer.EraseInDisplay(Param(parameters, 0, 0));
                break;
            case 'K':
                Buffer.EraseInLine(Param(parameters, 0, 0));
                break;
            case 'L':
                Buffer.InsertLines(Count(parameters));
                break;
            case 'M':
                Buffer.DeleteLines(Count(parameters));
                break;
            case 'P':
                Buffer.DeleteCharacters(Count(parameters));
                break;
            case 'S':
                Buffer.ScrollUp(Count(parameters));
                break;
            case 'T':
                Buffer.ScrollDown(Count(parameters));
                break;
            case 'X':
                Buffer.EraseCharacters(Count(parameters));
                break;
            case 'Z':
                Buffer.BackTab(Count(parameters));
                break;
            case '@':
                Buffer.InsertCharacters(Count(parameters));
                break;
            case 'b':
                RepeatLastCharacter(Count(parameters));
                break;
            case 'd':
                Buffer.SetCursor(OneBased(parameters, 0), Buffer.CursorX);
                break;
            case 'g':
                Buffer.ClearTabStop(Param(parameters, 0, 0) == 3);
                break;
            case 'h':
            case 'l':
                DispatchAnsiModes(final == 'h', parameters);
                break;
            case 'm':
                ApplySgr(parameters);
                break;
            case 'n':
                DeviceStatus(Param(parameters, 0, 0), privateReport: false);
                break;
            case 'r':
                Buffer.SetScrollRegion(
                    OneBased(parameters, 0),
                    Math.Max(1, Param(parameters, 1, Buffer.Rows)) - 1);
                break;
            case 's':
                Buffer.SaveCursor();
                break;
            case 'u':
                Buffer.RestoreCursor();
                _sgr = Buffer.CurrentAttributes;
                break;
        }
    }

    private void DispatchAnsiModes(bool enable, ReadOnlySpan<int> parameters)
    {
        for (var i = 0; i < parameters.Length; i++)
        {
            switch (Param(parameters, i, 0))
            {
                case 4:
                    InsertMode = enable;
                    break;
                case 20:
                    NewLineMode = enable;
                    break;
            }
        }
    }

    private void DispatchPrivateModes(bool enable, ReadOnlySpan<int> parameters)
    {
        for (var i = 0; i < parameters.Length; i++)
        {
            var mode = Param(parameters, i, 0);
            switch (mode)
            {
                case 1:
                    ApplicationCursorKeys = enable;
                    break;
                case 5:
                    ReverseVideo = enable;
                    break;
                case 6:
                    Buffer.OriginMode = enable;
                    Buffer.SetCursor(0, 0);
                    break;
                case 7:
                    AutoWrap = enable;
                    Buffer.WrapPending = false;
                    break;
                case 12:
                    CursorBlinking = enable;
                    break;
                case 25:
                    CursorVisible = enable;
                    break;
                case 47:
                    SetAlternateBuffer(enable, clearOnEnter: false, saveCursor: false);
                    break;
                case 1000:
                case 1002:
                case 1003:
                    MouseTracking = enable;
                    break;
                case 1004:
                    FocusTracking = enable;
                    break;
                case 1006:
                    SgrMouse = enable;
                    break;
                case 1047:
                    SetAlternateBuffer(enable, clearOnEnter: true, saveCursor: false);
                    break;
                case 1048:
                    if (enable)
                    {
                        Buffer.SaveCursor();
                    }
                    else
                    {
                        Buffer.RestoreCursor();
                        _sgr = Buffer.CurrentAttributes;
                    }

                    break;
                case 1049:
                    SetAlternateBuffer(enable, clearOnEnter: true, saveCursor: true);
                    break;
                case 2004:
                    BracketedPaste = enable;
                    break;
            }
        }
    }

    private void SetAlternateBuffer(bool enable, bool clearOnEnter, bool saveCursor)
    {
        if (enable)
        {
            if (saveCursor)
            {
                _primary.SaveCursor();
            }

            _active = _alternate;
            if (clearOnEnter)
            {
                _alternate.Reset(keepHistory: false);
            }

            _alternate.CurrentAttributes = _sgr;
        }
        else
        {
            _active = _primary;
            if (saveCursor)
            {
                _primary.RestoreCursor();
                _sgr = _primary.CurrentAttributes;
            }
        }
    }

    private void RepeatLastCharacter(int count)
    {
        if (!_hasLastPrintedRune)
        {
            return;
        }

        for (var i = 0; i < count; i++)
        {
            ((IVtDispatch)this).Print(_lastPrintedRune);
        }
    }

    private void ApplySgr(ReadOnlySpan<int> parameters)
    {
        if (parameters.Length == 0)
        {
            _sgr = CellAttributes.Default;
            Buffer.CurrentAttributes = _sgr;
            return;
        }

        for (var i = 0; i < parameters.Length; i++)
        {
            var value = Param(parameters, i, 0);
            switch (value)
            {
                case 0:
                    _sgr = CellAttributes.Default;
                    break;
                case 1:
                    _sgr.Flags = (_sgr.Flags | CellFlags.Bold) & ~CellFlags.Faint;
                    break;
                case 2:
                    _sgr.Flags |= CellFlags.Faint;
                    break;
                case 3:
                    _sgr.Flags |= CellFlags.Italic;
                    break;
                case 4:
                case 21:
                    _sgr.Flags |= CellFlags.Underline;
                    break;
                case 5:
                case 6:
                    _sgr.Flags |= CellFlags.Blink;
                    break;
                case 7:
                    _sgr.Flags |= CellFlags.Inverse;
                    break;
                case 8:
                    _sgr.Flags |= CellFlags.Invisible;
                    break;
                case 9:
                    _sgr.Flags |= CellFlags.Strikethrough;
                    break;
                case 22:
                    _sgr.Flags &= ~(CellFlags.Bold | CellFlags.Faint);
                    break;
                case 23:
                    _sgr.Flags &= ~CellFlags.Italic;
                    break;
                case 24:
                    _sgr.Flags &= ~CellFlags.Underline;
                    break;
                case 25:
                    _sgr.Flags &= ~CellFlags.Blink;
                    break;
                case 27:
                    _sgr.Flags &= ~CellFlags.Inverse;
                    break;
                case 28:
                    _sgr.Flags &= ~CellFlags.Invisible;
                    break;
                case 29:
                    _sgr.Flags &= ~CellFlags.Strikethrough;
                    break;
                case 38:
                case 48:
                    i = ApplyExtendedColor(parameters, i, value == 38);
                    break;
                case 39:
                    _sgr.Foreground = TermColor.Default;
                    break;
                case 49:
                    _sgr.Background = TermColor.Default;
                    break;
                case >= 30 and <= 37:
                    _sgr.Foreground = TermColor.FromIndex(value - 30);
                    break;
                case >= 40 and <= 47:
                    _sgr.Background = TermColor.FromIndex(value - 40);
                    break;
                case >= 90 and <= 97:
                    _sgr.Foreground = TermColor.FromIndex(value - 90 + 8);
                    break;
                case >= 100 and <= 107:
                    _sgr.Background = TermColor.FromIndex(value - 100 + 8);
                    break;
            }
        }

        Buffer.CurrentAttributes = _sgr;
    }

    private int ApplyExtendedColor(ReadOnlySpan<int> parameters, int index, bool foreground)
    {
        var mode = Param(parameters, index + 1, 0);
        if (mode == 5 && index + 2 < parameters.Length)
        {
            SetSgrColor(TermColor.FromIndex(Param(parameters, index + 2, 0)), foreground);
            return index + 2;
        }

        if (mode == 2)
        {
            var component = index + 2;
            if (component < parameters.Length && parameters[component] < 0)
            {
                component++;
            }

            if (component + 2 < parameters.Length)
            {
                SetSgrColor(
                    TermColor.FromRgb(
                        ClampByte(Param(parameters, component, 0)),
                        ClampByte(Param(parameters, component + 1, 0)),
                        ClampByte(Param(parameters, component + 2, 0))),
                    foreground);
                return component + 2;
            }
        }

        return Math.Min(index + 1, parameters.Length - 1);
    }

    private void SetSgrColor(TermColor color, bool foreground)
    {
        if (foreground)
        {
            _sgr.Foreground = color;
        }
        else
        {
            _sgr.Background = color;
        }
    }

    private void DeviceStatus(int mode, bool privateReport)
    {
        if (!privateReport && mode == 5)
        {
            Respond("\u001b[0n");
        }
        else if (mode == 6)
        {
            var row = Buffer.CursorY + 1;
            if (Buffer.OriginMode)
            {
                row -= Buffer.ScrollTop;
            }

            Respond($"\u001b[{(privateReport ? "?" : string.Empty)}{row};{Buffer.CursorX + 1}R");
        }
    }

    private void ReportMode(int mode, bool privateMode)
    {
        var state = GetModeState(mode, privateMode);
        Respond($"\u001b[{(privateMode ? "?" : string.Empty)}{mode};{state}$y");
    }

    private int GetModeState(int mode, bool privateMode)
    {
        if (!privateMode)
        {
            return mode switch
            {
                4 => InsertMode ? 1 : 2,
                20 => NewLineMode ? 1 : 2,
                _ => 0,
            };
        }

        return mode switch
        {
            1 => ApplicationCursorKeys ? 1 : 2,
            5 => ReverseVideo ? 1 : 2,
            6 => Buffer.OriginMode ? 1 : 2,
            7 => AutoWrap ? 1 : 2,
            12 => CursorBlinking ? 1 : 2,
            25 => CursorVisible ? 1 : 2,
            47 or 1047 or 1049 => AlternateBufferActive ? 1 : 2,
            1000 or 1002 or 1003 => MouseTracking ? 1 : 2,
            1004 => FocusTracking ? 1 : 2,
            1006 => SgrMouse ? 1 : 2,
            2004 => BracketedPaste ? 1 : 2,
            _ => 0,
        };
    }

    private void DispatchColorTable(ReadOnlySpan<char> data)
    {
        var parts = data.ToString().Split(';');
        for (var i = 0; i + 1 < parts.Length; i += 2)
        {
            if (!int.TryParse(parts[i], out var index) || (uint)index >= 256)
            {
                continue;
            }

            if (parts[i + 1] == "?")
            {
                RespondOsc($"4;{index};{ColorScheme.FormatXtermColor(_scheme.Resolve(index))}");
            }
            else if (ColorScheme.TryParseXtermColor(parts[i + 1], out var color))
            {
                _scheme = _scheme.WithColorTableEntry(index, color);
            }
        }
    }

    private void DispatchDynamicColors(int firstResource, ReadOnlySpan<char> data)
    {
        var colors = data.ToString().Split(';');
        for (var i = 0; i < colors.Length && firstResource + i <= 12; i++)
        {
            var resource = firstResource + i;
            if (colors[i] == "?")
            {
                RespondOsc($"{resource};{ColorScheme.FormatXtermColor(GetDynamicColor(resource))}");
            }
            else if (ColorScheme.TryParseXtermColor(colors[i], out var color))
            {
                _scheme = resource switch
                {
                    10 => _scheme.WithForeground(color),
                    11 => _scheme.WithBackground(color),
                    12 => _scheme.WithCursor(color),
                    _ => _scheme,
                };
            }
        }
    }

    private uint GetDynamicColor(int resource) => resource switch
    {
        10 => _scheme.Foreground,
        11 => _scheme.Background,
        12 => _scheme.Cursor,
        _ => 0,
    };

    private void DispatchHyperlink(ReadOnlySpan<char> data)
    {
        var separator = data.IndexOf(';');
        if (separator < 0)
        {
            return;
        }

        var uri = data[(separator + 1)..];
        Buffer.CurrentHyperlinkUri = uri.IsEmpty ? null : uri.ToString();
    }

    private void RespondOsc(string payload) => Respond($"\u001b]{payload}\u001b\\");

    private void RespondDcs(string payload) => Respond($"\u001bP{payload}\u001b\\");

    private void Respond(string text) => ResponseReady?.Invoke(this, Encoding.UTF8.GetBytes(text));

    private static int Count(ReadOnlySpan<int> parameters) => Math.Max(1, Param(parameters, 0, 1));

    private static int OneBased(ReadOnlySpan<int> parameters, int index) =>
        Math.Max(1, Param(parameters, index, 1)) - 1;

    private static byte ClampByte(int value) => (byte)Math.Clamp(value, 0, 255);

    private static int Param(ReadOnlySpan<int> parameters, int index, int defaultValue)
    {
        if ((uint)index >= (uint)parameters.Length)
        {
            return defaultValue;
        }

        return parameters[index] < 0 ? defaultValue : parameters[index];
    }
}
