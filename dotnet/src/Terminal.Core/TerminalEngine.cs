using System.Text;
using System.Globalization;
using System.Collections.ObjectModel;

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

public enum TerminalMouseTrackingMode
{
    None,
    Button,
    ButtonEvent,
    AllMotion,
}

public sealed class TerminalEngine : ITerminalEngine, IVtDispatch
{
    private readonly VtParser _parser;
    private readonly SixelDecoder _sixelDecoder = new();
    private readonly TextBuffer _primary;
    private readonly TextBuffer _alternate;
    private readonly List<TerminalImageOverlay> _images = [];
    private readonly Dictionary<int, DrcsGlyph> _drcsGlyphs = [];
    private readonly ReadOnlyDictionary<int, DrcsGlyph> _readOnlyDrcsGlyphs;
    private readonly byte[]?[] _macros = new byte[VtResourceLimits.MaximumMacros][];
    private readonly string?[] _gsets = ["B", "B", "B", "B"];
    private TextBuffer _active;
    private CellAttributes _sgr = CellAttributes.Default;
    private ColorScheme _scheme;
    private ColorScheme _defaultScheme;
    private Rune _lastPrintedRune;
    private bool _hasLastPrintedRune;
    private long _nextImageId;
    private long _retainedImageBytes;
    private bool _vt52Graphics;
    private bool _rectangularAttributeExtent;
    private string? _drcsDesignator;
    private bool _drcsIs96Character;
    private int _gl;
    private int _gr = 2;
    private int _singleShift = -1;
    private int _macroBytes;
    private int _macroDepth;
    private int _macroExpandedBytes;

    public TerminalEngine(int columns = 120, int rows = 30, int historySize = 9001)
    {
        _primary = new TextBuffer(columns, rows, historySize, hasHistory: true);
        _alternate = new TextBuffer(columns, rows, 0, hasHistory: false);
        _active = _primary;
        _parser = new VtParser(this);
        _readOnlyDrcsGlyphs = new ReadOnlyDictionary<int, DrcsGlyph>(_drcsGlyphs);
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
    public TerminalMouseTrackingMode MouseTrackingMode { get; private set; }
    public bool SgrMouse { get; private set; }
    public bool FocusTracking { get; private set; }
    public bool AutoWrap { get; private set; }
    public bool InsertMode { get; private set; }
    public bool NewLineMode { get; private set; }
    public bool ReverseVideo { get; private set; }
    public bool AnsiMode { get; private set; } = true;
    public bool ApplicationKeypad { get; private set; }
    public bool AllowClipboardWrite { get; private set; }
    public bool AllowNotifications { get; private set; }
    public int Columns => Buffer.Columns;
    public int Rows => Buffer.Rows;
    public int CursorX => Buffer.CursorX;
    public int CursorY => Buffer.CursorY;
    public int HistoryCount => Buffer.HistoryCount;
    public int ScrollOffset => Buffer.ScrollOffset;
    public IReadOnlyList<TerminalImageOverlay> Images => _images;
    public IReadOnlyDictionary<int, DrcsGlyph> DrcsGlyphs => _readOnlyDrcsGlyphs;
    public int MacroCount => _macros.Count(static macro => macro is not null);

    public event EventHandler? Invalidated;
    public event EventHandler<string>? TitleChanged;
    public event EventHandler<string?>? WorkingDirectoryChanged;
    public event EventHandler? ShellIntegrationChanged;
    public event EventHandler<string>? ClipboardWriteRequested;
    public event EventHandler<TerminalNotification>? NotificationRequested;
    public event EventHandler<TerminalImageOverlay>? ImageAdded;
    public event EventHandler? Bell;
    public event EventHandler<byte[]>? ResponseReady;

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    public void Feed(ReadOnlySpan<byte> data)
    {
        _parser.Process(data);
        Invalidated?.Invoke(this, EventArgs.Empty);
    }

    public void Feed(string text) => Feed(Encoding.UTF8.GetBytes(text));

    public void Resize(int columns, int rows, double cellWidth = 1, double cellHeight = 1)
    {
        _primary.Resize(columns, rows);
        _alternate.Resize(columns, rows);
        Invalidated?.Invoke(this, EventArgs.Empty);
    }

    public void SetScrollOffset(int offset) =>
        Buffer.ScrollOffset = Math.Clamp(offset, 0, Buffer.HistoryCount);

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
        MouseTrackingMode = TerminalMouseTrackingMode.None;
        SgrMouse = false;
        FocusTracking = false;
        AutoWrap = true;
        InsertMode = false;
        NewLineMode = false;
        ReverseVideo = false;
        AnsiMode = true;
        ApplicationKeypad = false;
        _vt52Graphics = false;
        _rectangularAttributeExtent = false;
        _drcsDesignator = null;
        _drcsIs96Character = false;
        _drcsGlyphs.Clear();
        Array.Fill(_gsets, "B");
        _gl = 0;
        _gr = 2;
        _singleShift = -1;
        Array.Clear(_macros);
        _macroBytes = 0;
        _macroDepth = 0;
        _macroExpandedBytes = 0;
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
        var isGlCharacter = rune.Value is >= 0x20 and <= 0x7F;
        var isGrCharacter = rune.Value is >= 0xA0 and <= 0xFF;
        var activeGset = _singleShift >= 0
            ? _singleShift
            : isGrCharacter
                ? _gr
                : _gl;
        _singleShift = -1;
        if (!AnsiMode && _vt52Graphics && TryMapVt52Graphics(rune, out var mapped))
        {
            rune = mapped;
        }
        else if (AnsiMode &&
                 (isGlCharacter || isGrCharacter) &&
                 _drcsDesignator is not null &&
                 string.Equals(_gsets[activeGset], _drcsDesignator, StringComparison.Ordinal))
        {
            var character = isGrCharacter ? rune.Value - 0xA0 : rune.Value - 0x20;
            if ((_drcsIs96Character || character is >= 1 and <= 94) &&
                _drcsGlyphs.TryGetValue(character, out var glyph))
            {
                rune = glyph.PrivateUseRune;
            }
        }

        if (!AutoWrap && Buffer.WrapPending)
        {
            Buffer.WrapPending = false;
        }

        var advance = Buffer.GetPrintAdvance(rune);
        if (InsertMode && advance > 0)
        {
            Buffer.InsertCharacters(advance);
        }

        Buffer.Print(rune);
        if (advance > 0)
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
            case 0x0E:
                _gl = 1;
                break;
            case 0x0F:
                _gl = 0;
                break;
            case 0x8E:
                _singleShift = 2;
                break;
            case 0x8F:
                _singleShift = 3;
                break;
        }
    }

    void IVtDispatch.EscDispatch(char final, byte intermediate)
    {
        if (TryDesignateCharset(final, intermediate))
        {
            return;
        }

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
            case 'N':
                _singleShift = 2;
                break;
            case 'O':
                _singleShift = 3;
                break;
            case 'n':
                _gl = 2;
                break;
            case 'o':
                _gl = 3;
                break;
            case '~':
                _gr = 1;
                break;
            case '}':
                _gr = 2;
                break;
            case '|':
                _gr = 3;
                break;
            case '=':
                ApplicationKeypad = true;
                break;
            case '>':
                ApplicationKeypad = false;
                break;
        }
    }

    void IVtDispatch.EscDispatch(char final, ReadOnlySpan<byte> intermediates)
    {
        if (TryDesignateCharset(final, intermediates))
        {
            return;
        }

        ((IVtDispatch)this).EscDispatch(
            final,
            intermediates.IsEmpty ? (byte)0 : intermediates[^1]);
    }

    void IVtDispatch.Vt52Dispatch(char final, byte row, byte column)
    {
        switch (final)
        {
            case '<':
                AnsiMode = true;
                _vt52Graphics = false;
                break;
            case 'A':
                Buffer.MoveCursor(0, -1);
                break;
            case 'B':
                Buffer.MoveCursor(0, 1);
                break;
            case 'C':
                Buffer.MoveCursor(1, 0);
                break;
            case 'D':
                Buffer.MoveCursor(-1, 0);
                break;
            case 'F':
                _vt52Graphics = true;
                break;
            case 'G':
                _vt52Graphics = false;
                break;
            case 'H':
                Buffer.SetCursor(0, 0, relativeToOrigin: false);
                break;
            case 'I':
                Buffer.ReverseIndex();
                break;
            case 'J':
                Buffer.EraseInDisplay(0);
                break;
            case 'K':
                Buffer.EraseInLine(0);
                break;
            case 'Y':
                Buffer.SetCursor(Math.Max(0, row - 32), Math.Max(0, column - 32), relativeToOrigin: false);
                break;
            case 'Z':
                Respond("\u001b/Z");
                break;
            case '=':
                ApplicationKeypad = true;
                break;
            case '>':
                ApplicationKeypad = false;
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
        else if (final == '{' && intermediates.IsEmpty)
        {
            DispatchDrcs(parameters, data);
        }
        else if (final == 'z' && intermediates.SequenceEqual([(byte)'!']))
        {
            DefineMacro(parameters, data);
        }
        else if (final == 'p' && intermediates.SequenceEqual([(byte)'$']))
        {
            RestoreTerminalState(parameters, data);
        }
        else if (final == 't' && intermediates.SequenceEqual([(byte)'$']))
        {
            RestorePresentationState(parameters, data);
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
            "*x" => $"1$r{(_rectangularAttributeExtent ? 2 : 1)}*x",
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

    private void DispatchDrcs(ReadOnlySpan<int> parameters, ReadOnlySpan<byte> data)
    {
        if (!DrcsDecoder.TryDecode(
                parameters,
                data,
                out var designator,
                out var eraseControl,
                out var is96Character,
                out var glyphs))
        {
            return;
        }

        if (eraseControl is 0 or 2)
        {
            _drcsGlyphs.Clear();
        }

        foreach (var (character, glyph) in glyphs)
        {
            _drcsGlyphs[character] = glyph;
        }

        _drcsDesignator = designator;
        _drcsIs96Character = is96Character;
    }

    private void DefineMacro(ReadOnlySpan<int> parameters, ReadOnlySpan<byte> data)
    {
        if (_macroDepth != 0)
        {
            return;
        }

        var macroId = Param(parameters, 0, 0);
        var deleteControl = Param(parameters, 1, 0);
        var encoding = Param(parameters, 2, 0);
        if ((uint)macroId >= VtResourceLimits.MaximumMacros || deleteControl is < 0 or > 1)
        {
            return;
        }

        if (deleteControl == 1)
        {
            Array.Clear(_macros);
            _macroBytes = 0;
        }
        else
        {
            DeleteMacro(macroId);
        }

        if (!MacroDecoder.TryDecode(data, encoding, out var macro) ||
            _macroBytes + macro.Length > VtResourceLimits.MaximumMacroBytes)
        {
            return;
        }

        _macros[macroId] = macro;
        _macroBytes += macro.Length;
    }

    private void DeleteMacro(int macroId)
    {
        if (_macros[macroId] is { } existing)
        {
            _macroBytes -= existing.Length;
            _macros[macroId] = null;
        }
    }

    private void InvokeMacro(int macroId)
    {
        if ((uint)macroId >= VtResourceLimits.MaximumMacros ||
            _macros[macroId] is not { } macro ||
            _macroDepth >= VtResourceLimits.MaximumMacroRecursionDepth)
        {
            return;
        }

        var topLevel = _macroDepth == 0;
        if (topLevel)
        {
            _macroExpandedBytes = 0;
        }

        if (macro.Length > VtResourceLimits.MaximumMacroBytes - _macroExpandedBytes)
        {
            return;
        }

        _macroExpandedBytes += macro.Length;
        _macroDepth++;
        try
        {
            _parser.Process(macro);
        }
        finally
        {
            _macroDepth--;
            if (topLevel)
            {
                _macroExpandedBytes = 0;
            }
        }
    }

    private ushort MacroChecksum()
    {
        ushort checksum = 0;
        foreach (var macro in _macros)
        {
            if (macro is null)
            {
                continue;
            }

            foreach (var value in macro)
            {
                checksum = unchecked((ushort)(checksum - value));
            }
        }

        return checksum;
    }

    private bool TryDesignateCharset(char final, byte intermediate)
    {
        var gset = intermediate switch
        {
            (byte)'(' => 0,
            (byte)')' or (byte)'-' => 1,
            (byte)'*' or (byte)'.' => 2,
            (byte)'+' or (byte)'/' => 3,
            _ => -1,
        };
        if (gset < 0)
        {
            return false;
        }

        _gsets[gset] = final.ToString();
        return true;
    }

    private bool TryDesignateCharset(char final, ReadOnlySpan<byte> intermediates)
    {
        if (intermediates.IsEmpty)
        {
            return false;
        }

        var gset = intermediates[0] switch
        {
            (byte)'(' => 0,
            (byte)')' or (byte)'-' => 1,
            (byte)'*' or (byte)'.' => 2,
            (byte)'+' or (byte)'/' => 3,
            _ => -1,
        };
        if (gset < 0)
        {
            return false;
        }

        _gsets[gset] = intermediates.Length == 1
            ? final.ToString()
            : string.Concat((char)intermediates[1], final);
        return true;
    }

    private void ReportTerminalState(ReadOnlySpan<int> parameters)
    {
        if (Param(parameters, 0, 1) != 2)
        {
            return;
        }

        var model = Param(parameters, 1, 1);
        if (model is not (1 or 2))
        {
            return;
        }

        var response = new StringBuilder("2$s");
        for (var index = 0; index < 256; index++)
        {
            if (index > 0)
            {
                response.Append('/');
            }

            var color = _scheme.Resolve(index);
            var red = (byte)(color >> 16);
            var green = (byte)(color >> 8);
            var blue = (byte)color;
            response.Append(index).Append(';').Append(model).Append(';');
            if (model == 2)
            {
                response
                    .Append(ToPercent(red)).Append(';')
                    .Append(ToPercent(green)).Append(';')
                    .Append(ToPercent(blue));
            }
            else
            {
                var (hue, lightness, saturation) = RgbToHls(red, green, blue);
                response.Append(hue).Append(';').Append(lightness).Append(';').Append(saturation);
            }
        }

        RespondDcs(response.ToString());
    }

    private void RestoreTerminalState(ReadOnlySpan<int> parameters, ReadOnlySpan<byte> data)
    {
        if (Param(parameters, 0, 1) != 2 || data.Length > 32 * 1024)
        {
            return;
        }

        var entries = Encoding.ASCII.GetString(data).Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var entry in entries.Take(256))
        {
            var fields = entry.Split(';');
            if (fields.Length != 5 ||
                !int.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var index) ||
                !int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var model) ||
                !int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var x) ||
                !int.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out var y) ||
                !int.TryParse(fields[4], NumberStyles.None, CultureInfo.InvariantCulture, out var z) ||
                (uint)index >= 256)
            {
                continue;
            }

            uint color;
            if (model == 2 && x is >= 0 and <= 100 && y is >= 0 and <= 100 && z is >= 0 and <= 100)
            {
                color = PackRgb(FromPercent(x), FromPercent(y), FromPercent(z));
            }
            else if (model == 1 && x is >= 0 and <= 360 && y is >= 0 and <= 100 && z is >= 0 and <= 100)
            {
                var (red, green, blue) = HlsToRgb(x, y, z);
                color = PackRgb(red, green, blue);
            }
            else
            {
                continue;
            }

            _scheme = _scheme.WithColorTableEntry(index, color);
        }
    }

    private void ReportPresentationState(ReadOnlySpan<int> parameters)
    {
        switch (Param(parameters, 0, 1))
        {
            case 1:
                var rendition = '@';
                if ((_sgr.Flags & CellFlags.Bold) != 0)
                {
                    rendition += (char)1;
                }

                if ((_sgr.Flags & CellFlags.Underline) != 0)
                {
                    rendition += (char)2;
                }

                if ((_sgr.Flags & CellFlags.Blink) != 0)
                {
                    rendition += (char)4;
                }

                if ((_sgr.Flags & CellFlags.Inverse) != 0)
                {
                    rendition += (char)8;
                }

                if ((_sgr.Flags & CellFlags.Invisible) != 0)
                {
                    rendition += (char)16;
                }

                var flags = '@';
                if (Buffer.OriginMode)
                {
                    flags += (char)1;
                }

                if (_singleShift == 2)
                {
                    flags += (char)2;
                }
                else if (_singleShift == 3)
                {
                    flags += (char)4;
                }

                if (Buffer.WrapPending)
                {
                    flags += (char)8;
                }

                RespondDcs(
                    $"1$u{Buffer.CursorY + 1};{Buffer.CursorX + 1};1;{rendition};" +
                    $"{(Buffer.CurrentProtection ? 'A' : '@')};{flags};{_gl};{_gr};@;" +
                    string.Concat(_gsets.Select(static value => value ?? "B")));
                break;
            case 2:
                RespondDcs("2$u" + string.Join('/', Buffer.GetTabStops().Select(static column => column + 1)));
                break;
        }
    }

    private void RestorePresentationState(ReadOnlySpan<int> parameters, ReadOnlySpan<byte> data)
    {
        var format = Param(parameters, 0, 1);
        if (format == 2)
        {
            var tabs = ParseTabStops(data, Buffer.Columns);
            Buffer.ReplaceTabStops(tabs);
            return;
        }

        if (format != 1)
        {
            return;
        }

        var text = Encoding.ASCII.GetString(data);
        var fields = text.Split(';', 10);
        if (fields.Length != 10 ||
            !int.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var row) ||
            !int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var column) ||
            !int.TryParse(fields[6], NumberStyles.None, CultureInfo.InvariantCulture, out var gl) ||
            !int.TryParse(fields[7], NumberStyles.None, CultureInfo.InvariantCulture, out var gr) ||
            fields[3].Length != 1 ||
            fields[4].Length != 1 ||
            fields[5].Length != 1 ||
            !TryParseCharsetDesignators(fields[9], out var gsets))
        {
            return;
        }

        var rendition = fields[3][0] - '@';
        _sgr.Flags &= ~(CellFlags.Bold | CellFlags.Underline | CellFlags.Blink | CellFlags.Inverse | CellFlags.Invisible);
        if ((rendition & 1) != 0) _sgr.Flags |= CellFlags.Bold;
        if ((rendition & 2) != 0) _sgr.Flags |= CellFlags.Underline;
        if ((rendition & 4) != 0) _sgr.Flags |= CellFlags.Blink;
        if ((rendition & 8) != 0) _sgr.Flags |= CellFlags.Inverse;
        if ((rendition & 16) != 0) _sgr.Flags |= CellFlags.Invisible;
        Buffer.CurrentAttributes = _sgr;
        Buffer.CurrentProtection = fields[4][0] == 'A';
        var flags = fields[5][0] - '@';
        Buffer.OriginMode = (flags & 1) != 0;
        _singleShift = (flags & 2) != 0 ? 2 : (flags & 4) != 0 ? 3 : -1;
        Buffer.SetCursor(Math.Max(0, row - 1), Math.Max(0, column - 1), relativeToOrigin: false);
        Buffer.WrapPending = (flags & 8) != 0 && Buffer.CursorX == Buffer.Columns - 1;
        _gl = Math.Clamp(gl, 0, 3);
        _gr = Math.Clamp(gr, 0, 3);
        for (var index = 0; index < 4; index++)
        {
            _gsets[index] = gsets[index];
        }
    }

    private static int[] ParseTabStops(ReadOnlySpan<byte> data, int maximumCount)
    {
        var tabs = new List<int>(Math.Min(maximumCount, 256));
        var value = 0;
        var hasDigits = false;
        var valid = true;

        for (var offset = 0; offset <= data.Length && tabs.Count < maximumCount; offset++)
        {
            var current = offset < data.Length ? data[offset] : (byte)'/';
            if (current is >= (byte)'0' and <= (byte)'9')
            {
                hasDigits = true;
                var digit = current - (byte)'0';
                value = value > (int.MaxValue - digit) / 10
                    ? int.MaxValue
                    : (value * 10) + digit;
            }
            else if (current == (byte)'/')
            {
                if (valid && hasDigits && value > 0)
                {
                    tabs.Add(value - 1);
                }

                value = 0;
                hasDigits = false;
                valid = true;
            }
            else
            {
                valid = false;
            }
        }

        return tabs.ToArray();
    }

    private static bool TryParseCharsetDesignators(string value, out string[] designators)
    {
        designators = new string[4];
        var offset = 0;
        for (var index = 0; index < designators.Length; index++)
        {
            if (offset >= value.Length)
            {
                return false;
            }

            var length = value[offset] is >= ' ' and <= '/' ? 2 : 1;
            if (offset + length > value.Length)
            {
                return false;
            }

            designators[index] = value.Substring(offset, length);
            offset += length;
        }

        return offset == value.Length;
    }

    private static int ToPercent(byte component) => (component * 100 + 127) / 255;

    private static byte FromPercent(int component) => (byte)((component * 255 + 50) / 100);

    private static uint PackRgb(byte red, byte green, byte blue) =>
        0xFF000000u | ((uint)red << 16) | ((uint)green << 8) | blue;

    private static (int Hue, int Lightness, int Saturation) RgbToHls(byte red, byte green, byte blue)
    {
        var r = red / 255d;
        var g = green / 255d;
        var b = blue / 255d;
        var maximum = Math.Max(r, Math.Max(g, b));
        var minimum = Math.Min(r, Math.Min(g, b));
        var lightness = (maximum + minimum) / 2;
        if (maximum == minimum)
        {
            return (0, (int)Math.Round(lightness * 100), 0);
        }

        var delta = maximum - minimum;
        var saturation = lightness > 0.5
            ? delta / (2 - maximum - minimum)
            : delta / (maximum + minimum);
        var hue = maximum == r
            ? (g - b) / delta + (g < b ? 6 : 0)
            : maximum == g
                ? (b - r) / delta + 2
                : (r - g) / delta + 4;
        return (
            (int)Math.Round(hue * 60) % 360,
            (int)Math.Round(lightness * 100),
            (int)Math.Round(saturation * 100));
    }

    private static (byte Red, byte Green, byte Blue) HlsToRgb(int hue, int lightness, int saturation)
    {
        var h = (hue % 360) / 360d;
        var l = lightness / 100d;
        var s = saturation / 100d;
        if (s == 0)
        {
            var gray = (byte)Math.Round(l * 255);
            return (gray, gray, gray);
        }

        var q = l < 0.5 ? l * (1 + s) : l + s - (l * s);
        var p = (2 * l) - q;
        static double Hue(double p, double q, double t)
        {
            if (t < 0) t++;
            if (t > 1) t--;
            if (t < 1d / 6) return p + ((q - p) * 6 * t);
            if (t < 1d / 2) return q;
            if (t < 2d / 3) return p + ((q - p) * (2d / 3 - t) * 6);
            return p;
        }

        return (
            (byte)Math.Round(Hue(p, q, h + (1d / 3)) * 255),
            (byte)Math.Round(Hue(p, q, h) * 255),
            (byte)Math.Round(Hue(p, q, h - (1d / 3)) * 255));
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
        if (privateMarker == 0 && intermediate == (byte)'*' && final == 'z')
        {
            InvokeMacro(Param(parameters, 0, 0));
            return;
        }

        if (privateMarker == 0 && intermediate == (byte)'$' && final == 'u')
        {
            ReportTerminalState(parameters);
            return;
        }

        if (privateMarker == 0 && intermediate == (byte)'$' && final == 'w')
        {
            ReportPresentationState(parameters);
            return;
        }

        if (privateMarker == (byte)'?' && intermediate == 0 && final is 'J' or 'K')
        {
            if (final == 'J')
            {
                Buffer.SelectiveEraseInDisplay(Param(parameters, 0, 0));
            }
            else
            {
                Buffer.SelectiveEraseInLine(Param(parameters, 0, 0));
            }

            return;
        }

        if (privateMarker == 0 && intermediate == (byte)'$' && DispatchRectangularOperation(final, parameters))
        {
            return;
        }

        if (privateMarker == 0 && intermediate == (byte)'*')
        {
            if (final == 'x')
            {
                _rectangularAttributeExtent = Param(parameters, 0, 0) == 2;
                return;
            }

            if (final == 'y')
            {
                ReportRectangleChecksum(parameters);
                return;
            }
        }

        if (privateMarker == 0 && intermediate == (byte)'"' && final == 'q')
        {
            Buffer.CurrentProtection = Param(parameters, 0, 0) == 1;
            return;
        }

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
            var mode = Param(parameters, 0, 0);
            if (mode == 62)
            {
                Respond($"\u001b[{(VtResourceLimits.MaximumMacroBytes - _macroBytes) / 16}*{{");
            }
            else if (mode == 63)
            {
                RespondDcs($"{Math.Clamp(Param(parameters, 1, 0), 0, 65535)}!~{MacroChecksum():X4}");
            }
            else
            {
                DeviceStatus(mode, privateReport: true);
            }

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

    private bool DispatchRectangularOperation(char final, ReadOnlySpan<int> parameters)
    {
        var top = RectangleStart(parameters, 0, row: true);
        var left = RectangleStart(parameters, 1, row: false);
        var bottom = RectangleEnd(parameters, 2, row: true);
        var right = RectangleEnd(parameters, 3, row: false);
        switch (final)
        {
            case 'r':
                ReadOnlySpan<int> changeAttributes = parameters.Length > 4 ? parameters[4..] : [];
                Buffer.ChangeAttributesRectangle(
                    top,
                    left,
                    bottom,
                    right,
                    changeAttributes,
                    reverse: false,
                    rectangular: _rectangularAttributeExtent);
                return true;
            case 't':
                ReadOnlySpan<int> reverseAttributes = parameters.Length > 4 ? parameters[4..] : [];
                Buffer.ChangeAttributesRectangle(
                    top,
                    left,
                    bottom,
                    right,
                    reverseAttributes,
                    reverse: true,
                    rectangular: _rectangularAttributeExtent);
                return true;
            case 'v':
                if (Param(parameters, 4, 1) is 0 or 1 && Param(parameters, 7, 1) is 0 or 1)
                {
                    Buffer.CopyRectangle(
                        top,
                        left,
                        bottom,
                        right,
                        OneBased(parameters, 5),
                        OneBased(parameters, 6));
                }

                return true;
            case 'x':
                var character = Param(parameters, 0, 32);
                if (character is >= 32 and <= 126 or >= 160 and <= 255)
                {
                    Buffer.FillRectangle(
                        new Rune(character),
                        RectangleStart(parameters, 1, row: true),
                        RectangleStart(parameters, 2, row: false),
                        RectangleEnd(parameters, 3, row: true),
                        RectangleEnd(parameters, 4, row: false));
                }

                return true;
            case 'z':
                Buffer.EraseRectangle(top, left, bottom, right, selective: false);
                return true;
            case '{':
                Buffer.EraseRectangle(top, left, bottom, right, selective: true);
                return true;
            default:
                return false;
        }
    }

    private void ReportRectangleChecksum(ReadOnlySpan<int> parameters)
    {
        var identifier = Math.Clamp(Param(parameters, 0, 0), 0, 65535);
        var page = Param(parameters, 1, 1);
        if (page is not (0 or 1))
        {
            return;
        }

        var checksum = page == 0
            ? (ushort)0
            : Buffer.ChecksumRectangle(
                OneBased(parameters, 2),
                OneBased(parameters, 3),
                OneBased(parameters, 4),
                OneBased(parameters, 5));
        RespondDcs($"{identifier}!~{checksum:X4}");
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
                case 2:
                    if (!enable)
                    {
                        AnsiMode = false;
                        _vt52Graphics = false;
                    }

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
                    var requestedMouseMode = mode switch
                    {
                        1002 => TerminalMouseTrackingMode.ButtonEvent,
                        1003 => TerminalMouseTrackingMode.AllMotion,
                        _ => TerminalMouseTrackingMode.Button,
                    };
                    if (enable)
                    {
                        MouseTrackingMode = requestedMouseMode;
                    }
                    else if (MouseTrackingMode == requestedMouseMode)
                    {
                        MouseTrackingMode = TerminalMouseTrackingMode.None;
                    }

                    MouseTracking = MouseTrackingMode != TerminalMouseTrackingMode.None;
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
                RemoveImages(alternateBuffer: true);
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

    private void RemoveImages(bool alternateBuffer)
    {
        for (var index = _images.Count - 1; index >= 0; index--)
        {
            if (_images[index].AlternateBuffer != alternateBuffer)
            {
                continue;
            }

            var image = _images[index];
            _retainedImageBytes -=
                image.Sixel?.EstimatedByteSize ?? image.InlineImage?.EstimatedByteSize ?? 0;
            _images.RemoveAt(index);
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
            1000 => MouseTrackingMode == TerminalMouseTrackingMode.Button ? 1 : 2,
            1002 => MouseTrackingMode == TerminalMouseTrackingMode.ButtonEvent ? 1 : 2,
            1003 => MouseTrackingMode == TerminalMouseTrackingMode.AllMotion ? 1 : 2,
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

    private int RectangleStart(ReadOnlySpan<int> parameters, int index, bool row)
    {
        var value = Math.Max(1, Param(parameters, index, 1)) - 1;
        if (row && Buffer.OriginMode)
        {
            value += Buffer.ScrollTop;
        }

        var maximum = row
            ? (Buffer.OriginMode ? Buffer.ScrollBottom : Buffer.Rows - 1)
            : Buffer.Columns - 1;
        return Math.Clamp(value, 0, maximum);
    }

    private int RectangleEnd(ReadOnlySpan<int> parameters, int index, bool row)
    {
        var offset = row && Buffer.OriginMode ? Buffer.ScrollTop : 0;
        var maximum = row
            ? (Buffer.OriginMode ? Buffer.ScrollBottom : Buffer.Rows - 1)
            : Buffer.Columns - 1;
        var raw = Param(parameters, index, 0);
        return raw <= 0 ? maximum : Math.Clamp(offset + raw - 1, 0, maximum);
    }

    private static byte ClampByte(int value) => (byte)Math.Clamp(value, 0, 255);

    private static int Param(ReadOnlySpan<int> parameters, int index, int defaultValue)
    {
        if ((uint)index >= (uint)parameters.Length)
        {
            return defaultValue;
        }

        return parameters[index] < 0 ? defaultValue : parameters[index];
    }

    private static bool TryMapVt52Graphics(Rune rune, out Rune mapped)
    {
        var value = rune.Value switch
        {
            0x5F => 0x20,
            0x60 => 0x25C6,
            0x61 => 0x2592,
            0x62 => 0x2409,
            0x63 => 0x240C,
            0x64 => 0x240D,
            0x65 => 0x240A,
            0x66 => 0x00B0,
            0x67 => 0x00B1,
            0x68 => 0x2424,
            0x69 => 0x240B,
            0x6A => 0x2518,
            0x6B => 0x2510,
            0x6C => 0x250C,
            0x6D => 0x2514,
            0x6E => 0x253C,
            0x6F or 0x70 or 0x71 or 0x72 or 0x73 => 0x2500,
            0x74 => 0x251C,
            0x75 => 0x2524,
            0x76 => 0x2534,
            0x77 => 0x252C,
            0x78 => 0x2502,
            0x79 => 0x2264,
            0x7A => 0x2265,
            0x7B => 0x03C0,
            0x7C => 0x2260,
            0x7D => 0x00A3,
            0x7E => 0x00B7,
            _ => -1,
        };
        mapped = value >= 0 ? new Rune(value) : rune;
        return value >= 0;
    }
}
