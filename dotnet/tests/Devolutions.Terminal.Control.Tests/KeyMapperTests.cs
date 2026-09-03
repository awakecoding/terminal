using Avalonia.Input;
using Devolutions.Terminal;
using Devolutions.Terminal.Core;
using Xunit;

namespace Devolutions.Terminal.Control.Tests;

public sealed class KeyMapperTests
{
    [Theory]
    [InlineData(Key.Return)]
    [InlineData(Key.LineFeed)]
    public void MapsEveryEnterKeyToCarriageReturn(Key key)
    {
        var sequence = KeyMapper.ToVt(
            key,
            KeyModifiers.None,
            PhysicalKey.None,
            null,
            applicationCursorKeys: false);

        Assert.Equal("\r", sequence);
    }

    [Theory]
    [InlineData(PhysicalKey.Enter)]
    [InlineData(PhysicalKey.NumPadEnter)]
    public void MapsPhysicalEnterWhenLogicalKeyIsUnavailable(PhysicalKey physicalKey)
    {
        var sequence = KeyMapper.ToVt(
            Key.None,
            KeyModifiers.None,
            physicalKey,
            null,
            applicationCursorKeys: false);

        Assert.Equal("\r", sequence);
    }

    [Fact]
    public void PhysicalAltEnterPreservesEscapePrefix()
    {
        var sequence = KeyMapper.ToVt(
            Key.None,
            KeyModifiers.Alt,
            PhysicalKey.Enter,
            null,
            applicationCursorKeys: false);

        Assert.Equal("\u001b\r", sequence);
    }

    [Fact]
    public void MapsApplicationCursorKey()
    {
        var sequence = KeyMapper.ToVt(
            Key.Up,
            KeyModifiers.None,
            PhysicalKey.ArrowUp,
            null,
            applicationCursorKeys: true);

        Assert.Equal("\u001bOA", sequence);
    }

    [Theory]
    [InlineData(Key.Up, "\u001bA")]
    [InlineData(Key.F1, "\u001bP")]
    [InlineData(Key.F4, "\u001bS")]
    public void MapsVt52CursorAndPfKeys(Key key, string expected)
    {
        var sequence = KeyMapper.ToVt(
            key,
            KeyModifiers.None,
            PhysicalKey.None,
            null,
            new TerminalInputMode(false, false, false, KittyKeyboardFlags.None, 0, false));

        Assert.Equal(expected, sequence);
    }

    [Fact]
    public void MapsVt52AndAnsiApplicationKeypad()
    {
        var vt52 = KeyMapper.ToVt(
            Key.NumPad3,
            KeyModifiers.None,
            PhysicalKey.None,
            null,
            new TerminalInputMode(false, false, true, KittyKeyboardFlags.None, 0, false));
        var ansi = KeyMapper.ToVt(
            Key.NumPad3,
            KeyModifiers.None,
            PhysicalKey.None,
            null,
            new TerminalInputMode(true, false, true, KittyKeyboardFlags.None, 0, false));

        Assert.Equal("\u001b?s", vt52);
        Assert.Equal("\u001bOs", ansi);
    }

    [Fact]
    public void KittyCsiUReportsPressRepeatAndRelease()
    {
        var mode = new TerminalInputMode(
            true,
            false,
            false,
            KittyKeyboardFlags.DisambiguateEscapeCodes | KittyKeyboardFlags.ReportEventTypes,
            0,
            false);

        Assert.Equal(
            "\u001b[57352;7u",
            KeyMapper.ToVt(Key.Up, KeyModifiers.Alt | KeyModifiers.Control, PhysicalKey.None, null, mode));
        Assert.Equal(
            "\u001b[57352;1:2u",
            KeyMapper.ToVt(
                Key.Up,
                KeyModifiers.None,
                PhysicalKey.None,
                null,
                mode,
                TerminalKeyEventType.Repeat));
        Assert.Equal(
            "\u001b[57352;1:3u",
            KeyMapper.ToVt(
                Key.Up,
                KeyModifiers.None,
                PhysicalKey.None,
                null,
                mode,
                TerminalKeyEventType.Release));
    }

    [Fact]
    public void KittyModeTakesPrecedenceOverWin32InputMode()
    {
        var mode = new TerminalInputMode(
            true,
            false,
            false,
            KittyKeyboardFlags.DisambiguateEscapeCodes,
            0,
            true);
        var sequence = KeyMapper.ToVt(
            Key.Up,
            KeyModifiers.Control,
            PhysicalKey.ArrowUp,
            null,
            mode);
        var shiftedText = KeyMapper.ToVt(
            Key.A,
            KeyModifiers.Shift,
            PhysicalKey.A,
            "A",
            mode);

        Assert.Equal("\u001b[57352;5u", sequence);
        Assert.Null(shiftedText);
    }

    [Fact]
    public void KittyDisambiguationLeavesShiftOnlyPrintableTextUnencoded()
    {
        var sequence = KeyMapper.ToVt(
            Key.A,
            KeyModifiers.Shift,
            PhysicalKey.A,
            "A",
            new TerminalInputMode(
                true,
                false,
                false,
                KittyKeyboardFlags.DisambiguateEscapeCodes,
                0,
                false));

        Assert.Null(sequence);
    }

    [Theory]
    [InlineData(Key.A, KeyModifiers.None, "a", "\u001b[97;;97u")]
    [InlineData(Key.A, KeyModifiers.Shift, "A", "\u001b[97;2;65u")]
    [InlineData(Key.D1, KeyModifiers.Shift, "!", "\u001b[49;2;33u")]
    public void KittyAssociatedTextUsesTheThirdParameter(
        Key key,
        KeyModifiers modifiers,
        string text,
        string expected)
    {
        var sequence = KeyMapper.ToVt(
            key,
            modifiers,
            PhysicalKey.None,
            text,
            new TerminalInputMode(
                true,
                false,
                false,
                KittyKeyboardFlags.ReportAllKeysAsEscapeCodes |
                KittyKeyboardFlags.ReportAssociatedText,
                0,
                false));

        Assert.Equal(expected, sequence);
    }

    [Theory]
    [InlineData(Key.OemMinus, PhysicalKey.Minus, "_", "\u001b[45;2;95u")]
    [InlineData(Key.OemPlus, PhysicalKey.Equal, "+", "\u001b[61;2;43u")]
    [InlineData(Key.OemOpenBrackets, PhysicalKey.BracketLeft, "{", "\u001b[91;2;123u")]
    [InlineData(Key.OemQuestion, PhysicalKey.Slash, "?", "\u001b[47;2;63u")]
    public void KittyShiftedPunctuationUsesUnshiftedPhysicalCode(
        Key key,
        PhysicalKey physicalKey,
        string text,
        string expected)
    {
        var sequence = KeyMapper.ToVt(
            key,
            KeyModifiers.Shift,
            physicalKey,
            text,
            new TerminalInputMode(
                true,
                false,
                false,
                KittyKeyboardFlags.ReportAllKeysAsEscapeCodes |
                KittyKeyboardFlags.ReportAssociatedText,
                0,
                false));

        Assert.Equal(expected, sequence);
    }

    [Fact]
    public void KittyTextOnlyInputUsesAssociatedTextCodepoints()
    {
        Assert.Equal(
            "\u001b[0;;233:128578u",
            KeyMapper.EncodeKittyTextInput(
                "é🙂",
                KittyKeyboardFlags.ReportAssociatedText));
    }

    [Fact]
    public void ModifyOtherKeysAndWin32InputHaveDistinctEncodings()
    {
        var modified = KeyMapper.ToVt(
            Key.A,
            KeyModifiers.Control,
            PhysicalKey.A,
            "a",
            new TerminalInputMode(true, false, false, KittyKeyboardFlags.None, 2, false));
        var win32Release = KeyMapper.ToVt(
            Key.A,
            KeyModifiers.Control,
            PhysicalKey.A,
            "a",
            new TerminalInputMode(true, false, false, KittyKeyboardFlags.None, 0, true),
            TerminalKeyEventType.Release,
            repeatCount: 2);

        Assert.Equal("\u001b[27;5;97~", modified);
        Assert.Equal("\u001b[65;0;97;0;8;2_", win32Release);
    }
}
