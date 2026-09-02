using Microsoft.Terminal.Ghostty;
using Xunit;

namespace Microsoft.Terminal.Ghostty.Tests;

public sealed class GhosttyTerminalEngineTests
{
    public static TheoryData<string> SharedVtCorpus => new()
    {
        "plain text",
        "line one\r\nline two",
        "\u001b[2;4Hpositioned",
        "\u001b[31mred\u001b[0m normal",
        "\u001b[1mbold\u001b[0m \u001b[4munderline\u001b[0m",
        "\u001b[?2004hbracketed",
        "\u001b[?1049halt screen\u001b[?1049lprimary",
    };

    [Fact]
    public void AbiManifestMatchesPinnedSchema()
    {
        GhosttyAbi.Validate();

        Assert.Contains("\"schema\":1", GhosttyAbi.TypeManifest);
    }

    [Fact]
    public void FeedProjectsGhosttyGridIntoSharedBuffer()
    {
        using var engine = new GhosttyTerminalEngine(20, 3);

        engine.Feed("hello \u001b[1;32mGhostty\u001b[0m");

        var snapshot = engine.CreateSnapshot().Buffer;
        Assert.Equal("hello Ghostty", LineText(snapshot, 0));
        Assert.True(snapshot.Lines[0].Cells[6].Attributes.Flags.HasFlag(Microsoft.Terminal.Core.CellFlags.Bold));
        Assert.Equal(13, snapshot.CursorX);
    }

    [Fact]
    public void ResizeAndModesComeFromGhostty()
    {
        using var engine = new GhosttyTerminalEngine(20, 3);

        engine.Feed("\u001b[?2004h\u001b[?1h");
        engine.Resize(40, 5, 8, 16);

        Assert.True(engine.BracketedPaste);
        Assert.True(engine.ApplicationCursorKeys);
        Assert.Equal(40, engine.Columns);
        Assert.Equal(5, engine.Rows);
        Assert.Equal(320u, engine.PixelWidth);
        Assert.Equal(80u, engine.PixelHeight);
        Assert.Equal("\u001b[200~text\u001b[201~", engine.WrapPaste("text"));
    }

    [Fact]
    public void TitleAndBellEffectsAreSurfaced()
    {
        using var engine = new GhosttyTerminalEngine(20, 3);
        var titles = new List<string>();
        var bells = 0;
        engine.TitleChanged += (_, title) => titles.Add(title);
        engine.Bell += (_, _) => bells++;

        engine.Feed("\u001b]0;Ghostty tab\u0007\u0007");

        Assert.Equal("Ghostty tab", engine.Title);
        Assert.Contains("Ghostty tab", titles);
        Assert.Equal(1, bells);
    }

    [Fact]
    public void ScrollbackCanBeNavigatedAndSnapshotted()
    {
        using var engine = new GhosttyTerminalEngine(20, 3);
        engine.Feed("one\r\ntwo\r\nthree\r\nfour\r\nfive");

        Assert.True(engine.HistoryCount >= 2);
        engine.SetScrollOffset(engine.HistoryCount);

        Assert.Equal(engine.HistoryCount, engine.ScrollOffset);
        var snapshot = engine.CreateSnapshot(includeHistory: true).Buffer;
        Assert.Equal(engine.HistoryCount, snapshot.HistoryCount);
        Assert.Contains(snapshot.Lines, line =>
            string.Concat(line.Cells.Select(static cell => cell.Text)).TrimEnd() == "one");
    }

    [Fact]
    public void NativeScrollbackHonorsProfileLimit()
    {
        using var engine = new GhosttyTerminalEngine(20, 3, historySize: 0);
        engine.Feed("one\r\ntwo\r\nthree\r\nfour\r\nfive");

        Assert.Equal(0, engine.HistoryCount);
    }

    [Fact]
    public void ZeroScrollbackInvalidatesCoordinatesWhenViewportRowsAreDiscarded()
    {
        using var engine = new GhosttyTerminalEngine(20, 3, historySize: 0);
        var version = engine.Buffer.CoordinateVersion;

        engine.Feed("one\r\ntwo\r\nthree\r\nfour");

        Assert.True(engine.Buffer.CoordinateVersion > version);
    }

    [Fact]
    public void ProjectionPreservesBlankBackgroundAndWrappedRows()
    {
        using var engine = new GhosttyTerminalEngine(5, 3);
        var coordinateVersion = engine.Buffer.CoordinateVersion;

        engine.Feed("\u001b[41m\u001b[2J123456");

        var snapshot = engine.CreateSnapshot().Buffer;
        Assert.Equal(Microsoft.Terminal.Core.ColorKind.Rgb, snapshot.Lines[0].Cells[4].Attributes.Background.Kind);
        Assert.True(snapshot.Lines[0].Wrapped);
        Assert.Equal(coordinateVersion, engine.Buffer.CoordinateVersion);
        Assert.Equal(Microsoft.Terminal.Core.ShellIntegrationKind.None, snapshot.Lines[2].Cells[4].ShellIntegration);
    }

    [Fact]
    public void CursorIsHiddenWhenScrolledOutsideViewport()
    {
        using var engine = new GhosttyTerminalEngine(20, 3);
        engine.Feed("one\r\ntwo\r\nthree\r\nfour\r\nfive");

        engine.SetScrollOffset(engine.HistoryCount);

        Assert.False(engine.CursorVisible);
        Assert.Equal(4, engine.CursorX);
        Assert.Equal(2, engine.CursorY);
    }

    [Fact]
    public void SearchRevealScrollsTheNativeViewport()
    {
        using var engine = new GhosttyTerminalEngine(20, 3);
        using var search = new Microsoft.Terminal.Control.TerminalSearchSession(engine);
        engine.Feed("needle\r\ntwo\r\nthree\r\nfour\r\nfive");

        search.Update("needle");

        Assert.True(engine.ScrollOffset > 0);
    }

    [Fact]
    public void PositiveHistoryLimitIsNotConstrainedByGhosttyByteDefault()
    {
        using var engine = new GhosttyTerminalEngine(20, 3, historySize: 9001);
        engine.Feed(string.Concat(Enumerable.Repeat("line\r\n", 5000)));

        Assert.True(engine.HistoryCount > 4000);
    }

    [Fact]
    public void SemanticPromptCreatesOneMarkAtItsActualColumn()
    {
        using var engine = new GhosttyTerminalEngine(20, 3);
        engine.Feed("\u001b]133;A\u0007P> \u001b]133;B\u0007");

        var line = engine.CreateSnapshot().Buffer.Lines[0];

        var mark = Assert.Single(line.Marks);
        Assert.Equal(0, mark.StartColumn);
        Assert.Equal(Microsoft.Terminal.Core.ShellIntegrationKind.Prompt, line.Cells[0].ShellIntegration);
    }

    [Fact]
    public void HistoryEvictionAdvancesCoordinateVersion()
    {
        using var engine = new GhosttyTerminalEngine(20, 3, historySize: 10);
        engine.Feed(string.Concat(Enumerable.Repeat("line\r\n", 5000)));
        var version = engine.Buffer.CoordinateVersion;
        var history = engine.HistoryCount;

        engine.Feed(new string('x', 100_000));

        Assert.True(engine.Buffer.CoordinateVersion > version);
    }

    [Fact]
    public void Osc52ClipboardWriteHonorsPolicy()
    {
        using var engine = new GhosttyTerminalEngine(20, 3);
        string? clipboard = null;
        engine.ClipboardWriteRequested += (_, text) => clipboard = text;
        var payload = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("hello Ω"));

        engine.Feed($"\u001b]52;c;{payload}\u0007");
        Assert.Null(clipboard);

        engine.ConfigureOptionalFeatures(allowClipboardWrite: true, allowNotifications: false);
        engine.Feed($"\u001b]52;c;{payload}\u0007");
        Assert.Equal("hello Ω", clipboard);
    }

    [Fact]
    public void EmptyOsc52RequestsClipboardClear()
    {
        using var engine = new GhosttyTerminalEngine(20, 3);
        string? clipboard = null;
        engine.ConfigureOptionalFeatures(allowClipboardWrite: true, allowNotifications: false);
        engine.ClipboardWriteRequested += (_, text) => clipboard = text;

        engine.Feed("\u001b]52;c;\u0007");

        Assert.Equal(string.Empty, clipboard);
    }

    [Fact]
    public void DesktopNotificationHonorsPolicy()
    {
        using var engine = new GhosttyTerminalEngine(20, 3);
        Microsoft.Terminal.Core.TerminalNotification? notification = null;
        engine.NotificationRequested += (_, value) => notification = value;

        engine.Feed("\u001b]777;notify;Deploy;Completed\u0007");
        Assert.Null(notification);

        engine.ConfigureOptionalFeatures(allowClipboardWrite: false, allowNotifications: true);
        engine.Feed("\u001b]777;notify;Deploy;Completed\u0007");
        Assert.Equal("Deploy", notification?.Title);
        Assert.Equal("Completed", notification?.Body);
    }

    [Fact]
    public void HyperlinksAreProjectedIntoSharedCells()
    {
        using var engine = new GhosttyTerminalEngine(20, 3);

        engine.Feed("\u001b]8;;https://example.com\u001b\\link\u001b]8;;\u001b\\");

        var firstCell = engine.CreateSnapshot().Buffer.Lines[0].Cells[0];
        Assert.Equal("https://example.com", firstCell.HyperlinkUri);
    }

    [Fact]
    public void DeviceReportsAreReturnedToThePty()
    {
        using var engine = new GhosttyTerminalEngine(20, 3);
        byte[]? response = null;
        engine.ResponseReady += (_, value) => response = value;

        engine.Feed("\u001b[2;4H\u001b[6n");

        Assert.Equal("\u001b[2;4R", System.Text.Encoding.ASCII.GetString(response!));
    }

    [Fact]
    public void SizeReportsUseCurrentCellPixelGeometry()
    {
        using var engine = new GhosttyTerminalEngine(10, 4);
        var responses = new List<string>();
        engine.ResponseReady += (_, value) =>
            responses.Add(System.Text.Encoding.ASCII.GetString(value));
        engine.Resize(10, 4, 8, 16);

        engine.Feed("\u001b[14t\u001b[16t\u001b[18t");

        Assert.Contains("\u001b[4;64;80t", responses);
        Assert.Contains("\u001b[6;16;8t", responses);
        Assert.Contains("\u001b[8;4;10t", responses);
    }

    [Fact]
    public void PlainOutputDoesNotEmitFalseMetadataChanges()
    {
        using var engine = new GhosttyTerminalEngine(20, 3);
        var titleChanges = 0;
        var workingDirectoryChanges = 0;
        engine.TitleChanged += (_, _) => titleChanges++;
        engine.WorkingDirectoryChanged += (_, _) => workingDirectoryChanges++;

        engine.Feed("ordinary output");

        Assert.Equal(0, titleChanges);
        Assert.Equal(0, workingDirectoryChanges);
    }

    [Fact]
    public void FirstLargeFeedEvictionAdvancesCoordinateVersion()
    {
        using var engine = new GhosttyTerminalEngine(20, 3, historySize: 10);
        var version = engine.Buffer.CoordinateVersion;

        engine.Feed(new string('x', 100_000));

        Assert.True(engine.Buffer.CoordinateVersion > version);
    }

    [Fact]
    public void FirstControlSequenceScrollEvictionAdvancesCoordinateVersion()
    {
        using var engine = new GhosttyTerminalEngine(20, 3, historySize: 10);
        var version = engine.Buffer.CoordinateVersion;

        engine.Feed(string.Concat(Enumerable.Repeat("\u001bD", 5000)));

        Assert.True(engine.Buffer.CoordinateVersion > version);
    }

    [Fact]
    public void FirstLineFeedEvictionAtLimitAdvancesCoordinateVersion()
    {
        using var engine = new GhosttyTerminalEngine(20, 3, historySize: 5000);
        var version = engine.Buffer.CoordinateVersion;

        engine.Feed(string.Concat(Enumerable.Repeat("\n", 10_000)));

        Assert.True(engine.Buffer.CoordinateVersion > version);
    }

    [Theory]
    [MemberData(nameof(SharedVtCorpus))]
    public void SharedCorpusMatchesBuiltInProjection(string sequence)
    {
        using var builtIn = new Microsoft.Terminal.Core.TerminalEngine(40, 5);
        using var ghostty = new GhosttyTerminalEngine(40, 5);

        builtIn.Feed(sequence);
        ghostty.Feed(sequence);

        var expected = builtIn.CreateSnapshot().Buffer;
        var actual = ghostty.CreateSnapshot().Buffer;
        Assert.Equal(expected.CursorX, actual.CursorX);
        Assert.Equal(expected.CursorY, actual.CursorY);
        Assert.Equal(
            Enumerable.Range(0, expected.Rows).Select(row => LineText(expected, row)),
            Enumerable.Range(0, actual.Rows).Select(row => LineText(actual, row)));
        Assert.Equal(builtIn.BracketedPaste, ghostty.BracketedPaste);
        Assert.Equal(builtIn.AlternateBufferActive, ghostty.AlternateBufferActive);
    }

    private static string LineText(Microsoft.Terminal.Core.TextBufferSnapshot snapshot, int row) =>
        string.Concat(snapshot.Lines[row].Cells.Select(static cell => cell.Text)).TrimEnd();
}
