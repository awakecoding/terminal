using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Microsoft.Terminal.Render;

namespace Microsoft.Terminal.Control;

internal sealed class TerminalSkiaDrawOperation : ICustomDrawOperation
{
    private readonly SkiaTerminalRenderer _renderer;
    private readonly TerminalRenderFrame _frame;
    private readonly TerminalRenderOverlays _overlays;
    private readonly float _padding;
    private readonly bool _drawCursor;
    private readonly int _resourceGeneration;

    public TerminalSkiaDrawOperation(
        Rect bounds,
        SkiaTerminalRenderer renderer,
        TerminalRenderFrame frame,
        TerminalRenderOverlays overlays,
        float padding,
        bool drawCursor)
    {
        Bounds = bounds;
        _renderer = renderer;
        _frame = frame;
        _overlays = overlays;
        _padding = padding;
        _drawCursor = drawCursor;
        _resourceGeneration = renderer.ResourceGeneration;
    }

    public Rect Bounds { get; }

    public void Render(ImmediateDrawingContext context)
    {
        var feature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
        if (feature is null)
        {
            return;
        }

        using var lease = feature.Lease();
        _renderer.Render(
            lease.SkCanvas,
            _frame,
            _overlays,
            new SkiaSharp.SKRect(0, 0, (float)Bounds.Width, (float)Bounds.Height),
            _padding,
            _drawCursor);
    }

    public bool HitTest(Point point) => Bounds.Contains(point);

    public bool Equals(ICustomDrawOperation? other)
    {
        if (other is not TerminalSkiaDrawOperation operation ||
            Bounds != operation.Bounds ||
            _drawCursor != operation._drawCursor ||
            _resourceGeneration != operation._resourceGeneration ||
            TerminalFrameDiffer.GetDirtyRows(_frame, operation._frame).Count != 0)
        {
            return false;
        }

        return RangesEqual(_overlays.Selection, operation._overlays.Selection) &&
               RangesEqual(_overlays.Search, operation._overlays.Search) &&
               RangesEqual(_overlays.Hyperlink, operation._overlays.Hyperlink) &&
               _overlays.Composition == operation._overlays.Composition;
    }

    public void Dispose()
    {
        // The control owns the renderer and immutable frame data.
    }

    private static bool RangesEqual(
        IReadOnlyList<TerminalCellRange> left,
        IReadOnlyList<TerminalCellRange> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            if (left[index] != right[index])
            {
                return false;
            }
        }

        return true;
    }
}
