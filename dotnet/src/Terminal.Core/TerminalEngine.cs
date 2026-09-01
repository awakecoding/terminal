using System.Text;

namespace Microsoft.Terminal.Core;

public sealed class TerminalEngine : IVtDispatch
{
    private readonly VtParser _parser;
    private TextBuffer _primary;
    private TextBuffer _alt;
    private bool _altActive;
    private CellAttributes _sgr = CellAttributes.Default;

    public TerminalEngine(int columns = 120, int rows = 30, int historySize = 9001)
    {
        _primary = new TextBuffer(columns, rows, historySize, hasHistory: true);
        _alt = new TextBuffer(columns, rows, 0, hasHistory: false);
        _parser = new VtParser(this);
        Scheme = ColorScheme.Campbell;
        CursorVisible = true;
        AutoWrap = true;
    }

    public TextBuffer Buffer => _altActive ? _alt : _primary;
    public ColorScheme Scheme { get; set; }
    public string Title { get; private set; } = "Windows Terminal";
    public bool CursorVisible { get; private set; }
    public bool ApplicationCursorKeys { get; private set; }
    public bool BracketedPaste { get; private set; }
    public bool MouseTracking { get; private set; }
    public bool SgrMouse { get; private set; }
    public bool AutoWrap { get; private set; }
    public bool InsertMode { get; private set; }
    public int Columns => Buffer.Columns;
    public int Rows => Buffer.Rows;
    public int CursorX => Buffer.CursorX;
    public int CursorY => Buffer.CursorY;

    public event EventHandler? Invalidated;
    public event EventHandler<string>? TitleChanged;
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
        _alt.Resize(columns, rows);
        Invalidated?.Invoke(this, EventArgs.Empty);
    }

    public void Reset()
    {
        _parser.Reset();
        _sgr = CellAttributes.Default;
        _altActive = false;
        CursorVisible = true;
        ApplicationCursorKeys = false;
        BracketedPaste = false;
        MouseTracking = false;
        SgrMouse = false;
        AutoWrap = true;
        InsertMode = false;
        _primary.Reset(keepHistory: false);
        _alt.Reset(keepHistory: false);
        Invalidated?.Invoke(this, EventArgs.Empty);
    }

    public string CopySelection(int x1, int y1, int x2, int y2) => Buffer.GetText(x1, y1, x2, y2);

    public string WrapPaste(string text)
    {
        if (!BracketedPaste)
        {
            return text;
        }

        return "\u001b[200~" + text.Replace("\u001b", "") + "\u001b[201~";
    }

    void IVtDispatch.Print(Rune rune)
    {
        if (!AutoWrap && Buffer.CursorX >= Buffer.Columns - 1)
        {
            Buffer.GetRow(Buffer.CursorY)[Buffer.Columns - 1] = new Cell
            {
                Rune = rune,
                Attributes = Buffer.CurrentAttributes,
            };
            return;
        }

        if (InsertMode)
        {
            Buffer.InsertCharacters(WcWidth.Width(rune));
        }

        Buffer.Print(rune);
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
                Buffer.LineFeed();
                break;
            case 0x0D:
                Buffer.CarriageReturn();
                break;
            case 0x0E:
            case 0x0F:
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
            case 'M':
                Buffer.ReverseIndex();
                break;
            case '=' or '>':
                break;
        }
    }

    void IVtDispatch.CsiDispatch(char final, ReadOnlySpan<int> parameters, byte intermediate, bool privateMarker)
    {
        if (privateMarker)
        {
            DispatchPrivate(final, parameters);
            return;
        }

        switch (final)
        {
            case 'A':
                Buffer.MoveCursor(0, -Math.Max(1, Param(parameters, 0, 1)));
                break;
            case 'B':
                Buffer.MoveCursor(0, Math.Max(1, Param(parameters, 0, 1)));
                break;
            case 'C':
                Buffer.MoveCursor(Math.Max(1, Param(parameters, 0, 1)), 0);
                break;
            case 'D':
                Buffer.MoveCursor(-Math.Max(1, Param(parameters, 0, 1)), 0);
                break;
            case 'E':
                Buffer.SetCursor(Buffer.CursorY + Math.Max(1, Param(parameters, 0, 1)), 0, relativeToOrigin: false);
                break;
            case 'F':
                Buffer.SetCursor(Buffer.CursorY - Math.Max(1, Param(parameters, 0, 1)), 0, relativeToOrigin: false);
                break;
            case 'G':
                Buffer.SetCursor(Buffer.CursorY, Math.Max(1, Param(parameters, 0, 1)) - 1, relativeToOrigin: false);
                break;
            case 'H' or 'f':
                Buffer.SetCursor(
                    Math.Max(1, Param(parameters, 0, 1)) - 1,
                    Math.Max(1, Param(parameters, 1, 1)) - 1);
                break;
            case 'J':
                Buffer.EraseInDisplay(Param(parameters, 0, 0));
                break;
            case 'K':
                Buffer.EraseInLine(Param(parameters, 0, 0));
                break;
            case 'L':
                Buffer.InsertLines(Param(parameters, 0, 1));
                break;
            case 'M':
                Buffer.DeleteLines(Param(parameters, 0, 1));
                break;
            case 'P':
                Buffer.DeleteCharacters(Param(parameters, 0, 1));
                break;
            case 'S':
                Buffer.ScrollUp(Param(parameters, 0, 1));
                break;
            case 'T':
                Buffer.ScrollDown(Param(parameters, 0, 1));
                break;
            case 'X':
                Buffer.EraseCharacters(Param(parameters, 0, 1));
                break;
            case '@':
                Buffer.InsertCharacters(Param(parameters, 0, 1));
                break;
            case 'd':
                Buffer.SetCursor(Math.Max(1, Param(parameters, 0, 1)) - 1, Buffer.CursorX);
                break;
            case 'm':
                ApplySgr(parameters);
                break;
            case 'n':
                DeviceStatus(Param(parameters, 0, 0));
                break;
            case 'c':
                Respond("\u001b[?62;c");
                break;
            case 'r':
                if (parameters.Length >= 2)
                {
                    Buffer.SetScrollRegion(
                        Math.Max(1, Param(parameters, 0, 1)) - 1,
                        Math.Max(1, Param(parameters, 1, Buffer.Rows)) - 1);
                }
                else
                {
                    Buffer.SetScrollRegion(0, Buffer.Rows - 1);
                }

                break;
            case 'h':
                if (Param(parameters, 0, 0) == 4)
                {
                    InsertMode = true;
                }

                break;
            case 'l':
                if (Param(parameters, 0, 0) == 4)
                {
                    InsertMode = false;
                }

                break;
        }
    }

    void IVtDispatch.OscDispatch(int command, ReadOnlySpan<char> data)
    {
        if (command is 0 or 2)
        {
            Title = data.ToString();
            TitleChanged?.Invoke(this, Title);
        }
    }

    private void DispatchPrivate(char final, ReadOnlySpan<int> parameters)
    {
        var enable = final == 'h';
        if (final is not ('h' or 'l'))
        {
            return;
        }

        for (var i = 0; i < parameters.Length || i == 0; i++)
        {
            switch (Param(parameters, i, 0))
            {
                case 1:
                    ApplicationCursorKeys = enable;
                    break;
                case 6:
                    Buffer.OriginMode = enable;
                    break;
                case 7:
                    AutoWrap = enable;
                    break;
                case 12:
                    break;
                case 25:
                    CursorVisible = enable;
                    break;
                case 1000:
                case 1002:
                case 1003:
                    MouseTracking = enable;
                    break;
                case 1006:
                    SgrMouse = enable;
                    break;
                case 2004:
                    BracketedPaste = enable;
                    break;
                case 47:
                case 1047:
                case 1049:
                    SetAltScreen(enable, saveCursor: Param(parameters, i, 0) == 1049);
                    break;
                case 1048:
                    if (enable)
                    {
                        Buffer.SaveCursor();
                    }
                    else
                    {
                        Buffer.RestoreCursor();
                    }

                    break;
            }

            if (parameters.Length == 0)
            {
                break;
            }
        }
    }

    private void SetAltScreen(bool enable, bool saveCursor)
    {
        if (enable)
        {
            if (saveCursor)
            {
                _primary.SaveCursor();
            }

            _altActive = true;
            _alt.Reset(keepHistory: false);
            _alt.CurrentAttributes = _sgr;
        }
        else
        {
            _altActive = false;
            if (saveCursor)
            {
                _primary.RestoreCursor();
            }
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
            var p = Param(parameters, i, 0);
            switch (p)
            {
                case 0:
                    _sgr = CellAttributes.Default;
                    break;
                case 1:
                    _sgr.Flags |= CellFlags.Bold;
                    _sgr.Flags &= ~CellFlags.Faint;
                    break;
                case 2:
                    _sgr.Flags |= CellFlags.Faint;
                    break;
                case 3:
                    _sgr.Flags |= CellFlags.Italic;
                    break;
                case 4:
                    _sgr.Flags |= CellFlags.Underline;
                    break;
                case 5:
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
                case 39:
                    _sgr.Foreground = TermColor.Default;
                    break;
                case 49:
                    _sgr.Background = TermColor.Default;
                    break;
                case >= 30 and <= 37:
                    _sgr.Foreground = TermColor.FromIndex(p - 30);
                    break;
                case >= 40 and <= 47:
                    _sgr.Background = TermColor.FromIndex(p - 40);
                    break;
                case >= 90 and <= 97:
                    _sgr.Foreground = TermColor.FromIndex(p - 90 + 8);
                    break;
                case >= 100 and <= 107:
                    _sgr.Background = TermColor.FromIndex(p - 100 + 8);
                    break;
                case 38:
                case 48:
                    i = ApplyExtendedColor(parameters, i, foreground: p == 38);
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
            var color = TermColor.FromIndex(Param(parameters, index + 2, 0));
            if (foreground)
            {
                _sgr.Foreground = color;
            }
            else
            {
                _sgr.Background = color;
            }

            return index + 2;
        }

        if (mode == 2)
        {
            // 38;2;r;g;b or 38;2;0;r;g;b (ITU)
            var baseIndex = index + 2;
            if (parameters.Length - baseIndex >= 4)
            {
                baseIndex++;
            }

            if (baseIndex + 2 < parameters.Length)
            {
                var color = TermColor.FromRgb(
                    (byte)Param(parameters, baseIndex, 0),
                    (byte)Param(parameters, baseIndex + 1, 0),
                    (byte)Param(parameters, baseIndex + 2, 0));
                if (foreground)
                {
                    _sgr.Foreground = color;
                }
                else
                {
                    _sgr.Background = color;
                }

                return baseIndex + 2;
            }
        }

        return index + 1;
    }

    private void DeviceStatus(int mode)
    {
        if (mode == 5)
        {
            Respond("\u001b[0n");
        }
        else if (mode == 6)
        {
            Respond($"\u001b[{Buffer.CursorY + 1};{Buffer.CursorX + 1}R");
        }
    }

    private void Respond(string text) => ResponseReady?.Invoke(this, Encoding.UTF8.GetBytes(text));

    private static int Param(ReadOnlySpan<int> parameters, int index, int defaultValue)
    {
        if ((uint)index >= (uint)parameters.Length)
        {
            return defaultValue;
        }

        var value = parameters[index];
        return value < 0 ? defaultValue : value;
    }
}
