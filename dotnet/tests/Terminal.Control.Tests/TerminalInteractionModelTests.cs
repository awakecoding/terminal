using Avalonia.Input;
using Microsoft.Terminal.Control;
using Microsoft.Terminal.Core;
using Microsoft.Terminal.Render;
using Microsoft.Terminal.Settings;
using System.Reflection;
using System.Text;
using Xunit;

namespace Terminal.Control.Tests;

public sealed class TerminalInteractionModelTests
{
    [Fact]
    public void LinearSelectionPreservesWrappedLines()
    {
        var engine = new TerminalEngine(5, 3);
        engine.Feed("abcdef");
        var snapshot = engine.CreateSnapshot(includeHistory: true).Buffer;
        var selection = new TerminalSelection(
            new TerminalSelectionPoint(1, 0),
            new TerminalSelectionPoint(0, 1));

        var text = TerminalInteractionModel.GetSelectedText(snapshot, selection, trimBlockSelection: true);

        Assert.Equal("bcdef", text);
    }

    [Fact]
    public void BlockSelectionUsesSameColumnsAndTrimsEachRow()
    {
        var engine = new TerminalEngine(8, 3);
        engine.Feed("abcd\r\nxy");
        var snapshot = engine.CreateSnapshot(includeHistory: true).Buffer;
        var selection = new TerminalSelection(
            new TerminalSelectionPoint(1, 0),
            new TerminalSelectionPoint(3, 1),
            TerminalSelectionMode.Block);

        var text = TerminalInteractionModel.GetSelectedText(snapshot, selection, trimBlockSelection: true);

        Assert.Equal("bcd\ny", text);
    }

    [Fact]
    public void WordAndLogicalLineSelectionRespectDelimitersAndWrap()
    {
        var engine = new TerminalEngine(6, 3);
        engine.Feed("hello world");
        var snapshot = engine.CreateSnapshot(includeHistory: true).Buffer;

        var word = TerminalInteractionModel.SelectAt(
            snapshot,
            new TerminalSelectionPoint(1, 0),
            TerminalSelectionMode.Word,
            " ");
        var line = TerminalInteractionModel.SelectAt(
            snapshot,
            new TerminalSelectionPoint(2, 1),
            TerminalSelectionMode.Line,
            " ");

        Assert.Equal("hello", TerminalInteractionModel.GetSelectedText(snapshot, word, true));
        Assert.Equal("hello world", TerminalInteractionModel.GetSelectedText(snapshot, line, true).TrimEnd());
    }

    [Fact]
    public void SelectionSnapsWideContinuationToLeadingCell()
    {
        var engine = new TerminalEngine(8, 2);
        engine.Feed("\u754cx");
        var snapshot = engine.CreateSnapshot(includeHistory: true).Buffer;

        var selection = TerminalInteractionModel.SelectAt(
            snapshot,
            new TerminalSelectionPoint(1, 0),
            TerminalSelectionMode.Word,
            " ");

        Assert.Equal(0, selection.Anchor.Column);
        Assert.Equal("\u754cx", TerminalInteractionModel.GetSelectedText(snapshot, selection, true));
    }

    [Fact]
    public void CommandAndOutputSelectionUseShellIntegration()
    {
        var engine = new TerminalEngine(20, 4);
        engine.Feed("\u001b]133;A\u0007PS> ");
        engine.Feed("\u001b]133;B\u0007echo hi");
        engine.Feed("\u001b]133;C\u0007\r\nresult");
        engine.Feed("\u001b]133;D;0\u0007");
        var snapshot = engine.CreateSnapshot(includeHistory: true).Buffer;

        var command = TerminalInteractionModel.SelectAt(
            snapshot,
            new TerminalSelectionPoint(5, 0),
            TerminalSelectionMode.Command,
            " ");
        var output = TerminalInteractionModel.SelectAt(
            snapshot,
            new TerminalSelectionPoint(1, 1),
            TerminalSelectionMode.Output,
            " ");

        Assert.Equal("echo hi", TerminalInteractionModel.GetSelectedText(snapshot, command, true));
        Assert.Equal("result", TerminalInteractionModel.GetSelectedText(snapshot, output, true));
    }

    [Fact]
    public void ClipboardPayloadSupportsPlainHtmlRtfAndControlPolicy()
    {
        var payload = TerminalInteractionModel.BuildClipboardPayload(
            "one\u001btwo\n\u754c",
            new TerminalCopyOptions
            {
                SingleLine = true,
                Formats = CopyFormat.All,
            });

        Assert.Equal("onetwo \u754c", payload.Text);
        Assert.Contains("<!--StartFragment--><pre>onetwo \u754c</pre>", payload.Html);
        Assert.StartsWith(@"{\rtf1", payload.Rtf);
        Assert.Contains(@"\u30028?", payload.Rtf);

        var data = TermControl.CreateClipboardDataObject(payload);
        Assert.IsType<byte[]>(data.Get("HTML Format"));
        Assert.IsType<byte[]>(data.Get("Rich Text Format"));
    }

    [Fact]
    public void BracketedPastePreservesWhitespaceAndSuppressesAutomaticMultilineWarning()
    {
        var request = TerminalInteractionModel.PreparePaste(
            "  one\ntwo  ",
            new TerminalPasteOptions(),
            bracketedPaste: true);

        Assert.Equal("  one\rtwo  ", request.Text);
        Assert.Equal(TerminalPasteWarning.None, request.Warning);
        Assert.True(request.BracketedPaste);

        var empty = TerminalInteractionModel.PreparePaste(
            string.Empty,
            new TerminalPasteOptions(),
            bracketedPaste: true);
        Assert.True(empty.BracketedPaste);
        Assert.Empty(empty.Text);
    }

    [Theory]
    [InlineData("  one\r\ntwo  ", 7, 2, TerminalPasteWarning.MultiLine)]
    [InlineData("12345", 5, 1, TerminalPasteWarning.Large)]
    public void PastePreparationTrimsNormalizesAndClassifiesWarnings(
        string input,
        int expectedCharacters,
        int expectedLines,
        TerminalPasteWarning expectedWarning)
    {
        var request = TerminalInteractionModel.PreparePaste(
            input,
            new TerminalPasteOptions { LargePasteThreshold = 5 });

        Assert.Equal(expectedCharacters, request.CharacterCount);
        Assert.Equal(expectedLines, request.LineCount);
        Assert.True(request.Warning.HasFlag(expectedWarning));
        Assert.DoesNotContain('\n', request.Text);
    }

    [Fact]
    public void ScrollMarksCombineShellStatusAndSearchLocations()
    {
        var engine = new TerminalEngine(10, 3);
        engine.Feed("\u001b]133;A\u0007prompt");
        engine.Feed("\u001b]133;D;7\u0007");
        var snapshot = engine.CreateSnapshot(includeHistory: true).Buffer;
        var matches = TextBufferSearch.FindAll(snapshot, "prompt");

        var marks = TerminalInteractionModel.GetScrollMarks(snapshot, matches, 0);

        Assert.Contains(marks, mark => mark.Kind == TerminalScrollMarkKind.CommandError && mark.ExitCode == 7);
        Assert.Contains(marks, mark => mark.Kind == TerminalScrollMarkKind.CurrentSearchMatch);
        Assert.All(marks, mark => Assert.InRange(mark.Position, 0, 1));
    }

    [Fact]
    public void HyperlinkHitTestReturnsFullRunAndEnforcesSchemePolicy()
    {
        var engine = new TerminalEngine(30, 2);
        engine.Feed("\u001b]8;;https://example.test\u0007link text\u001b]8;;\u0007");
        var snapshot = engine.CreateSnapshot(includeHistory: true).Buffer;

        var safe = TerminalInteractionModel.HitTestHyperlink(
            snapshot,
            new TerminalSelectionPoint(2, 0),
            new HashSet<string>(["https"], StringComparer.OrdinalIgnoreCase));
        var blocked = TerminalInteractionModel.HitTestHyperlink(
            snapshot,
            new TerminalSelectionPoint(2, 0),
            new HashSet<string>(["mailto"], StringComparer.OrdinalIgnoreCase));

        Assert.Equal("link text", safe?.Text);
        Assert.True(safe?.CanOpen);
        Assert.False(blocked?.CanOpen);
    }

    [Theory]
    [InlineData("file:///C:/temp/tool.exe", false)]
    [InlineData("file:///C:/temp/readme.txt", true)]
    public void HyperlinkPolicyRejectsExecutableFileTargets(string uri, bool expectedCanOpen)
    {
        var engine = new TerminalEngine(30, 2);
        engine.Feed($"\u001b]8;;{uri}\u0007target\u001b]8;;\u0007");
        var snapshot = engine.CreateSnapshot(includeHistory: true).Buffer;

        var hyperlink = TerminalInteractionModel.HitTestHyperlink(
            snapshot,
            new TerminalSelectionPoint(2, 0),
            new HashSet<string>(["file"], StringComparer.OrdinalIgnoreCase));

        Assert.Equal(expectedCanOpen, hyperlink?.CanOpen);
    }

    [Fact]
    public void CursorAndMouseSequencesUseTerminalCoordinates()
    {
        Assert.Equal(
            "\u001b[B\u001b[B\u001b[D\u001b[D",
            TerminalInteractionModel.BuildCursorRepositionSequence(4, 2, 2, 4));
        Assert.Equal(
            "\u001bOB\u001bOB\u001bOD\u001bOD",
            TerminalInteractionModel.BuildCursorRepositionSequence(
                4,
                2,
                2,
                4,
                applicationCursorKeys: true));
        Assert.Equal(
            "\u001b[<16;3;4M",
            TerminalInteractionModel.BuildMouseSequence(
                0,
                2,
                3,
                released: false,
                sgr: true,
                KeyModifiers.Control));
    }

    [Fact]
    public void EngineRetainsNegotiatedMouseTrackingMode()
    {
        var engine = new TerminalEngine();

        engine.Feed("\u001b[?1002h");
        Assert.Equal(TerminalMouseTrackingMode.ButtonEvent, engine.MouseTrackingMode);
        engine.Feed("\u001b[?1002l");
        Assert.Equal(TerminalMouseTrackingMode.None, engine.MouseTrackingMode);

        engine.Feed("\u001b[?1003h");
        Assert.Equal(TerminalMouseTrackingMode.AllMotion, engine.MouseTrackingMode);
    }

    [Fact]
    public void DecrqmReportsEachMouseTrackingModeExactly()
    {
        var engine = new TerminalEngine();
        var responses = new List<byte>();
        engine.ResponseReady += (_, response) => responses.AddRange(response);
        engine.Feed("\u001b[?1002h");

        engine.Feed("\u001b[?1000$p\u001b[?1002$p\u001b[?1003$p");

        Assert.Equal(
            "\u001b[?1000;2$y\u001b[?1002;1$y\u001b[?1003;2$y",
            Encoding.UTF8.GetString([.. responses]));
    }

    [Fact]
    public void BufferCoordinateVersionChangesOnlyWhenCoordinatesCanShift()
    {
        var engine = new TerminalEngine(4, 2, historySize: 1);
        var initialVersion = engine.Buffer.CoordinateVersion;

        engine.Feed("a");
        Assert.Equal(initialVersion, engine.Buffer.CoordinateVersion);

        engine.Feed("\r\nb\r\nc\r\n");
        Assert.True(engine.Buffer.CoordinateVersion > initialVersion);
    }

    [Theory]
    [InlineData("abc", 3)]
    [InlineData("\u754c", 2)]
    [InlineData("\U0001f600", 2)]
    public void CompositionWidthUsesDisplayCells(string text, int expectedWidth)
    {
        var method = typeof(SkiaTerminalRenderer).GetMethod(
            "DisplayWidth",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        Assert.Equal(expectedWidth, method.Invoke(null, [text, int.MaxValue]));
    }
}
