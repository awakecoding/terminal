using Microsoft.Terminal.Core;
using Microsoft.Terminal.Settings;

namespace Microsoft.Terminal.Control;

public enum TerminalSelectionMode
{
    Linear,
    Block,
    Word,
    Line,
    Command,
    Output,
}

public enum TerminalSelectionEndpoint
{
    Anchor,
    Active,
}

public enum TerminalShellSelectionDirection
{
    Previous,
    Next,
}

public enum TerminalControlSequencePolicy
{
    Strip,
    Preserve,
}

[Flags]
public enum TerminalPasteWarning
{
    None = 0,
    Large = 1,
    MultiLine = 2,
}

public enum TerminalPasteResult
{
    Empty,
    Written,
    ConfirmationRequired,
    Cancelled,
    NoConnection,
}

public enum TerminalScrollMarkKind
{
    User,
    Prompt,
    CommandSuccess,
    CommandError,
    SearchMatch,
    CurrentSearchMatch,
}

public readonly record struct TerminalSelectionPoint(int Column, int Line);

public sealed record TerminalSelection(
    TerminalSelectionPoint Anchor,
    TerminalSelectionPoint Active,
    TerminalSelectionMode Mode = TerminalSelectionMode.Linear,
    TerminalSelectionEndpoint ActiveEndpoint = TerminalSelectionEndpoint.Active);

public sealed record TerminalCopyOptions
{
    public bool SingleLine { get; init; }
    public bool TrimBlockSelection { get; init; } = true;
    public CopyFormat Formats { get; init; } = CopyFormat.None;
    public TerminalControlSequencePolicy ControlSequencePolicy { get; init; } =
        TerminalControlSequencePolicy.Strip;
}

public sealed record TerminalClipboardPayload(string Text, string? Html, string? Rtf);

public sealed record TerminalPasteOptions
{
    public bool TrimWhitespace { get; init; } = true;
    public bool WarnAboutLargePaste { get; init; } = true;
    public string WarnAboutMultiLinePaste { get; init; } = "automatic";
    public int LargePasteThreshold { get; init; } = 5 * 1024;
}

public sealed record TerminalPasteRequest(
    string Text,
    TerminalPasteWarning Warning,
    int CharacterCount,
    int LineCount,
    bool BracketedPaste = false)
{
    public bool RequiresConfirmation => Warning != TerminalPasteWarning.None;
}

public sealed class TerminalPasteWarningEventArgs(TerminalPasteRequest request) : EventArgs
{
    public TerminalPasteRequest Request { get; } = request;
    public bool Allow { get; set; }
}

public sealed record TerminalHyperlinkContext(
    string Uri,
    string Text,
    TerminalSelectionPoint Start,
    TerminalSelectionPoint End,
    bool CanOpen);

public sealed class TerminalHyperlinkEventArgs(TerminalHyperlinkContext hyperlink) : EventArgs
{
    public TerminalHyperlinkContext Hyperlink { get; } = hyperlink;
    public bool Handled { get; set; }
}

public sealed record TerminalScrollMark(
    int Line,
    double Position,
    TerminalScrollMarkKind Kind,
    uint? ExitCode = null,
    string? Color = null);

public sealed record TerminalInteractionOptions
{
    public string WordDelimiters { get; init; } = " /\\()\"'-.,:;<>~!@#$%^&*|+=[]{}~?\u2502";
    public TerminalCopyOptions Copy { get; init; } = new();
    public TerminalPasteOptions Paste { get; init; } = new();
    public IReadOnlySet<string> SafeUriSchemes { get; init; } =
        new HashSet<string>(["http", "https", "mailto"], StringComparer.OrdinalIgnoreCase);
    public bool CopyOnSelect { get; init; }
    public bool ScrollToZoom { get; init; } = true;

    public static TerminalInteractionOptions FromSettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var schemes = settings.SafeUriSchemes.Count == 0
            ? new HashSet<string>(["http", "https", "mailto"], StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(settings.SafeUriSchemes, StringComparer.OrdinalIgnoreCase);
        return new TerminalInteractionOptions
        {
            WordDelimiters = settings.WordDelimiters,
            CopyOnSelect = settings.CopyOnSelect,
            ScrollToZoom = settings.ScrollToZoom,
            SafeUriSchemes = schemes,
            Copy = new TerminalCopyOptions
            {
                TrimBlockSelection = settings.TrimBlockSelection,
                Formats = settings.CopyFormatting
                    ? settings.CopyFormatFormats == CopyFormat.None ? CopyFormat.All : settings.CopyFormatFormats
                    : CopyFormat.None,
            },
            Paste = new TerminalPasteOptions
            {
                TrimWhitespace = settings.TrimPaste,
                WarnAboutLargePaste = settings.WarnAboutLargePaste,
                WarnAboutMultiLinePaste = settings.WarnAboutMultiLinePaste,
            },
        };
    }
}

public sealed record TerminalAccessibleState(
    string Name,
    bool IsRunning,
    bool IsReadOnly,
    int LineCount,
    TerminalTextRange DocumentRange,
    TerminalTextRange SelectionRange,
    TerminalTextRange CaretRange);

public readonly record struct TerminalTextRange(int Start, int End)
{
    public int Length => Math.Max(0, End - Start);
    public bool IsDegenerate => Start == End;

    public TerminalTextRange Normalize(int documentLength)
    {
        var start = Math.Clamp(Math.Min(Start, End), 0, documentLength);
        var end = Math.Clamp(Math.Max(Start, End), start, documentLength);
        return new TerminalTextRange(start, end);
    }
}

public sealed class TerminalInteractionErrorEventArgs(
    string operation,
    Exception exception) : EventArgs
{
    public string Operation { get; } = operation;
    public Exception Exception { get; } = exception;
}

internal readonly record struct NormalizedSelection(
    TerminalSelectionPoint Start,
    TerminalSelectionPoint End,
    TerminalSelectionMode Mode);
