using Avalonia.Input;
using Microsoft.Terminal.Core;

namespace Microsoft.Terminal.Control;

public static class KeyMapper
{
    public static string? ToVt(Key key, KeyModifiers modifiers, PhysicalKey physicalKey, string? keySymbol, bool applicationCursorKeys)
    {
        if (physicalKey is PhysicalKey.Enter or PhysicalKey.NumPadEnter)
        {
            key = Key.Return;
        }

        var ctrl = modifiers.HasFlag(KeyModifiers.Control);
        var alt = modifiers.HasFlag(KeyModifiers.Alt);
        var shift = modifiers.HasFlag(KeyModifiers.Shift);

        if (ctrl && !alt && !shift && key is >= Key.A and <= Key.Z)
        {
            return ((char)(key - Key.A + 1)).ToString();
        }

        if (ctrl && key == Key.Space)
        {
            return "\0";
        }

        var sequence = key switch
        {
            Key.Return or Key.LineFeed => "\r",
            Key.Tab => shift ? "\u001b[Z" : "\t",
            Key.Back => "\u007f",
            Key.Escape => "\u001b",
            Key.Up => applicationCursorKeys ? "\u001bOA" : "\u001b[A",
            Key.Down => applicationCursorKeys ? "\u001bOB" : "\u001b[B",
            Key.Right => applicationCursorKeys ? "\u001bOC" : "\u001b[C",
            Key.Left => applicationCursorKeys ? "\u001bOD" : "\u001b[D",
            Key.Home => applicationCursorKeys ? "\u001bOH" : "\u001b[H",
            Key.End => applicationCursorKeys ? "\u001bOF" : "\u001b[F",
            Key.Insert => "\u001b[2~",
            Key.Delete => "\u001b[3~",
            Key.PageUp => "\u001b[5~",
            Key.PageDown => "\u001b[6~",
            Key.F1 => "\u001bOP",
            Key.F2 => "\u001bOQ",
            Key.F3 => "\u001bOR",
            Key.F4 => "\u001bOS",
            Key.F5 => "\u001b[15~",
            Key.F6 => "\u001b[17~",
            Key.F7 => "\u001b[18~",
            Key.F8 => "\u001b[19~",
            Key.F9 => "\u001b[20~",
            Key.F10 => "\u001b[21~",
            Key.F11 => "\u001b[23~",
            Key.F12 => "\u001b[24~",
            _ => null,
        };

        if (sequence is not null)
        {
            return alt ? "\u001b" + sequence : sequence;
        }

        if (!string.IsNullOrEmpty(keySymbol) && !ctrl)
        {
            return alt ? "\u001b" + keySymbol : null;
        }

        _ = physicalKey;
        return null;
    }
}
