using Microsoft.Terminal.Core;
using SkiaSharp;

namespace Microsoft.Terminal.Render;

public readonly record struct CellSize(double Width, double Height);

public readonly record struct RenderViewport(int Columns, int Rows, double Scale);

public enum TerminalCursorStyle : byte
{
    Bar,
    Underscore,
    DoubleUnderscore,
    Vintage,
    FilledBox,
    EmptyBox,
}

public readonly record struct ResolvedCellAttributes(
    uint Foreground,
    uint Background,
    CellFlags Flags,
    string? HyperlinkUri);

public readonly record struct TerminalTextCluster(
    int TextOffset,
    int TextLength,
    int StartColumn,
    int CellCount);

public sealed record TerminalRenderRun(
    int StartColumn,
    int CellCount,
    string Text,
    ResolvedCellAttributes Attributes,
    IReadOnlyList<TerminalTextCluster> Clusters);

public sealed record TerminalRenderRow(
    int RowIndex,
    IReadOnlyList<TerminalRenderRun> Runs);

public sealed record TerminalRenderFrame(
    int Columns,
    int Rows,
    int CursorX,
    int CursorY,
    bool CursorVisible,
    uint Background,
    uint CursorColor,
    uint SelectionColor,
    IReadOnlyList<TerminalRenderRow> RowsData)
{
    public TerminalCursorStyle CursorStyle { get; init; } = TerminalCursorStyle.Bar;
    public int CursorHeightPercentage { get; init; } = 25;
    public IReadOnlyList<TerminalImageOverlay> Images { get; init; } = [];
}

public readonly record struct TerminalCellRange(
    int Row,
    int StartColumn,
    int EndColumn,
    uint Color);

public sealed record TerminalRenderOverlays(
    IReadOnlyList<TerminalCellRange> Selection,
    IReadOnlyList<TerminalCellRange> Search,
    IReadOnlyList<TerminalCellRange> Hyperlink)
{
    public static TerminalRenderOverlays Empty { get; } = new([], [], []);
}

public sealed record TerminalRenderOptions
{
    public TerminalCursorStyle CursorStyle { get; init; } = TerminalCursorStyle.Bar;
    public int CursorHeightPercentage { get; init; } = 25;
}

public sealed record TerminalRendererSettings
{
    public string FontFamily { get; init; } = "Cascadia Mono";
    public float FontSize { get; init; } = 12;
    public int FontWeight { get; init; } = 400;
    public IReadOnlyList<string> FallbackFontFamilies { get; init; } =
        ["Cascadia Mono", "Consolas", "Segoe UI Emoji"];
    public IReadOnlyList<TerminalFontSource> FontSources { get; init; } = [];
    public int GlyphCacheCapacity { get; init; } = 4096;
}

public sealed record TerminalFontSource(
    string FamilyName,
    bool Italic,
    Func<Stream> OpenStream);

public readonly record struct GlyphCacheStatistics(
    int Count,
    int Capacity,
    long Hits,
    long Misses,
    long Evictions);

public interface ITerminalRenderer
{
    CellSize CellSize { get; }

    void Resize(RenderViewport viewport);

    void Invalidate();

    void Render(
        SKCanvas canvas,
        TerminalRenderFrame frame,
        TerminalRenderOverlays overlays,
        SKRect bounds,
        float padding,
        bool drawCursor);
}
