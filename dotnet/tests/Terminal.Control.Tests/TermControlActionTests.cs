using Microsoft.Terminal.Control;
using Microsoft.Terminal.Core;
using Avalonia.Input;
using Xunit;

namespace Terminal.Control.Tests;

public sealed class TermControlActionTests
{
    [Fact]
    public void SelectionActionsUpdateState()
    {
        var control = new TermControl();
        control.Engine.Feed("hello");

        control.SelectAll();
        Assert.True(control.HasSelection);

        control.ClearSelection();
        Assert.False(control.HasSelection);
    }

    [Fact]
    public void FindSelectsVisibleMatch()
    {
        var control = new TermControl();
        control.Engine.Feed("hello terminal");

        Assert.True(control.Find("terminal"));
        Assert.True(control.HasSelection);
        Assert.False(control.Find("missing"));
    }

    [Fact]
    public void FontActionsClampAndReset()
    {
        var control = new TermControl();

        control.AdjustFontSize(100);
        Assert.Equal(72, control.FontSize);

        control.ResetFontSize();
        Assert.Equal(12, control.FontSize);
    }

    [Fact]
    public void ScrollActionsUpdateViewport()
    {
        var control = new TermControl();
        control.Engine.Resize(10, 3);
        control.Engine.Feed("one\r\ntwo\r\nthree\r\nfour\r\nfive");

        control.ScrollToTop();
        Assert.Equal(control.Engine.Buffer.HistoryCount, control.Engine.Buffer.ScrollOffset);

        control.ScrollToBottom();
        Assert.Equal(0, control.Engine.Buffer.ScrollOffset);

        control.ScrollBy(1);
        Assert.Equal(1, control.Engine.Buffer.ScrollOffset);
    }

    [Fact]
    public void KittyKeyEventSuppressesItsPairedRawTextInput()
    {
        var control = new TermControl();
        var mode = new TerminalInputMode(
            true,
            false,
            false,
            KittyKeyboardFlags.ReportAllKeysAsEscapeCodes |
            KittyKeyboardFlags.ReportAssociatedText,
            0,
            false);

        Assert.Equal(
            "\u001b[97;2;65u",
            control.ProcessKeyDownInput(
                Key.A,
                KeyModifiers.Shift,
                PhysicalKey.A,
                "A",
                mode));
        Assert.Null(control.ProcessTextInput("A", mode));
    }

    [Fact]
    public void KittyTextInputPreservesImeCommitsAndNormalFallback()
    {
        var control = new TermControl();
        var associatedMode = new TerminalInputMode(
            true,
            false,
            false,
            KittyKeyboardFlags.ReportAllKeysAsEscapeCodes |
            KittyKeyboardFlags.ReportAssociatedText,
            0,
            false);
        var reportAllOnly = associatedMode with
        {
            KittyFlags = KittyKeyboardFlags.ReportAllKeysAsEscapeCodes,
        };

        Assert.Equal(
            "\u001b[0;;28450:233:128578u",
            control.ProcessTextInput("漢é🙂", associatedMode));
        Assert.Equal("normal", control.ProcessTextInput("normal", reportAllOnly));
    }

    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 0)]
    public void ExtendedKeyEncodingSuppressesPairedRawTextInput(
        bool win32Input,
        int modifyOtherKeys)
    {
        var control = new TermControl();
        var mode = new TerminalInputMode(
            true,
            false,
            false,
            KittyKeyboardFlags.None,
            modifyOtherKeys,
            win32Input);

        Assert.NotNull(control.ProcessKeyDownInput(
            Key.A,
            KeyModifiers.Control,
            PhysicalKey.A,
            "a",
            mode));
        Assert.Null(control.ProcessTextInput("a", mode));
    }

    [Fact]
    public void EncodedKeyWithoutTextInputCannotSuppressALaterKey()
    {
        var control = new TermControl();
        var encoded = new TerminalInputMode(
            true,
            false,
            false,
            KittyKeyboardFlags.None,
            2,
            false);
        var normal = encoded with { ModifyOtherKeys = 0 };

        Assert.NotNull(control.ProcessKeyDownInput(
            Key.A,
            KeyModifiers.Alt,
            PhysicalKey.A,
            "a",
            encoded));
        Assert.Null(control.ProcessKeyDownInput(
            Key.B,
            KeyModifiers.None,
            PhysicalKey.B,
            "b",
            normal));
        Assert.Equal("a", control.ProcessTextInput("a", normal));
    }
}
