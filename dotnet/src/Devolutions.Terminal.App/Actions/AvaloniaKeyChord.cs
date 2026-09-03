using Avalonia.Input;
using Devolutions.Terminal.Settings;

namespace Devolutions.Terminal.App.Actions;

public static class AvaloniaKeyChord
{
    public static bool TryCreate(KeyEventArgs args, out KeyChord chord)
    {
        var key = GetKeyName(args.Key);
        if (key is null)
        {
            chord = default;
            return false;
        }

        var parts = new List<string>(5);
        if (args.KeyModifiers.HasFlag(KeyModifiers.Meta))
        {
            parts.Add("win");
        }

        if (args.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            parts.Add("ctrl");
        }

        if (args.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            parts.Add("alt");
        }

        if (args.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            parts.Add("shift");
        }

        parts.Add(key);
        return KeyChord.TryParse(string.Join('+', parts), out chord);
    }

    public static string? GetKeyName(Key key)
    {
        if (key is >= Key.A and <= Key.Z)
        {
            return key.ToString().ToLowerInvariant();
        }

        if (key is >= Key.D0 and <= Key.D9)
        {
            return ((int)key - (int)Key.D0).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (key is >= Key.NumPad0 and <= Key.NumPad9)
        {
            return $"numpad{(int)key - (int)Key.NumPad0}";
        }

        if (key is >= Key.F1 and <= Key.F24)
        {
            return key.ToString().ToLowerInvariant();
        }

        return key switch
        {
            Key.Return or Key.LineFeed => "enter",
            Key.Tab => "tab",
            Key.Escape => "esc",
            Key.Space => "space",
            Key.Back => "backspace",
            Key.Delete => "delete",
            Key.Insert => "insert",
            Key.Home => "home",
            Key.End => "end",
            Key.PageUp => "pageup",
            Key.PageDown => "pagedown",
            Key.Apps => "menu",
            Key.Left => "left",
            Key.Right => "right",
            Key.Up => "up",
            Key.Down => "down",
            Key.OemPlus => "plus",
            Key.Add => "numpad_plus",
            Key.OemMinus => "minus",
            Key.Subtract => "numpad_minus",
            Key.OemComma => "comma",
            Key.OemPeriod => "period",
            Key.Decimal => "numpad_period",
            Key.OemQuestion => "slash",
            Key.Divide => "numpad_divide",
            Key.OemBackslash => "backslash",
            Key.OemSemicolon => "semicolon",
            Key.OemQuotes => "quote",
            Key.OemOpenBrackets => "open_bracket",
            Key.OemCloseBrackets => "close_bracket",
            Key.OemTilde => "backtick",
            Key.Multiply => "numpad_multiply",
            Key.BrowserBack => "browser_back",
            Key.BrowserForward => "browser_forward",
            Key.BrowserRefresh => "browser_refresh",
            Key.BrowserStop => "browser_stop",
            Key.BrowserSearch => "browser_search",
            Key.BrowserFavorites => "browser_favorites",
            Key.BrowserHome => "browser_home",
            _ => null,
        };
    }
}
