namespace Microsoft.Terminal.Core;

public interface ITerminalEngine : IDisposable
{
    TextBuffer Buffer { get; }
    ColorScheme Scheme { get; set; }
    string Title { get; }
    string? WorkingDirectory { get; }
    bool AlternateBufferActive { get; }
    bool CursorVisible { get; }
    bool CursorBlinking { get; }
    bool ApplicationCursorKeys { get; }
    bool BracketedPaste { get; }
    bool MouseTracking { get; }
    TerminalMouseTrackingMode MouseTrackingMode { get; }
    bool SgrMouse { get; }
    bool FocusTracking { get; }
    bool AutoWrap { get; }
    bool InsertMode { get; }
    bool ReverseVideo { get; }
    int Columns { get; }
    int Rows { get; }
    int CursorX { get; }
    int CursorY { get; }
    int HistoryCount { get; }
    int ScrollOffset { get; }

    event EventHandler? Invalidated;
    event EventHandler<string>? TitleChanged;
    event EventHandler<string?>? WorkingDirectoryChanged;
    event EventHandler? ShellIntegrationChanged;
    event EventHandler<string>? ClipboardWriteRequested;
    event EventHandler<TerminalNotification>? NotificationRequested;
    event EventHandler? Bell;
    event EventHandler<byte[]>? ResponseReady;

    void Feed(ReadOnlySpan<byte> data);
    void Feed(string text);
    void Resize(int columns, int rows, double cellWidth = 1, double cellHeight = 1);
    void Reset();
    void SetScrollOffset(int offset);
    void ConfigureOptionalFeatures(bool allowClipboardWrite, bool allowNotifications);
    TerminalSnapshot CreateSnapshot(bool includeHistory = false);
    string WrapPaste(string text);
}
