using System.Text;

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
    bool ReverseVideo);

public sealed class TerminalEngine : IVtDispatch
{
    private readonly VtParser _parser;
    private readonly TextBuffer _primary;
    private readonly TextBuffer _alternate;
    private TextBuffer _active;
    private CellAttributes _sgr = CellAttributes.Default;
    private ColorScheme _scheme;
    private ColorScheme _defaultScheme;
    private Rune _lastPrintedRune;
    private bool _hasLastPrintedRune;

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
    public int Columns => Buffer.Columns;
    public int Rows => Buffer.Rows;
    public int CursorX => Buffer.CursorX;
    public int CursorY => Buffer.CursorY;

    public event EventHandler? Invalidated;
    public event EventHandler<string>? TitleChanged;
    public event EventHandler<string?>? WorkingDirectoryChanged;
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
        _primary.Reset(keepHistory: false);
        _alternate.Reset(keepHistory: false);
        Invalidated?.Invoke(this, EventArgs.Empty);
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
        ReverseVideo);

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
            case 10:
            case 11:
            case 12:
                DispatchDynamicColors(command, data);
                break;
        }
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
