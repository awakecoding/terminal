using System.Text;

namespace Microsoft.Terminal.Core;

public sealed class VtParser
{
    private const int MaxParameters = 32;
    private const int MaxStringBytes = 1024 * 1024;
    private const int MaxDcsPayloadBytes = TerminalImageLimits.MaximumDcsPayloadBytes;
    private const int MaxDcsIntermediates = 2;

    private enum State
    {
        Ground,
        Escape,
        EscapeIntermediate,
        CsiEntry,
        CsiParam,
        CsiIntermediate,
        CsiIgnore,
        DcsEntry,
        DcsParam,
        DcsIntermediate,
        DcsPassthrough,
        DcsEscape,
        DcsIgnore,
        OscString,
        OscEscape,
        StringIgnore,
        StringEscape,
    }

    private readonly IVtDispatch _dispatch;
    private readonly int[] _parameters = new int[MaxParameters];
    private readonly List<byte> _osc = [];
    private readonly List<byte> _dcs = [];
    private readonly byte[] _dcsIntermediates = new byte[MaxDcsIntermediates];
    private State _state;
    private int _parameterCount;
    private int _currentParameter = -1;
    private byte _intermediate;
    private byte _privateMarker;
    private byte _dcsFinal;
    private int _dcsIntermediateCount;
    private bool _dcsEscapeCanDispatch;
    private int _utf8Needed;
    private int _utf8Accumulator;
    private int _utf8Minimum;

    public VtParser(IVtDispatch dispatch)
    {
        _dispatch = dispatch;
    }

    public void Process(ReadOnlySpan<byte> data)
    {
        foreach (var value in data)
        {
            ProcessByte(value);
        }
    }

    public void Reset()
    {
        _state = State.Ground;
        ClearSequence();
        _osc.Clear();
        ClearDcs();
        ResetUtf8();
    }

    private void ProcessByte(byte value)
    {
        if (_state is State.DcsEntry or State.DcsParam or State.DcsIntermediate or
            State.DcsPassthrough or State.DcsEscape or State.DcsIgnore)
        {
            ProcessDcs(value);
            return;
        }

        if (_state == State.OscString)
        {
            ProcessOsc(value);
            return;
        }

        if (_state == State.OscEscape)
        {
            if (value == (byte)'\\')
            {
                FinishOsc();
            }
            else
            {
                AppendOsc(0x1B);
                _state = State.OscString;
                ProcessOsc(value);
            }

            return;
        }

        if (_state == State.StringIgnore)
        {
            if (value == 0x1B)
            {
                _state = State.StringEscape;
            }
            else if (value is 0x07 or 0x9C)
            {
                _state = State.Ground;
            }

            return;
        }

        if (_state == State.StringEscape)
        {
            _state = value == (byte)'\\' ? State.Ground : State.StringIgnore;
            return;
        }

        if (value is 0x18 or 0x1A)
        {
            _state = State.Ground;
            ClearSequence();
            ResetUtf8();
            return;
        }

        if (value == 0x1B)
        {
            EmitIncompleteUtf8();
            _state = State.Escape;
            ClearSequence();
            return;
        }

        if (value < 0x20)
        {
            EmitIncompleteUtf8();
            _dispatch.ExecuteC0(value);
            return;
        }

        if (value == 0x7F)
        {
            EmitIncompleteUtf8();
            return;
        }

        switch (_state)
        {
            case State.Ground:
                ProcessGround(value);
                break;
            case State.Escape:
                ProcessEscape(value);
                break;
            case State.EscapeIntermediate:
                ProcessEscapeIntermediate(value);
                break;
            case State.CsiEntry:
                ProcessCsiEntry(value);
                break;
            case State.CsiParam:
                ProcessCsiParam(value);
                break;
            case State.CsiIntermediate:
                ProcessCsiIntermediate(value);
                break;
            case State.CsiIgnore:
                if (IsFinal(value))
                {
                    _state = State.Ground;
                }

                break;
        }
    }

    private void ProcessGround(byte value)
    {
        if (_utf8Needed > 0)
        {
            if ((value & 0xC0) != 0x80)
            {
                EmitReplacement();
                ResetUtf8();
                ProcessGround(value);
                return;
            }

            _utf8Accumulator = (_utf8Accumulator << 6) | (value & 0x3F);
            _utf8Needed--;
            if (_utf8Needed == 0)
            {
                var scalar = _utf8Accumulator;
                if (scalar >= _utf8Minimum && Rune.IsValid(scalar))
                {
                    _dispatch.Print(new Rune(scalar));
                }
                else
                {
                    EmitReplacement();
                }

                ResetUtf8();
            }

            return;
        }

        if (value < 0x80)
        {
            _dispatch.Print(new Rune(value));
            return;
        }

        if (value is 0x9B)
        {
            EnterCsi();
            return;
        }

        if (value is 0x9D)
        {
            EnterOsc();
            return;
        }

        if (value == 0x90)
        {
            EnterDcs();
            return;
        }

        if (value is 0x98 or 0x9E or 0x9F)
        {
            _state = State.StringIgnore;
            return;
        }

        if (value is >= 0xC2 and <= 0xDF)
        {
            StartUtf8(value & 0x1F, 1, 0x80);
        }
        else if (value is >= 0xE0 and <= 0xEF)
        {
            StartUtf8(value & 0x0F, 2, 0x800);
        }
        else if (value is >= 0xF0 and <= 0xF4)
        {
            StartUtf8(value & 0x07, 3, 0x10000);
        }
        else
        {
            EmitReplacement();
        }
    }

    private void ProcessEscape(byte value)
    {
        switch (value)
        {
            case (byte)'[':
                EnterCsi();
                break;
            case (byte)']':
                EnterOsc();
                break;
            case (byte)'P':
                EnterDcs();
                break;
            case (byte)'X':
            case (byte)'^':
            case (byte)'_':
                _state = State.StringIgnore;
                break;
            case >= 0x20 and <= 0x2F:
                _intermediate = value;
                _state = State.EscapeIntermediate;
                break;
            case >= 0x30 and <= 0x7E:
                _dispatch.EscDispatch((char)value, 0);
                _state = State.Ground;
                break;
            default:
                _state = State.Ground;
                break;
        }
    }

    private void ProcessEscapeIntermediate(byte value)
    {
        if (value is >= 0x20 and <= 0x2F)
        {
            _intermediate = value;
        }
        else if (IsFinal(value))
        {
            _dispatch.EscDispatch((char)value, _intermediate);
            _state = State.Ground;
        }
        else
        {
            _state = State.Ground;
        }
    }

    private void ProcessCsiEntry(byte value)
    {
        if (value is >= 0x3C and <= 0x3F)
        {
            _privateMarker = value;
            _state = State.CsiParam;
        }
        else
        {
            ProcessCsiParam(value);
        }
    }

    private void ProcessCsiParam(byte value)
    {
        if (value is >= (byte)'0' and <= (byte)'9')
        {
            _currentParameter = _currentParameter < 0 ? 0 : _currentParameter;
            _currentParameter = Math.Min((_currentParameter * 10) + (value - '0'), 65535);
            _state = State.CsiParam;
        }
        else if (value is (byte)';' or (byte)':')
        {
            PushParameter();
        }
        else if (value is >= 0x20 and <= 0x2F)
        {
            PushParameterIfNeeded();
            _intermediate = value;
            _state = State.CsiIntermediate;
        }
        else if (IsFinal(value))
        {
            DispatchCsi(value);
        }
        else if (value is >= 0x3C and <= 0x3F)
        {
            _state = State.CsiIgnore;
        }
    }

    private void ProcessCsiIntermediate(byte value)
    {
        if (value is >= 0x20 and <= 0x2F)
        {
            _intermediate = value;
        }
        else if (IsFinal(value))
        {
            DispatchCsi(value);
        }
        else
        {
            _state = State.CsiIgnore;
        }
    }

    private void DispatchCsi(byte final)
    {
        PushParameterIfNeeded();
        _dispatch.CsiDispatch(
            (char)final,
            _parameters.AsSpan(0, _parameterCount),
            _intermediate,
            _privateMarker);
        _state = State.Ground;
        ClearSequence();
    }

    private void ProcessOsc(byte value)
    {
        if (value is 0x18 or 0x1A)
        {
            _osc.Clear();
            _state = State.Ground;
            _dispatch.ExecuteC0(value);
        }
        else if (value is 0x07 or 0x9C)
        {
            FinishOsc();
        }
        else if (value == 0x1B)
        {
            _state = State.OscEscape;
        }
        else if (value >= 0x20 || value == 0x09)
        {
            AppendOsc(value);
        }
    }

    private void AppendOsc(byte value)
    {
        if (_osc.Count < MaxStringBytes)
        {
            _osc.Add(value);
        }
        else
        {
            _osc.Clear();
            _state = State.StringIgnore;
        }
    }

    private void FinishOsc()
    {
        var text = Encoding.UTF8.GetString(_osc.ToArray());
        var separator = text.IndexOf(';');
        var command = 0;
        ReadOnlySpan<char> data = text;
        if (separator >= 0)
        {
            if (!int.TryParse(text.AsSpan(0, separator), out command))
            {
                command = -1;
            }

            data = text.AsSpan(separator + 1);
        }

        if (command >= 0)
        {
            _dispatch.OscDispatch(command, data);
        }

        _osc.Clear();
        _state = State.Ground;
    }

    private void EnterCsi()
    {
        ClearSequence();
        _state = State.CsiEntry;
    }

    private void EnterOsc()
    {
        _osc.Clear();
        _state = State.OscString;
    }

    private void EnterDcs()
    {
        ClearSequence();
        ClearDcs();
        _state = State.DcsEntry;
    }

    private void ProcessDcs(byte value)
    {
        if (value is 0x18 or 0x1A)
        {
            CancelDcs();
            _dispatch.ExecuteC0(value);
            return;
        }

        if (value == 0x9C)
        {
            TerminateDcs(_state == State.DcsPassthrough);
            return;
        }

        if (_state == State.DcsEscape)
        {
            if (value == (byte)'\\')
            {
                TerminateDcs(_dcsEscapeCanDispatch);
            }
            else
            {
                CancelDcs();
                _state = State.Escape;
                ProcessByte(value);
            }

            return;
        }

        if (value == 0x1B)
        {
            _dcsEscapeCanDispatch = _state == State.DcsPassthrough;
            _state = State.DcsEscape;
            return;
        }

        if (value < 0x20)
        {
            _dispatch.ExecuteC0(value);
            return;
        }

        if (value == 0x7F)
        {
            return;
        }

        switch (_state)
        {
            case State.DcsEntry:
                ProcessDcsEntry(value);
                break;
            case State.DcsParam:
                ProcessDcsParam(value);
                break;
            case State.DcsIntermediate:
                ProcessDcsIntermediate(value);
                break;
            case State.DcsPassthrough:
                AppendDcs(value);
                break;
            case State.DcsIgnore:
                break;
        }
    }

    private void ProcessDcsEntry(byte value)
    {
        if (value is >= 0x3C and <= 0x3F)
        {
            _privateMarker = value;
            _state = State.DcsParam;
        }
        else if (value is >= (byte)'0' and <= (byte)'9' or (byte)';')
        {
            _state = State.DcsParam;
            ProcessDcsParam(value);
        }
        else if (value == (byte)':')
        {
            _state = State.DcsIgnore;
        }
        else if (value is >= 0x20 and <= 0x2F)
        {
            AppendDcsIntermediate(value);
        }
        else if (IsFinal(value))
        {
            StartDcsPassthrough(value);
        }
        else
        {
            _state = State.DcsIgnore;
        }
    }

    private void ProcessDcsParam(byte value)
    {
        if (value is >= (byte)'0' and <= (byte)'9')
        {
            _currentParameter = _currentParameter < 0 ? 0 : _currentParameter;
            _currentParameter = Math.Min((_currentParameter * 10) + (value - '0'), 65535);
        }
        else if (value == (byte)';')
        {
            PushDcsParameter();
        }
        else if (value == (byte)':' || value is >= 0x3C and <= 0x3F)
        {
            _state = State.DcsIgnore;
        }
        else if (value is >= 0x20 and <= 0x2F)
        {
            PushDcsParameterIfNeeded();
            if (_state != State.DcsIgnore)
            {
                AppendDcsIntermediate(value);
            }
        }
        else if (IsFinal(value))
        {
            PushDcsParameterIfNeeded();
            if (_state != State.DcsIgnore)
            {
                StartDcsPassthrough(value);
            }
        }
        else
        {
            _state = State.DcsIgnore;
        }
    }

    private void ProcessDcsIntermediate(byte value)
    {
        if (value is >= 0x20 and <= 0x2F)
        {
            AppendDcsIntermediate(value);
        }
        else if (IsFinal(value))
        {
            StartDcsPassthrough(value);
        }
        else
        {
            _state = State.DcsIgnore;
        }
    }

    private void AppendDcsIntermediate(byte value)
    {
        if (_dcsIntermediateCount == _dcsIntermediates.Length)
        {
            _state = State.DcsIgnore;
            return;
        }

        _dcsIntermediates[_dcsIntermediateCount++] = value;
        _state = State.DcsIntermediate;
    }

    private void StartDcsPassthrough(byte final)
    {
        _dcsFinal = final;
        _state = State.DcsPassthrough;
    }

    private void AppendDcs(byte value)
    {
        if (_dcs.Count == MaxDcsPayloadBytes)
        {
            _dcs.Clear();
            _state = State.DcsIgnore;
            return;
        }

        _dcs.Add(value);
    }

    private void TerminateDcs(bool dispatch)
    {
        if (dispatch)
        {
            _dispatch.DcsDispatch(
                (char)_dcsFinal,
                _parameters.AsSpan(0, _parameterCount),
                _dcsIntermediates.AsSpan(0, _dcsIntermediateCount),
                _privateMarker,
                System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_dcs));
        }

        CancelDcs();
    }

    private void CancelDcs()
    {
        ClearDcs();
        ClearSequence();
        _state = State.Ground;
    }

    private void ClearDcs()
    {
        _dcs.Clear();
        _dcsFinal = 0;
        _dcsIntermediateCount = 0;
        _dcsEscapeCanDispatch = false;
    }

    private void PushDcsParameter()
    {
        if (_parameterCount >= _parameters.Length)
        {
            _state = State.DcsIgnore;
            return;
        }

        _parameters[_parameterCount++] = _currentParameter;
        _currentParameter = -1;
    }

    private void PushDcsParameterIfNeeded()
    {
        if (_currentParameter >= 0 || _parameterCount > 0)
        {
            PushDcsParameter();
        }
    }

    private void PushParameter()
    {
        if (_parameterCount >= _parameters.Length)
        {
            _state = State.CsiIgnore;
            return;
        }

        _parameters[_parameterCount++] = _currentParameter;
        _currentParameter = -1;
    }

    private void PushParameterIfNeeded()
    {
        if (_currentParameter >= 0 || _parameterCount > 0)
        {
            PushParameter();
        }
    }

    private void ClearSequence()
    {
        _parameterCount = 0;
        _currentParameter = -1;
        _intermediate = 0;
        _privateMarker = 0;
    }

    private void StartUtf8(int accumulator, int needed, int minimum)
    {
        _utf8Accumulator = accumulator;
        _utf8Needed = needed;
        _utf8Minimum = minimum;
    }

    private void EmitIncompleteUtf8()
    {
        if (_utf8Needed > 0)
        {
            EmitReplacement();
            ResetUtf8();
        }
    }

    private void EmitReplacement() => _dispatch.Print(Rune.ReplacementChar);

    private void ResetUtf8()
    {
        _utf8Needed = 0;
        _utf8Accumulator = 0;
        _utf8Minimum = 0;
    }

    private static bool IsFinal(byte value) => value is >= 0x40 and <= 0x7E;
}
