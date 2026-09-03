using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Devolutions.Terminal.Ghostty;

internal static unsafe partial class GhosttyNative
{
    private const string LibraryName = "ghostty-vt";

    [LibraryImport(LibraryName, EntryPoint = "ghostty_terminal_new")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial GhosttyResult TerminalNew(
        nint allocator,
        out nint terminal,
        ushort columns,
        ushort rows);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_terminal_free")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void TerminalFree(nint terminal);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_terminal_vt_write")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void TerminalWrite(
        nint terminal,
        byte* bytes,
        nuint length);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_terminal_resize")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial GhosttyResult TerminalResize(
        nint terminal,
        ushort columns,
        ushort rows,
        uint cellWidth,
        uint cellHeight);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_terminal_reset")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void TerminalReset(nint terminal);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_terminal_get")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial GhosttyResult TerminalGet(
        nint terminal,
        int data,
        void* output);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_terminal_set")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial GhosttyResult TerminalSet(
        nint terminal,
        int option,
        void* value);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_terminal_scroll_viewport")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void TerminalScrollViewport(
        nint terminal,
        GhosttyScrollViewport viewport);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_terminal_grid_ref")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial GhosttyResult TerminalGridRef(
        nint terminal,
        GhosttyPoint point,
        GhosttyGridRef* gridRef);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_terminal_grid_ref_track")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial GhosttyResult TerminalGridRefTrack(
        nint terminal,
        GhosttyPoint point,
        out nint trackedGridRef);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_tracked_grid_ref_free")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void TrackedGridRefFree(nint trackedGridRef);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_tracked_grid_ref_has_value")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool TrackedGridRefHasValue(nint trackedGridRef);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_grid_ref_hyperlink_uri")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial GhosttyResult GridRefHyperlinkUri(
        GhosttyGridRef* gridRef,
        byte* buffer,
        nuint bufferLength,
        nuint* written);


    [LibraryImport(LibraryName, EntryPoint = "ghostty_render_state_new")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial GhosttyResult RenderStateNew(
        nint allocator,
        out nint state);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_render_state_free")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void RenderStateFree(nint state);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_render_state_update")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial GhosttyResult RenderStateUpdate(
        nint state,
        nint terminal);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_render_state_get")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial GhosttyResult RenderStateGet(
        nint state,
        int data,
        void* output);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_render_state_row_iterator_new")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial GhosttyResult RowIteratorNew(
        nint allocator,
        out nint iterator);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_render_state_row_iterator_free")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void RowIteratorFree(nint iterator);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_render_state_row_iterator_next")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool RowIteratorNext(nint iterator);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_render_state_row_get")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial GhosttyResult RowGet(
        nint iterator,
        int data,
        void* output);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_render_state_row_cells_new")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial GhosttyResult RowCellsNew(
        nint allocator,
        out nint cells);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_render_state_row_cells_free")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void RowCellsFree(nint cells);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_render_state_row_cells_next")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool RowCellsNext(nint cells);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_render_state_row_cells_get")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial GhosttyResult RowCellsGet(
        nint cells,
        int data,
        void* output);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_cell_get")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial GhosttyResult CellGet(
        ulong cell,
        int data,
        void* output);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_row_get")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial GhosttyResult RawRowGet(
        ulong row,
        int data,
        void* output);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_type_json")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint TypeJson();
}

internal sealed class SafeGhosttyTerminalHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal SafeGhosttyTerminalHandle(nint value) : base(true)
    {
        SetHandle(value);
    }

    protected override bool ReleaseHandle()
    {
        GhosttyNative.TerminalFree(handle);
        return true;
    }
}

internal sealed class SafeGhosttyRenderStateHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal SafeGhosttyRenderStateHandle(nint value) : base(true)
    {
        SetHandle(value);
    }

    protected override bool ReleaseHandle()
    {
        GhosttyNative.RenderStateFree(handle);
        return true;
    }
}

internal sealed class SafeGhosttyRowIteratorHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal SafeGhosttyRowIteratorHandle(nint value) : base(true)
    {
        SetHandle(value);
    }

    protected override bool ReleaseHandle()
    {
        GhosttyNative.RowIteratorFree(handle);
        return true;
    }
}

internal sealed class SafeGhosttyRowCellsHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal SafeGhosttyRowCellsHandle(nint value) : base(true)
    {
        SetHandle(value);
    }

    protected override bool ReleaseHandle()
    {
        GhosttyNative.RowCellsFree(handle);
        return true;
    }
}

internal sealed class SafeGhosttyTrackedGridRefHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal SafeGhosttyTrackedGridRefHandle(nint value) : base(true)
    {
        SetHandle(value);
    }

    protected override bool ReleaseHandle()
    {
        GhosttyNative.TrackedGridRefFree(handle);
        return true;
    }
}
