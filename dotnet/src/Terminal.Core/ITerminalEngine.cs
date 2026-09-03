namespace Microsoft.Terminal.Core;

[Flags]
public enum TerminalEngineCapabilities
{
    None = 0,
    UnicodeGraphemeClusters = 1 << 0,
    RowRendition = 1 << 1,
    Vt52Keyboard = 1 << 2,
    DrcsGlyphs = 1 << 3,
    KittyKeyboard = 1 << 4,
    ModifyOtherKeys = 1 << 5,
    Win32Input = 1 << 6,
    SixelImages = 1 << 7,
    Iterm2Images = 1 << 8,
    ConEmuImages = 1 << 9,
}

public sealed record TerminalEngineDiagnostic(string Code, string Message);

[Flags]
public enum KittyKeyboardFlags
{
    None = 0,
    DisambiguateEscapeCodes = 1 << 0,
    ReportEventTypes = 1 << 1,
    ReportAlternateKeys = 1 << 2,
    ReportAllKeysAsEscapeCodes = 1 << 3,
    ReportAssociatedText = 1 << 4,
}

public readonly record struct TerminalInputMode(
    bool AnsiMode,
    bool ApplicationCursorKeys,
    bool ApplicationKeypad,
    KittyKeyboardFlags KittyFlags,
    int ModifyOtherKeys,
    bool Win32InputMode);

public interface ITerminalEngine : IDisposable
{
    TerminalEngineCapabilities Capabilities { get; }
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
    TerminalInputMode InputMode { get; }
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
    event EventHandler<TerminalEngineDiagnostic>? Diagnostic;
    event EventHandler? Bell;
    event EventHandler<byte[]>? ResponseReady;

    void Feed(ReadOnlySpan<byte> data);
    void Feed(string text);
    void Resize(int columns, int rows, double cellWidth = 1, double cellHeight = 1);
    void Reset();
    void SetScrollOffset(int offset);
    void ConfigureOptionalFeatures(
        bool allowClipboardWrite,
        bool allowNotifications,
        bool allowKittyKeyboard = true);
    TerminalSnapshot CreateSnapshot(bool includeHistory = false);
    string WrapPaste(string text);
}
