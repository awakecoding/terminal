using Avalonia.Automation.Peers;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.TextInput;
using Microsoft.Terminal.Control;
using Microsoft.Terminal.Render;
using Microsoft.Terminal.Settings;
using Xunit;

namespace Terminal.Control.Tests;

public sealed class TermControlAccessibilityTests
{
    [AvaloniaFact]
    public void DefaultCascadiaMetricsMatchWindowsTerminalPointsAndDpiRounding()
    {
        var profile = new ProfileSettings
        {
            FontFace = "Cascadia Mono",
            FontSize = 12,
        };

        var defaultDpi = TermControl.MeasureCell(profile);
        var scaled = TermControl.MeasureCell(profile, 1.5);

        Assert.Equal(new CellSize(9, 19), defaultDpi);
        Assert.Equal(Math.Round(scaled.Width * 1.5), scaled.Width * 1.5, 6);
        Assert.Equal(Math.Round(scaled.Height * 1.5), scaled.Height * 1.5, 6);
    }

    [AvaloniaFact]
    public void AutomationPeerExposesDocumentValueSelectionAndCaret()
    {
        var control = new TermControl { AccessibleName = "PowerShell terminal" };
        control.Engine.Resize(20, 3);
        control.Engine.Feed("hello terminal");
        control.SelectWordAt(7, 0);
        var peer = new TermControlAutomationPeer(control);

        var state = peer.CreateState();

        Assert.Equal(AutomationControlType.Document, peer.GetAutomationControlType());
        Assert.Equal("PowerShell terminal", peer.GetName());
        Assert.Contains("hello terminal", peer.Value);
        Assert.Equal("terminal", peer.GetText(state.SelectionRange));
        Assert.False(state.DocumentRange.IsDegenerate);
        Assert.True(state.CaretRange.IsDegenerate);
        Assert.True(state.IsReadOnly);
    }

    [AvaloniaFact]
    public void TextInputMethodRequestReturnsCompositionClient()
    {
        var control = new TermControl();
        control.Engine.Resize(20, 3);
        control.Engine.Feed("prompt");
        var args = new TextInputMethodClientRequestedEventArgs
        {
            RoutedEvent = InputElement.TextInputMethodClientRequestedEvent,
        };

        control.RaiseEvent(args);

        Assert.NotNull(args.Client);
        Assert.True(args.Client.SupportsPreedit);
        Assert.True(args.Client.SupportsSurroundingText);
        Assert.StartsWith("prompt", args.Client.SurroundingText);
        Assert.True(args.Client.CursorRectangle.Width > 0);
        args.Client.SetPreeditText("\u306b\u307b\u3093", 2);
    }

    [AvaloniaFact]
    public void ImePreservesSpacesThroughCaretAndMapsWideCells()
    {
        var control = new TermControl();
        control.Engine.Resize(20, 3);
        control.Engine.Feed("a  ");
        var args = new TextInputMethodClientRequestedEventArgs
        {
            RoutedEvent = InputElement.TextInputMethodClientRequestedEvent,
        };
        control.RaiseEvent(args);

        Assert.Equal("a  ", args.Client!.SurroundingText);
        Assert.Equal(3, args.Client.Selection.Start);

        control.Engine.Feed("\r\u754c");
        Assert.Equal(1, args.Client.Selection.Start);
        args.Client.Selection = new TextSelection(1, 1);
        Assert.Equal(2, control.Engine.CursorX);
    }

    [Fact]
    public void SelectionSurvivesOutputButClearsWhenCoordinatesChange()
    {
        var control = new TermControl();
        control.Engine.Resize(20, 3);
        control.Engine.Feed("alpha");
        control.SelectAll();

        control.Engine.Feed("x");
        Assert.True(control.HasSelection);

        control.Engine.Resize(10, 3);
        Assert.False(control.HasSelection);
    }

    [Fact]
    public void SelectionClearsWhenSwitchingBuffers()
    {
        var control = new TermControl();
        control.Engine.Feed("alpha");
        control.SelectAll();

        control.Engine.Feed("\u001b[?1049h");

        Assert.False(control.HasSelection);
    }

    [Fact]
    public void MarkModeSupportsMovementEndpointSwitchAndWordExpansion()
    {
        var control = new TermControl();
        control.Engine.Resize(20, 3);
        control.Engine.Feed("alpha beta");

        control.EnterMarkMode();
        control.MoveMarkCaret(-2, 0);
        var beforeSwitch = control.Selection;
        control.SwitchSelectionEndpoint();
        control.MoveMarkCaret(-3, 0);
        control.ExpandSelectionToWord();
        control.ToggleBlockSelection();

        Assert.True(control.IsMarkMode);
        Assert.NotEqual(beforeSwitch, control.Selection);
        Assert.Equal(TerminalSelectionMode.Block, control.Selection?.Mode);
        control.ToggleBlockSelection();
        Assert.Equal(TerminalSelectionMode.Linear, control.Selection?.Mode);
        control.ExitMarkMode();
        Assert.False(control.IsMarkMode);
        Assert.True(control.HasSelection);
    }

    [Fact]
    public void ShellSelectionNavigatesPreviousAndNextCommands()
    {
        var control = new TermControl();
        control.Engine.Resize(20, 5);
        control.Engine.Feed("\u001b]133;A\u0007P1 ");
        control.Engine.Feed("\u001b]133;B\u0007C1");
        control.Engine.Feed("\u001b]133;C\u0007\r\nO1");
        control.Engine.Feed("\u001b]133;D;0\u0007\r\n");
        control.Engine.Feed("\u001b]133;A\u0007P2 ");
        control.Engine.Feed("\u001b]133;B\u0007C2");
        control.Engine.Feed("\u001b]133;C\u0007\r\nO2");
        control.Engine.Feed("\u001b]133;D;0\u0007");

        Assert.True(control.SelectCommand(TerminalShellSelectionDirection.Previous));
        Assert.Equal("C2", control.BuildCopyPayload()?.Text);
        Assert.True(control.SelectCommand(TerminalShellSelectionDirection.Previous));
        Assert.Equal("C1", control.BuildCopyPayload()?.Text);
        Assert.True(control.SelectCommand(TerminalShellSelectionDirection.Next));
        Assert.Equal("C2", control.BuildCopyPayload()?.Text);
    }

    [Fact]
    public void ClickWithoutDragDoesNotLeaveASelectionThatConsumesEnter()
    {
        var control = new TermControl();
        control.Engine.Resize(20, 3);
        control.Engine.Feed("prompt");

        control.BeginSelection(2, 0);
        control.EndSelection();

        Assert.False(control.HasSelection);
    }

    [Fact]
    public void PasteWarningContractAllowsSubscriberToCancel()
    {
        var control = new TermControl();
        var options = new TerminalPasteOptions
        {
            WarnAboutMultiLinePaste = "always",
            WarnAboutLargePaste = false,
        };

        Assert.Equal(TerminalPasteResult.NoConnection, control.PasteText("one\ntwo", options));

        control.PasteWarning += (_, args) => args.Allow = false;
        Assert.Equal(TerminalPasteResult.Cancelled, control.PasteText("one\ntwo", options));
    }

    [Fact]
    public void InteractionOptionsMapWindowsTerminalSettings()
    {
        var settings = new AppSettings
        {
            CopyFormatting = true,
            CopyFormatFormats = CopyFormat.Html,
            TrimBlockSelection = false,
            TrimPaste = false,
            WarnAboutLargePaste = false,
            WarnAboutMultiLinePaste = "never",
            WordDelimiters = "|",
            SafeUriSchemes = ["ssh"],
        };

        var options = TerminalInteractionOptions.FromSettings(settings);

        Assert.Equal(CopyFormat.Html, options.Copy.Formats);
        Assert.False(options.Copy.TrimBlockSelection);
        Assert.False(options.Paste.TrimWhitespace);
        Assert.False(options.Paste.WarnAboutLargePaste);
        Assert.Equal("never", options.Paste.WarnAboutMultiLinePaste);
        Assert.Equal("|", options.WordDelimiters);
        Assert.Contains("ssh", options.SafeUriSchemes);
    }
}
