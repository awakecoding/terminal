using System.Text;

namespace Microsoft.Terminal.Core;

public sealed class VtParser
{
    private enum State
    {
        Ground,
        Escape,
        EscapeIntermediate,
        CsiEntry,
        CsiParam,
        CsiIgnore,
        OscString,
        SosPmApcString,
        DcsIgnore,
    }

    private readonly IVtDispatch _dispatch;
    private readonly int[] _params = new int[32];
    private readonly StringBuilder _osc = new();
    private State _state = State.Ground;
    private int _paramCount;
    private int _currentParam = -1;
    private byte _intermediate;
    private bool _privateMarker;
    private int _utf8Needed;
    private int _utf8Acc;

    public VtParser(IVtDispatch dispatch)
    {
        _dispatch = dispatch;
    }

    public void Process(ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
        {
            ProcessByte(b);
        }
    }

    public void Reset()
    {
        _state = State.Ground;
        ClearParams();
        _osc.Clear();
        _utf8Needed = 0;
        _utf8Acc = 0;
    }

    private void ProcessByte(byte b)
    {
        if (b == 0x18 || b == 0x1A)
        {
            _state = State.Ground;
            _utf8Needed = 0;
            return;
        }

        if (b == 0x1B)
        {
            _state = State.Escape;
            ClearParams();
            _utf8Needed = 0;
            return;
        }

        if (b < 0x20 && _state is not (State.OscString or State.SosPmApcString))
        {
            if (_state == State.Ground)
            {
                _dispatch.ExecuteC0(b);
            }

            return;
        }

        switch (_state)
        {
            case State.Ground:
                Ground(b);
                break;
            case State.Escape:
                Escape(b);
                break;
            case State.EscapeIntermediate:
                EscapeIntermediate(b);
                break;
            case State.CsiEntry:
                CsiEntry(b);
                break;
            case State.CsiParam:
                CsiParam(b);
                break;
            case State.CsiIgnore:
                if (b is >= 0x40 and <= 0x7E)
                {
                    _state = State.Ground;
                }

                break;
            case State.OscString:
                Osc(b);
                break;
            case State.SosPmApcString:
            case State.DcsIgnore:
                if (b is 0x07)
                {
                    _state = State.Ground;
                }

                break;
        }
    }

    private void Ground(byte b)
    {
        if (_utf8Needed > 0)
        {
            if ((b & 0xC0) != 0x80)
            {
                _utf8Needed = 0;
                Ground(b);
                return;
            }

            _utf8Acc = (_utf8Acc << 6) | (b & 0x3F);
            _utf8Needed--;
            if (_utf8Needed == 0 && Rune.IsValid(_utf8Acc))
            {
                _dispatch.Print(new Rune(_utf8Acc));
            }

            return;
        }

        if (b < 0x80)
        {
            if (b >= 0x20)
            {
                _dispatch.Print(new Rune(b));
            }

            return;
        }

        if (b <= 0x9F)
        {
            if (b == 0x9B)
            {
                EnterCsi();
            }
            else if (b == 0x9D)
            {
                EnterOsc();
            }

            return;
        }

        if ((b & 0xE0) == 0xC0)
        {
            _utf8Needed = 1;
            _utf8Acc = b & 0x1F;
        }
        else if ((b & 0xF0) == 0xE0)
        {
            _utf8Needed = 2;
            _utf8Acc = b & 0x0F;
        }
        else if ((b & 0xF8) == 0xF0)
        {
            _utf8Needed = 3;
            _utf8Acc = b & 0x07;
        }
    }

    private void Escape(byte b)
    {
        switch (b)
        {
            case (byte)'[':
                EnterCsi();
                return;
            case (byte)']':
                EnterOsc();
                return;
            case (byte)'P' or (byte)'X' or (byte)'^' or (byte)'_':
                _state = b == (byte)'P' ? State.DcsIgnore : State.SosPmApcString;
                return;
            case >= 0x20 and <= 0x2F:
                _intermediate = b;
                _state = State.EscapeIntermediate;
                return;
            case >= 0x30 and <= 0x7E:
                _dispatch.EscDispatch((char)b, 0);
                _state = State.Ground;
                return;
        }
    }

    private void EscapeIntermediate(byte b)
    {
        if (b is >= 0x20 and <= 0x2F)
        {
            _intermediate = b;
            return;
        }

        if (b is >= 0x30 and <= 0x7E)
        {
            _dispatch.EscDispatch((char)b, _intermediate);
            _state = State.Ground;
        }
    }

    private void CsiEntry(byte b)
    {
        if (b is (byte)'?' or (byte)'>' or (byte)'=')
        {
            _privateMarker = true;
            _state = State.CsiParam;
            return;
        }

        CsiParam(b);
    }

    private void CsiParam(byte b)
    {
        if (b is >= (byte)'0' and <= (byte)'9')
        {
            if (_currentParam < 0)
            {
                _currentParam = 0;
            }

            _currentParam = Math.Min((_currentParam * 10) + (b - '0'), 65535);
            _state = State.CsiParam;
            return;
        }

        if (b is (byte)';' or (byte)':')
        {
            PushParam();
            _state = State.CsiParam;
            return;
        }

        if (b is >= 0x20 and <= 0x2F)
        {
            _intermediate = b;
            _state = State.CsiParam;
            return;
        }

        if (b is >= 0x40 and <= 0x7E)
        {
            PushParam();
            var count = _paramCount;
            _dispatch.CsiDispatch((char)b, _params.AsSpan(0, count), _intermediate, _privateMarker);
            _state = State.Ground;
            return;
        }

        if (b is >= 0x3C and <= 0x3F && _state == State.CsiParam)
        {
            _state = State.CsiIgnore;
        }
    }

    private void Osc(byte b)
    {
        if (b is 0x07)
        {
            FinishOsc();
            return;
        }

        if (b == 0x5C && _osc.Length > 0 && _osc[^1] == '\u001b')
        {
            _osc.Length--;
            FinishOsc();
            return;
        }

        if (b >= 0x20 || b == 0x09)
        {
            _osc.Append((char)b);
        }
    }

    private void FinishOsc()
    {
        var text = _osc.ToString();
        var split = text.IndexOf(';');
        var command = 0;
        ReadOnlySpan<char> data = text;
        if (split >= 0)
        {
            _ = int.TryParse(text.AsSpan(0, split), out command);
            data = text.AsSpan(split + 1);
        }

        _dispatch.OscDispatch(command, data);
        _osc.Clear();
        _state = State.Ground;
    }

    private void EnterCsi()
    {
        ClearParams();
        _state = State.CsiEntry;
    }

    private void EnterOsc()
    {
        _osc.Clear();
        _state = State.OscString;
    }

    private void PushParam()
    {
        if (_paramCount >= _params.Length)
        {
            _currentParam = -1;
            return;
        }

        _params[_paramCount++] = _currentParam < 0 ? -1 : _currentParam;
        _currentParam = -1;
    }

    private void ClearParams()
    {
        _paramCount = 0;
        _currentParam = -1;
        _intermediate = 0;
        _privateMarker = false;
    }
}
