using System.Globalization;
using System.Text;
using Avalonia.Input;
using Devolutions.Terminal.Core;

namespace Devolutions.Terminal;

public enum TerminalKeyEventType : byte
{
    Press = 1,
    Repeat = 2,
    Release = 3,
}

public static class KeyMapper
{
    public static string? ToVt(
        Key key,
        KeyModifiers modifiers,
        PhysicalKey physicalKey,
        string? keySymbol,
        bool applicationCursorKeys) =>
        ToVt(
            key,
            modifiers,
            physicalKey,
            keySymbol,
            new TerminalInputMode(true, applicationCursorKeys, false, KittyKeyboardFlags.None, 0, false));

    public static string? ToVt(
        Key key,
        KeyModifiers modifiers,
        PhysicalKey physicalKey,
        string? keySymbol,
        TerminalInputMode mode,
        TerminalKeyEventType eventType = TerminalKeyEventType.Press,
        ushort repeatCount = 1)
    {
        if (physicalKey is PhysicalKey.Enter or PhysicalKey.NumPadEnter)
        {
            key = Key.Return;
        }

        if (mode.KittyFlags != KittyKeyboardFlags.None)
        {
            var kitty = EncodeKitty(
                key,
                modifiers,
                physicalKey,
                keySymbol,
                mode.KittyFlags,
                eventType);
            if (kitty is not null)
            {
                return kitty;
            }
        }

        if (mode.Win32InputMode && mode.KittyFlags == KittyKeyboardFlags.None)
        {
            return EncodeWin32(key, modifiers, keySymbol, eventType, repeatCount);
        }

        if (!mode.AnsiMode)
        {
            return eventType == TerminalKeyEventType.Release
                ? null
                : EncodeVt52(key, modifiers, mode.ApplicationKeypad);
        }

        if (eventType == TerminalKeyEventType.Release)
        {
            return null;
        }

        var ctrl = modifiers.HasFlag(KeyModifiers.Control);
        var alt = modifiers.HasFlag(KeyModifiers.Alt);
        var shift = modifiers.HasFlag(KeyModifiers.Shift);

        if (TrySingleRune(keySymbol, out var textRune) &&
            mode.ModifyOtherKeys > 0 &&
            (mode.ModifyOtherKeys == 2 || ctrl || alt))
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"\u001b[27;{KittyModifiers(modifiers)};{textRune.Value}~");
        }

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
            Key.Up => mode.ApplicationCursorKeys ? "\u001bOA" : "\u001b[A",
            Key.Down => mode.ApplicationCursorKeys ? "\u001bOB" : "\u001b[B",
            Key.Right => mode.ApplicationCursorKeys ? "\u001bOC" : "\u001b[C",
            Key.Left => mode.ApplicationCursorKeys ? "\u001bOD" : "\u001b[D",
            Key.Home => mode.ApplicationCursorKeys ? "\u001bOH" : "\u001b[H",
            Key.End => mode.ApplicationCursorKeys ? "\u001bOF" : "\u001b[F",
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
            >= Key.NumPad0 and <= Key.NumPad9 when mode.ApplicationKeypad =>
                $"\u001bO{(char)('p' + key - Key.NumPad0)}",
            Key.Decimal when mode.ApplicationKeypad => "\u001bOn",
            Key.Subtract when mode.ApplicationKeypad => "\u001bOm",
            Key.Add when mode.ApplicationKeypad => "\u001bOk",
            Key.Multiply when mode.ApplicationKeypad => "\u001bOj",
            Key.Divide when mode.ApplicationKeypad => "\u001bOo",
            _ => null,
        };

        if (sequence is not null)
        {
            return alt ? "\u001b" + sequence : sequence;
        }

        return !string.IsNullOrEmpty(keySymbol) && !ctrl && alt ? "\u001b" + keySymbol : null;
    }

    private static string? EncodeVt52(Key key, KeyModifiers modifiers, bool applicationKeypad)
    {
        var sequence = key switch
        {
            Key.Up => "\u001bA",
            Key.Down => "\u001bB",
            Key.Right => "\u001bC",
            Key.Left => "\u001bD",
            Key.F1 => "\u001bP",
            Key.F2 => "\u001bQ",
            Key.F3 => "\u001bR",
            Key.F4 => "\u001bS",
            >= Key.NumPad0 and <= Key.NumPad9 when applicationKeypad =>
                $"\u001b?{(char)('p' + key - Key.NumPad0)}",
            Key.Decimal when applicationKeypad => "\u001b?n",
            Key.Subtract when applicationKeypad => "\u001b?m",
            Key.Add when applicationKeypad => "\u001b?k",
            Key.Multiply when applicationKeypad => "\u001b?j",
            Key.Divide when applicationKeypad => "\u001b?o",
            Key.Return or Key.LineFeed => "\r",
            Key.Tab => "\t",
            Key.Back => "\b",
            Key.Escape => "\u001b",
            _ => null,
        };
        return sequence is not null && modifiers.HasFlag(KeyModifiers.Alt)
            ? "\u001b" + sequence
            : sequence;
    }

    private static string? EncodeKitty(
        Key key,
        KeyModifiers modifiers,
        PhysicalKey physicalKey,
        string? keySymbol,
        KittyKeyboardFlags flags,
        TerminalKeyEventType eventType)
    {
        if (eventType != TerminalKeyEventType.Press &&
            !flags.HasFlag(KittyKeyboardFlags.ReportEventTypes))
        {
            return null;
        }

        var codepoint = KittyCodepoint(key);
        var textKey = codepoint == 0;
        if (textKey)
        {
            codepoint = KittyTextBaseCodepoint(key, physicalKey, keySymbol);
        }

        if (codepoint == 0)
        {
            return null;
        }

        var allKeys = flags.HasFlag(KittyKeyboardFlags.ReportAllKeysAsEscapeCodes);
        var disambiguate = flags.HasFlag(KittyKeyboardFlags.DisambiguateEscapeCodes);
        var altOrControl = modifiers.HasFlag(KeyModifiers.Alt) ||
                           modifiers.HasFlag(KeyModifiers.Control);
        var disambiguated = disambiguate &&
            (key == Key.Escape ||
             (key is Key.Return or Key.LineFeed or Key.Tab or Key.Back &&
              modifiers != KeyModifiers.None) ||
             (textKey && altOrControl) ||
             codepoint >= 57344);
        var release = eventType == TerminalKeyEventType.Release &&
                      flags.HasFlag(KittyKeyboardFlags.ReportEventTypes);
        if (!allKeys && !disambiguated && !release)
        {
            return null;
        }

        var modifier = KittyModifiers(modifiers);
        var eventSuffix = flags.HasFlag(KittyKeyboardFlags.ReportEventTypes) &&
                          eventType != TerminalKeyEventType.Press
            ? $":{(int)eventType}"
            : string.Empty;
        var associatedText = flags.HasFlag(KittyKeyboardFlags.ReportAssociatedText) &&
                             eventType != TerminalKeyEventType.Release &&
                             TrySingleRune(keySymbol, out var associatedRune) &&
                             IsKittyText(associatedRune) &&
                             !modifiers.HasFlag(KeyModifiers.Control)
            ? associatedRune.Value
            : 0;
        var modifierField = modifier != 1 || eventSuffix.Length > 0
            ? modifier.ToString(CultureInfo.InvariantCulture) + eventSuffix
            : string.Empty;
        if (associatedText != 0)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"\u001b[{codepoint};{modifierField};{associatedText}u");
        }

        if (modifierField.Length == 0)
        {
            return string.Create(CultureInfo.InvariantCulture, $"\u001b[{codepoint}u");
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"\u001b[{codepoint};{modifierField}u");
    }

    internal static string? EncodeKittyTextInput(string text, KittyKeyboardFlags flags)
    {
        if (!flags.HasFlag(KittyKeyboardFlags.ReportAssociatedText) ||
            string.IsNullOrEmpty(text))
        {
            return null;
        }

        var codepoints = new List<int>();
        foreach (var rune in text.EnumerateRunes())
        {
            if (!IsKittyText(rune))
            {
                return null;
            }
            codepoints.Add(rune.Value);
        }
        return codepoints.Count == 0
            ? null
            : $"\u001b[0;;{string.Join(':', codepoints)}u";
    }

    private static int KittyTextBaseCodepoint(
        Key key,
        PhysicalKey physicalKey,
        string? keySymbol)
    {
        if (key is >= Key.A and <= Key.Z)
        {
            return 'a' + key - Key.A;
        }

        if (key is >= Key.D0 and <= Key.D9)
        {
            return '0' + key - Key.D0;
        }

        var physicalCodepoint = physicalKey switch
        {
            >= PhysicalKey.A and <= PhysicalKey.Z => 'a' + physicalKey - PhysicalKey.A,
            >= PhysicalKey.Digit0 and <= PhysicalKey.Digit9 =>
                '0' + physicalKey - PhysicalKey.Digit0,
            PhysicalKey.Space => ' ',
            PhysicalKey.Backquote => '`',
            PhysicalKey.Backslash => '\\',
            PhysicalKey.BracketLeft => '[',
            PhysicalKey.BracketRight => ']',
            PhysicalKey.Comma => ',',
            PhysicalKey.Equal => '=',
            PhysicalKey.Minus => '-',
            PhysicalKey.Period => '.',
            PhysicalKey.Quote => '\'',
            PhysicalKey.Semicolon => ';',
            PhysicalKey.Slash => '/',
            _ => 0,
        };
        if (physicalCodepoint != 0)
        {
            return physicalCodepoint;
        }

        return TrySingleRune(keySymbol, out var rune) ? rune.Value : 0;
    }

    private static bool IsKittyText(Rune rune) =>
        rune.Value is > 0x1F and < 0x7F or > 0x9F;

    private static int KittyCodepoint(Key key) => key switch
    {
        Key.Escape => 27,
        Key.Return or Key.LineFeed => 13,
        Key.Tab => 9,
        Key.Back => 127,
        Key.Insert => 57348,
        Key.Delete => 57349,
        Key.PageUp => 57350,
        Key.PageDown => 57351,
        Key.Up => 57352,
        Key.Down => 57353,
        Key.Left => 57354,
        Key.Right => 57355,
        Key.Home => 57356,
        Key.End => 57357,
        >= Key.F1 and <= Key.F12 => 57364 + key - Key.F1,
        _ => 0,
    };

    private static string EncodeWin32(
        Key key,
        KeyModifiers modifiers,
        string? keySymbol,
        TerminalKeyEventType eventType,
        ushort repeatCount)
    {
        var virtualKey = VirtualKey(key);
        var unicode = TrySingleRune(keySymbol, out var rune) ? rune.Value : 0;
        var keyDown = eventType == TerminalKeyEventType.Release ? 0 : 1;
        var controlState =
            (modifiers.HasFlag(KeyModifiers.Shift) ? 0x10 : 0) |
            (modifiers.HasFlag(KeyModifiers.Alt) ? 0x02 : 0) |
            (modifiers.HasFlag(KeyModifiers.Control) ? 0x08 : 0);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"\u001b[{virtualKey};0;{unicode};{keyDown};{controlState};{Math.Max(1, (int)repeatCount)}_");
    }

    private static int VirtualKey(Key key) => key switch
    {
        >= Key.A and <= Key.Z => 0x41 + key - Key.A,
        >= Key.D0 and <= Key.D9 => 0x30 + key - Key.D0,
        >= Key.NumPad0 and <= Key.NumPad9 => 0x60 + key - Key.NumPad0,
        Key.Return or Key.LineFeed => 0x0D,
        Key.Tab => 0x09,
        Key.Back => 0x08,
        Key.Escape => 0x1B,
        Key.Left => 0x25,
        Key.Up => 0x26,
        Key.Right => 0x27,
        Key.Down => 0x28,
        Key.Delete => 0x2E,
        Key.Insert => 0x2D,
        Key.Home => 0x24,
        Key.End => 0x23,
        Key.PageUp => 0x21,
        Key.PageDown => 0x22,
        >= Key.F1 and <= Key.F12 => 0x70 + key - Key.F1,
        _ => 0,
    };

    private static int KittyModifiers(KeyModifiers modifiers) =>
        1 +
        (modifiers.HasFlag(KeyModifiers.Shift) ? 1 : 0) +
        (modifiers.HasFlag(KeyModifiers.Alt) ? 2 : 0) +
        (modifiers.HasFlag(KeyModifiers.Control) ? 4 : 0);

    private static bool TrySingleRune(string? text, out Rune rune)
    {
        rune = default;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var status = Rune.DecodeFromUtf16(text, out rune, out var consumed);
        return status == System.Buffers.OperationStatus.Done && consumed == text.Length;
    }
}
