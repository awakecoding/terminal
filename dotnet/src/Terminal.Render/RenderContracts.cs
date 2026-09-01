using Microsoft.Terminal.Core;

namespace Microsoft.Terminal.Render;

public readonly record struct CellSize(double Width, double Height);

public readonly record struct RenderViewport(int Columns, int Rows, double Scale);

public readonly record struct ResolvedCellAttributes(
    uint Foreground,
    uint Background,
    CellFlags Flags,
    string? HyperlinkUri);

public sealed record TerminalRenderRun(
    int StartColumn,
    int CellCount,
    string Text,
    ResolvedCellAttributes Attributes);

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
    IReadOnlyList<TerminalRenderRow> RowsData);

public interface ITerminalRenderer
{
    void Resize(RenderViewport viewport);

    void Invalidate();
}
