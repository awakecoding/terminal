using System.Text;
using HarfBuzzBuffer = HarfBuzzSharp.Buffer;
using Microsoft.Terminal.Core;
using SkiaSharp;
using SkiaSharp.HarfBuzz;

namespace Microsoft.Terminal.Render;

public sealed class SkiaTerminalRenderer : ITerminalRenderer, IDisposable
{
    private readonly object _gate = new();
    private readonly SKPaint _paint = new() { IsAntialias = true };
    private readonly SKPaint _strokePaint = new()
    {
        IsAntialias = false,
        Style = SKPaintStyle.Stroke,
    };
    private readonly SKPath _powerlineRight = CreatePowerlinePath(pointsRight: true);
    private readonly SKPath _powerlineLeft = CreatePowerlinePath(pointsRight: false);
    private TerminalRendererSettings _settings;
    private FontResolver _fonts;
    private BoundedResourceCache<GlyphKey, CachedGlyph> _glyphs;
    private readonly Dictionary<long, CachedImage> _images = [];
    private readonly HashSet<long> _invalidImages = [];
    private long _imageBytes;
    private readonly Func<GlyphKey, CachedGlyph> _shapeFactory;
    private RenderViewport _viewport;
    private float _baseline;
    private bool _disposed;

    public SkiaTerminalRenderer(TerminalRendererSettings? settings = null)
    {
        _settings = Normalize(settings ?? new TerminalRendererSettings());
        _fonts = new FontResolver(_settings);
        _glyphs = CreateGlyphCache();
        _shapeFactory = Shape;
        MeasureCell();
    }

    public CellSize CellSize { get; private set; }

    public GlyphCacheStatistics CacheStatistics
    {
        get
        {
            lock (_gate)
            {
                return _glyphs.Statistics;
            }
        }
    }

    public int ResourceGeneration { get; private set; }

    public string? LastResolvedFontFamily { get; private set; }

    public void Configure(TerminalRendererSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var normalized = Normalize(settings);
            if (normalized == _settings)
            {
                return;
            }

            ReleaseResources();
            _settings = normalized;
            _fonts = new FontResolver(_settings);
            _glyphs = CreateGlyphCache();
            MeasureCell();
            ResourceGeneration++;
        }
    }

    public void Resize(RenderViewport viewport)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var scale = Math.Max(0.1, viewport.Scale);
            var normalized = viewport with { Scale = scale };
            if (Math.Abs(_viewport.Scale - normalized.Scale) > 0.001)
            {
                _glyphs.Clear();
                ResourceGeneration++;
            }

            _viewport = normalized;
        }
    }

    public void Invalidate()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _glyphs.Clear();
            ResourceGeneration++;
        }
    }

    public void Render(
        SKCanvas canvas,
        TerminalRenderFrame frame,
        TerminalRenderOverlays overlays,
        SKRect bounds,
        float padding,
        bool drawCursor)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(overlays);
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _paint.Style = SKPaintStyle.Fill;
            _paint.Color = ToColor(frame.Background);
            canvas.DrawRect(bounds, _paint);
            DrawImages(canvas, frame, bounds, padding);

            for (var rowIndex = 0; rowIndex < frame.RowsData.Count; rowIndex++)
            {
                DrawRow(canvas, frame, frame.RowsData[rowIndex], padding);
            }

            DrawRanges(canvas, overlays.Selection, padding);
            DrawRanges(canvas, overlays.Search, padding);
            DrawRanges(canvas, overlays.Hyperlink, padding);
            DrawComposition(canvas, overlays.Composition, padding);

            if (drawCursor && frame.CursorVisible)
            {
                DrawCursor(canvas, frame, padding);
            }
        }
    }

    private void DrawComposition(
        SKCanvas canvas,
        TerminalCompositionOverlay? composition,
        float padding)
    {
        if (composition is null || string.IsNullOrEmpty(composition.Text))
        {
            return;
        }

        var left = padding + (composition.Column * (float)CellSize.Width);
        var top = padding + (composition.Row * (float)CellSize.Height);
        _paint.Color = SKColors.White;
        _paint.TextSize = _settings.FontSize * (float)Math.Max(0.1, _viewport.Scale);
        _paint.Typeface = _fonts.Resolve(composition.Text.AsSpan(), CellFlags.None);
        canvas.DrawText(composition.Text, left, top + _baseline, _paint);

        _strokePaint.Color = SKColors.White;
        _strokePaint.StrokeWidth = Math.Max(1, (float)_viewport.Scale);
        var width = Math.Max(
            (float)CellSize.Width,
            DisplayWidth(composition.Text) * (float)CellSize.Width);
        canvas.DrawLine(left, top + (float)CellSize.Height - 1, left + width, top + (float)CellSize.Height - 1, _strokePaint);

        if (composition.CursorOffset is { } cursor)
        {
            var cursorLeft = left + (DisplayWidth(composition.Text, cursor) * (float)CellSize.Width);
            canvas.DrawLine(cursorLeft, top, cursorLeft, top + (float)CellSize.Height, _strokePaint);
        }
    }

    private static int DisplayWidth(string text, int utf16Limit = int.MaxValue)
    {
        var width = 0;
        var consumed = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (consumed + rune.Utf16SequenceLength > utf16Limit)
            {
                break;
            }

            width += Math.Max(0, WcWidth.Width(rune));
            consumed += rune.Utf16SequenceLength;
        }

        return width;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            ReleaseResources();
            _paint.Dispose();
            _strokePaint.Dispose();
            _powerlineRight.Dispose();
            _powerlineLeft.Dispose();
            _disposed = true;
        }
    }

    private void DrawRow(
        SKCanvas canvas,
        TerminalRenderFrame frame,
        TerminalRenderRow row,
        float padding)
    {
        var top = padding + ((float)row.RowIndex * (float)CellSize.Height);
        for (var runIndex = 0; runIndex < row.Runs.Count; runIndex++)
        {
            var run = row.Runs[runIndex];
            var runLeft = padding + (run.StartColumn * (float)CellSize.Width);
            var runWidth = run.CellCount * (float)CellSize.Width;
            if (run.Attributes.Background != frame.Background)
            {
                _paint.Color = ToColor(run.Attributes.Background);
                canvas.DrawRect(runLeft, top, runWidth, (float)CellSize.Height, _paint);
            }

            if ((run.Attributes.Flags & CellFlags.Invisible) == 0)
            {
                DrawClusters(canvas, run, top, padding);
            }

            DrawDecorations(canvas, run, top, padding);
        }
    }

    private void DrawImages(
        SKCanvas canvas,
        TerminalRenderFrame frame,
        SKRect bounds,
        float padding)
    {
        if (frame.Images.Count == 0)
        {
            if (_images.Count > 0)
            {
                foreach (var image in _images.Values)
                {
                    image.Dispose();
                }

                _images.Clear();
                _imageBytes = 0;
            }

            return;
        }

        var activeIds = frame.Images.Select(static image => image.Id).ToHashSet();
        foreach (var staleId in _images.Keys.Where(id => !activeIds.Contains(id)).ToArray())
        {
            _imageBytes -= _images[staleId].ByteSize;
            _images[staleId].Dispose();
            _images.Remove(staleId);
        }

        _invalidImages.RemoveWhere(id => !activeIds.Contains(id));
        var viewport = new SKRect(
            bounds.Left + padding,
            bounds.Top + padding,
            Math.Max(bounds.Left + padding, bounds.Right - padding),
            Math.Max(bounds.Top + padding, bounds.Bottom - padding));
        canvas.Save();
        canvas.ClipRect(viewport);
        _paint.Color = SKColors.White;
        foreach (var image in frame.Images)
        {
            if (_invalidImages.Contains(image.Id))
            {
                continue;
            }

            var left = padding + (image.AnchorColumn * (float)CellSize.Width);
            var top = padding + (image.AnchorRow * (float)CellSize.Height);
            if (left >= viewport.Right || top >= viewport.Bottom)
            {
                continue;
            }

            if (!_images.TryGetValue(image.Id, out var cached))
            {
                var decoded = DecodeImage(image);
                if (decoded is null)
                {
                    _invalidImages.Add(image.Id);
                    continue;
                }

                var byteSize = checked((long)decoded.RowBytes * decoded.Height);
                if (byteSize > _settings.DecodedImageCacheByteCapacity)
                {
                    decoded.Dispose();
                    _invalidImages.Add(image.Id);
                    continue;
                }

                while (_images.Count > 0 &&
                       _imageBytes + byteSize > _settings.DecodedImageCacheByteCapacity)
                {
                    var oldest = _images.First();
                    oldest.Value.Dispose();
                    _imageBytes -= oldest.Value.ByteSize;
                    _images.Remove(oldest.Key);
                }

                cached = new CachedImage(decoded, byteSize);
                _images.Add(image.Id, cached);
                _imageBytes += byteSize;
            }

            var destination = ImageDestination(image, cached.Bitmap, left, top, viewport);
            canvas.DrawBitmap(cached.Bitmap, destination, _paint);
        }

        canvas.Restore();
    }

    private static SKBitmap? DecodeImage(TerminalImageOverlay image)
    {
        if (image.Sixel is { } sixel)
        {
            var bitmap = new SKBitmap(
                sixel.Width,
                sixel.Height,
                SKColorType.Bgra8888,
                SKAlphaType.Premul);
            var indices = sixel.PixelIndices.Span;
            var palette = sixel.Palette.Span;
            var colors = new SKColor[indices.Length];
            for (var index = 0; index < colors.Length; index++)
            {
                var colorIndex = indices[index];
                colors[index] = colorIndex == SixelImage.TransparentColorIndex
                    ? SKColors.Transparent
                    : new SKColor(palette[colorIndex]);
            }

            bitmap.Pixels = colors;
            return bitmap;
        }

        if (image.InlineImage is not { } inline)
        {
            return null;
        }

        using var data = SKData.CreateCopy(inline.Data.ToArray());
        using var codec = SKCodec.Create(data);
        if (codec is null ||
            codec.Info.Width is <= 0 or > TerminalImageLimits.MaximumPixelDimension ||
            codec.Info.Height is <= 0 or > TerminalImageLimits.MaximumPixelDimension ||
            (long)codec.Info.Width * codec.Info.Height > TerminalImageLimits.MaximumPixelCount)
        {
            return null;
        }

        var decoded = new SKBitmap(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var decodeResult = codec.GetPixels(decoded.Info, decoded.GetPixels());
        if (decodeResult is SKCodecResult.Success or SKCodecResult.IncompleteInput)
        {
            return decoded;
        }

        decoded.Dispose();
        return null;
    }

    private SKRect ImageDestination(
        TerminalImageOverlay image,
        SKBitmap bitmap,
        float left,
        float top,
        SKRect bounds)
    {
        var naturalWidth = (float)bitmap.Width;
        var naturalHeight = (float)bitmap.Height;
        if (image.InlineImage is not { } inline)
        {
            return SKRect.Create(left, top, naturalWidth, naturalHeight);
        }

        var width = ResolveDimension(
            inline.Metadata.Width,
            naturalWidth,
            (float)CellSize.Width,
            bounds.Width);
        var height = ResolveDimension(
            inline.Metadata.Height,
            naturalHeight,
            (float)CellSize.Height,
            bounds.Height);
        if (inline.Metadata.PreserveAspectRatio)
        {
            var scale = Math.Min(width / naturalWidth, height / naturalHeight);
            if (inline.Metadata.Width.Kind == TerminalImageDimensionKind.Auto)
            {
                width = naturalWidth * (height / naturalHeight);
            }
            else if (inline.Metadata.Height.Kind == TerminalImageDimensionKind.Auto)
            {
                height = naturalHeight * (width / naturalWidth);
            }
            else
            {
                width = naturalWidth * scale;
                height = naturalHeight * scale;
            }
        }

        return SKRect.Create(left, top, width, height);
    }

    private static float ResolveDimension(
        TerminalImageDimension dimension,
        float natural,
        float cell,
        float available) =>
        dimension.Kind switch
        {
            TerminalImageDimensionKind.Cells => (float)dimension.Value * cell,
            TerminalImageDimensionKind.Pixels => (float)dimension.Value,
            TerminalImageDimensionKind.Percent => (float)(dimension.Value / 100) * available,
            _ => natural,
        };

    private void DrawClusters(
        SKCanvas canvas,
        TerminalRenderRun run,
        float top,
        float padding,
        uint? foregroundOverride = null)
    {
        _paint.Color = ToColor(foregroundOverride ?? run.Attributes.Foreground);
        for (var clusterIndex = 0; clusterIndex < run.Clusters.Count; clusterIndex++)
        {
            var cluster = run.Clusters[clusterIndex];
            if (IsWhitespace(run.Text.AsSpan(cluster.TextOffset, cluster.TextLength)))
            {
                continue;
            }

            var cellLeft = padding + (cluster.StartColumn * (float)CellSize.Width);
            var cellWidth = cluster.CellCount * (float)CellSize.Width;
            if (TryDrawPowerline(
                canvas,
                run.Text.AsSpan(cluster.TextOffset, cluster.TextLength),
                cellLeft,
                top,
                cellWidth))
            {
                continue;
            }

            var key = new GlyphKey(
                run.Text,
                cluster.TextOffset,
                cluster.TextLength,
                run.Attributes.Flags & (CellFlags.Bold | CellFlags.Italic),
                _settings.FontSize,
                _viewport.Scale);
            var glyph = _glyphs.GetOrAdd(key, _shapeFactory);
            var centered = Math.Max(0, (cellWidth - glyph.Width) * 0.5f);
            canvas.DrawText(glyph.Blob, cellLeft + centered, top + _baseline, _paint);
        }
    }

    private bool TryDrawPowerline(
        SKCanvas canvas,
        ReadOnlySpan<char> text,
        float left,
        float top,
        float width)
    {
        Rune.DecodeFromUtf16(text, out var rune, out var consumed);
        if (consumed != text.Length)
        {
            return false;
        }

        var right = left + width;
        var bottom = top + (float)CellSize.Height;
        var middle = top + ((float)CellSize.Height * 0.5f);
        switch (rune.Value)
        {
            case 0xE0A0:
                DrawPowerlineBranch(canvas, left, top, width, (float)CellSize.Height);
                return true;
            case 0xE0A1:
                DrawPowerlineLineNumber(canvas, left, top, width, (float)CellSize.Height);
                return true;
            case 0xE0A2:
                DrawPowerlineLock(canvas, left, top, width, (float)CellSize.Height);
                return true;
            case 0xE0A3:
                DrawPowerlineColumnNumber(canvas, left, top, width, (float)CellSize.Height);
                return true;
            case 0xE0B0:
                DrawScaledPath(canvas, _powerlineRight, left, top, width, (float)CellSize.Height);
                return true;
            case 0xE0B1:
                canvas.DrawLine(left, top, right, middle, _paint);
                canvas.DrawLine(right, middle, left, bottom, _paint);
                return true;
            case 0xE0B2:
                DrawScaledPath(canvas, _powerlineLeft, left, top, width, (float)CellSize.Height);
                return true;
            case 0xE0B3:
                canvas.DrawLine(right, top, left, middle, _paint);
                canvas.DrawLine(left, middle, right, bottom, _paint);
                return true;
            default:
                return false;
        }
    }

    private void DrawPowerlineBranch(
        SKCanvas canvas,
        float left,
        float top,
        float width,
        float height)
    {
        var radius = Math.Max(1, Math.Min(width, height) * 0.09f);
        var stemX = left + (width * 0.32f);
        var upperY = top + (height * 0.22f);
        var middleY = top + (height * 0.5f);
        var lowerY = top + (height * 0.78f);
        var branchX = left + (width * 0.72f);
        _paint.StrokeWidth = Math.Max(1, width * 0.1f);
        canvas.DrawLine(stemX, upperY, stemX, lowerY, _paint);
        canvas.DrawLine(stemX, middleY, branchX, middleY, _paint);
        canvas.DrawCircle(stemX, upperY, radius, _paint);
        canvas.DrawCircle(stemX, lowerY, radius, _paint);
        canvas.DrawCircle(branchX, middleY, radius, _paint);
    }

    private void DrawPowerlineLineNumber(
        SKCanvas canvas,
        float left,
        float top,
        float width,
        float height)
    {
        var stroke = Math.Max(1, width * 0.1f);
        _paint.StrokeWidth = stroke;
        canvas.DrawLine(left + (width * 0.38f), top + (height * 0.2f), left + (width * 0.28f), top + (height * 0.8f), _paint);
        canvas.DrawLine(left + (width * 0.72f), top + (height * 0.2f), left + (width * 0.62f), top + (height * 0.8f), _paint);
        canvas.DrawLine(left + (width * 0.2f), top + (height * 0.42f), left + (width * 0.8f), top + (height * 0.42f), _paint);
        canvas.DrawLine(left + (width * 0.16f), top + (height * 0.62f), left + (width * 0.76f), top + (height * 0.62f), _paint);
    }

    private void DrawPowerlineLock(
        SKCanvas canvas,
        float left,
        float top,
        float width,
        float height)
    {
        var bodyLeft = left + (width * 0.2f);
        var bodyTop = top + (height * 0.45f);
        var bodyWidth = width * 0.6f;
        var bodyHeight = height * 0.4f;
        canvas.DrawRect(bodyLeft, bodyTop, bodyWidth, bodyHeight, _paint);
        _strokePaint.Color = _paint.Color;
        _strokePaint.StrokeWidth = Math.Max(1, width * 0.1f);
        canvas.DrawArc(
            new SKRect(
                left + (width * 0.3f),
                top + (height * 0.15f),
                left + (width * 0.7f),
                top + (height * 0.6f)),
            180,
            180,
            false,
            _strokePaint);
    }

    private void DrawPowerlineColumnNumber(
        SKCanvas canvas,
        float left,
        float top,
        float width,
        float height)
    {
        _paint.StrokeWidth = Math.Max(1, width * 0.1f);
        var upper = top + (height * 0.22f);
        var lower = top + (height * 0.78f);
        canvas.DrawLine(left + (width * 0.28f), upper, left + (width * 0.28f), lower, _paint);
        canvas.DrawLine(left + (width * 0.5f), upper, left + (width * 0.5f), lower, _paint);
        canvas.DrawLine(left + (width * 0.72f), upper, left + (width * 0.72f), lower, _paint);
    }

    private void DrawScaledPath(
        SKCanvas canvas,
        SKPath path,
        float left,
        float top,
        float width,
        float height)
    {
        canvas.Save();
        canvas.Translate(left, top);
        canvas.Scale(width, height);
        canvas.DrawPath(path, _paint);
        canvas.Restore();
    }

    private static SKPath CreatePowerlinePath(bool pointsRight)
    {
        var path = new SKPath();
        if (pointsRight)
        {
            path.MoveTo(0, 0);
            path.LineTo(1, 0.5f);
            path.LineTo(0, 1);
        }
        else
        {
            path.MoveTo(1, 0);
            path.LineTo(0, 0.5f);
            path.LineTo(1, 1);
        }

        path.Close();
        return path;
    }

    private void DrawDecorations(
        SKCanvas canvas,
        TerminalRenderRun run,
        float top,
        float padding)
    {
        var flags = run.Attributes.Flags;
        var underline = (flags & CellFlags.Underline) != 0 || run.Attributes.HyperlinkUri is not null;
        var strike = (flags & CellFlags.Strikethrough) != 0;
        if (!underline && !strike)
        {
            return;
        }

        _paint.Color = ToColor(run.Attributes.Foreground);
        _paint.StrokeWidth = Math.Max(1, (float)_viewport.Scale);
        var left = padding + (run.StartColumn * (float)CellSize.Width);
        var right = left + (run.CellCount * (float)CellSize.Width);
        if (underline)
        {
            var y = top + _baseline + Math.Max(1, (float)CellSize.Height * 0.08f);
            canvas.DrawLine(left, y, right, y, _paint);
        }

        if (strike)
        {
            var y = top + ((float)CellSize.Height * 0.5f);
            canvas.DrawLine(left, y, right, y, _paint);
        }
    }

    private void DrawRanges(
        SKCanvas canvas,
        IReadOnlyList<TerminalCellRange> ranges,
        float padding)
    {
        for (var index = 0; index < ranges.Count; index++)
        {
            var range = ranges[index];
            var start = Math.Max(0, range.StartColumn);
            var end = Math.Max(start, range.EndColumn);
            _paint.Color = ToColor(range.Color);
            canvas.DrawRect(
                padding + (start * (float)CellSize.Width),
                padding + (range.Row * (float)CellSize.Height),
                (end - start + 1) * (float)CellSize.Width,
                (float)CellSize.Height,
                _paint);
        }
    }

    private void DrawCursor(SKCanvas canvas, TerminalRenderFrame frame, float padding)
    {
        var left = padding + (frame.CursorX * (float)CellSize.Width);
        var top = padding + (frame.CursorY * (float)CellSize.Height);
        var width = (float)CellSize.Width;
        var height = (float)CellSize.Height;
        _paint.Color = ToColor(frame.CursorColor);
        _paint.Style = SKPaintStyle.Fill;

        switch (frame.CursorStyle)
        {
            case TerminalCursorStyle.Underscore:
                canvas.DrawRect(left, top + height - Math.Max(2, height * 0.1f), width, Math.Max(2, height * 0.1f), _paint);
                break;
            case TerminalCursorStyle.DoubleUnderscore:
                var lineHeight = Math.Max(1, height * 0.07f);
                canvas.DrawRect(left, top + height - lineHeight, width, lineHeight, _paint);
                canvas.DrawRect(left, top + height - (lineHeight * 3), width, lineHeight, _paint);
                break;
            case TerminalCursorStyle.Vintage:
                var vintageHeight = Math.Max(1, height * frame.CursorHeightPercentage / 100f);
                canvas.DrawRect(left, top + height - vintageHeight, width, vintageHeight, _paint);
                break;
            case TerminalCursorStyle.FilledBox:
                canvas.DrawRect(left, top, width, height, _paint);
                RedrawCursorCell(canvas, frame, padding, left, top, width, height);
                break;
            case TerminalCursorStyle.EmptyBox:
                _strokePaint.Color = _paint.Color;
                _strokePaint.StrokeWidth = Math.Max(1, (float)_viewport.Scale);
                canvas.DrawRect(left, top, width, height, _strokePaint);
                break;
            default:
                canvas.DrawRect(left, top, Math.Max(1, width * 0.12f), height, _paint);
                break;
        }
    }

    private void RedrawCursorCell(
        SKCanvas canvas,
        TerminalRenderFrame frame,
        float padding,
        float left,
        float top,
        float width,
        float height)
    {
        if (frame.CursorY < 0 || frame.CursorY >= frame.RowsData.Count)
        {
            return;
        }

        var row = frame.RowsData[frame.CursorY];
        for (var index = 0; index < row.Runs.Count; index++)
        {
            var run = row.Runs[index];
            if (frame.CursorX < run.StartColumn ||
                frame.CursorX >= run.StartColumn + run.CellCount ||
                (run.Attributes.Flags & CellFlags.Invisible) != 0)
            {
                continue;
            }

            canvas.Save();
            canvas.ClipRect(new SKRect(left, top, left + width, top + height));
            DrawClusters(
                canvas,
                run,
                top,
                padding,
                CursorTextColor(frame.CursorColor, run.Attributes.Background));
            canvas.Restore();
            return;
        }
    }

    private CachedGlyph Shape(GlyphKey key)
    {
        var typeface = _fonts.Resolve(key.Text.AsSpan(key.Offset, key.Length), key.Flags);
        LastResolvedFontFamily = typeface.FamilyName;
        using var shapingPaint = CreateFontPaint(typeface, key.Flags);
        using var shaper = new SKShaper(typeface);
        using var buffer = new HarfBuzzBuffer();
        buffer.AddUtf16(key.Text, key.Offset, key.Length);
        buffer.GuessSegmentProperties();
        var result = shaper.Shape(buffer, shapingPaint);
        using var font = new SKFont(typeface, key.FontSize)
        {
            Embolden = ShouldEmbolden(typeface, key.Flags),
            SkewX = ShouldSkew(typeface, key.Flags) ? -0.25f : 0,
        };
        var glyphData = new byte[result.Codepoints.Length * sizeof(ushort)];
        for (var index = 0; index < result.Codepoints.Length; index++)
        {
            var glyph = checked((ushort)result.Codepoints[index]);
            glyphData[index * 2] = (byte)glyph;
            glyphData[(index * 2) + 1] = (byte)(glyph >> 8);
        }

        var blob = SKTextBlob.CreatePositioned(
            glyphData,
            SKTextEncoding.GlyphId,
            font,
            result.Points);
        return new CachedGlyph(blob, result.Width, typeface.FamilyName);
    }

    private SKPaint CreateFontPaint(SKTypeface typeface, CellFlags flags) =>
        new()
        {
            IsAntialias = true,
            Typeface = typeface,
            TextSize = _settings.FontSize,
            FakeBoldText = ShouldEmbolden(typeface, flags),
            TextSkewX = ShouldSkew(typeface, flags) ? -0.25f : 0,
        };

    private void MeasureCell()
    {
        var typeface = _fonts.Resolve("M".AsSpan(), CellFlags.None);
        using var paint = CreateFontPaint(typeface, CellFlags.None);
        paint.GetFontMetrics(out var metrics);
        var width = Math.Max(1, paint.MeasureText("M"));
        var height = Math.Max(1, metrics.Descent - metrics.Ascent + metrics.Leading);
        CellSize = new CellSize(Math.Ceiling(width), Math.Ceiling(height * 1.08f));
        _baseline = (float)Math.Ceiling(-metrics.Ascent + ((CellSize.Height - height) * 0.5));
    }

    private void ReleaseResources()
    {
        _glyphs.Dispose();
        _fonts.Dispose();
        foreach (var image in _images.Values)
        {
            image.Dispose();
        }

        _images.Clear();
        _imageBytes = 0;
        _invalidImages.Clear();
    }

    private BoundedResourceCache<GlyphKey, CachedGlyph> CreateGlyphCache() =>
        new(_settings.GlyphCacheCapacity);

    private static TerminalRendererSettings Normalize(TerminalRendererSettings settings) =>
        settings with
        {
            FontFamily = string.IsNullOrWhiteSpace(settings.FontFamily)
                ? "Cascadia Mono"
                : settings.FontFamily.Trim(),
            FontSize = Math.Max(1, settings.FontSize),
            FontWeight = Math.Clamp(settings.FontWeight, 100, 1000),
            GlyphCacheCapacity = Math.Max(1, settings.GlyphCacheCapacity),
            DecodedImageCacheByteCapacity = Math.Max(
                4L * 1024 * 1024,
                settings.DecodedImageCacheByteCapacity),
        };

    private static bool IsWhitespace(ReadOnlySpan<char> text)
    {
        foreach (var character in text)
        {
            if (!char.IsWhiteSpace(character))
            {
                return false;
            }
        }

        return true;
    }

    private static SKColor ToColor(uint argb) => new(
        (byte)((argb >> 16) & 0xFF),
        (byte)((argb >> 8) & 0xFF),
        (byte)(argb & 0xFF),
        (byte)(argb >> 24));

    private bool ShouldEmbolden(SKTypeface typeface, CellFlags flags) =>
        !typeface.IsBold &&
        ((flags & CellFlags.Bold) != 0 || _settings.FontWeight >= 600);

    private static bool ShouldSkew(SKTypeface typeface, CellFlags flags) =>
        (flags & CellFlags.Italic) != 0 && !typeface.IsItalic;

    private static uint CursorTextColor(uint cursorColor, uint cellBackground)
    {
        var cursorLuminance = Luminance(cursorColor);
        if (Math.Abs(cursorLuminance - Luminance(cellBackground)) >= 96)
        {
            return cellBackground;
        }

        return cursorLuminance >= 128 ? 0xFF000000 : 0xFFFFFFFF;
    }

    private static double Luminance(uint argb) =>
        (((argb >> 16) & 0xFF) * 0.2126) +
        (((argb >> 8) & 0xFF) * 0.7152) +
        ((argb & 0xFF) * 0.0722);

    private readonly record struct GlyphKey(
        string Text,
        int Offset,
        int Length,
        CellFlags Flags,
        float FontSize,
        double Scale)
    {
        public bool Equals(GlyphKey other) =>
            Offset == other.Offset &&
            Length == other.Length &&
            Flags == other.Flags &&
            FontSize.Equals(other.FontSize) &&
            Scale.Equals(other.Scale) &&
            string.Equals(Text, other.Text, StringComparison.Ordinal);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(Flags);
            hash.Add(FontSize);
            hash.Add(Scale);
            hash.Add(Offset);
            hash.Add(Length);
            hash.Add(Text, StringComparer.Ordinal);
            return hash.ToHashCode();
        }
    }

    private sealed class CachedImage(SKBitmap bitmap, long byteSize) : IDisposable
    {
        public SKBitmap Bitmap { get; } = bitmap;
        public long ByteSize { get; } = byteSize;
        public void Dispose() => Bitmap.Dispose();
    }

    private sealed class CachedGlyph(SKTextBlob blob, float width, string familyName) : IDisposable
    {
        public SKTextBlob Blob { get; } = blob;
        public float Width { get; } = width;
        public string FamilyName { get; } = familyName;
        public void Dispose() => Blob.Dispose();
    }

    private sealed class FontResolver : IDisposable
    {
        private readonly TerminalRendererSettings _settings;
        private readonly Dictionary<FontKey, SKTypeface> _faces = [];
        private readonly List<SourceFace> _sourceFaces = [];
        private readonly string[] _families;

        public FontResolver(TerminalRendererSettings settings)
        {
            _settings = settings;
            _families = [settings.FontFamily, .. settings.FallbackFontFamilies];
            for (var index = 0; index < settings.FontSources.Count; index++)
            {
                var source = settings.FontSources[index];
                using var stream = source.OpenStream();
                var face = SKTypeface.FromStream(stream);
                if (face is not null)
                {
                    _sourceFaces.Add(new SourceFace(source.FamilyName, source.Italic, face));
                }
            }
        }

        public SKTypeface Resolve(ReadOnlySpan<char> text, CellFlags flags)
        {
            var styleWeight = (flags & CellFlags.Bold) != 0
                ? Math.Max(_settings.FontWeight, (int)SKFontStyleWeight.Bold)
                : _settings.FontWeight;
            var styleSlant = (flags & CellFlags.Italic) != 0
                ? SKFontStyleSlant.Italic
                : SKFontStyleSlant.Upright;
            var style = new SKFontStyle(
                styleWeight,
                (int)SKFontStyleWidth.Normal,
                styleSlant);
            foreach (var family in _families)
            {
                var source = GetSourceFace(family, styleSlant == SKFontStyleSlant.Italic);
                if (source is not null && source.ContainsGlyphs(text))
                {
                    return source;
                }

                var face = GetFamily(family, styleWeight, styleSlant, style);
                if (face is not null && face.ContainsGlyphs(text))
                {
                    return face;
                }
            }

            Rune.DecodeFromUtf16(text, out var rune, out _);
            var matched = SKFontManager.Default.MatchCharacter(
                _settings.FontFamily,
                style,
                null,
                rune.Value);
            if (matched is null)
            {
                return GetFamily(
                    _settings.FontFamily,
                    styleWeight,
                    styleSlant,
                    style) ?? SKTypeface.Default;
            }

            var fallback = GetFamily(matched.FamilyName, styleWeight, styleSlant, style);
            if (fallback is null)
            {
                _faces.Add(new FontKey(matched.FamilyName, styleWeight, styleSlant), matched);
                return matched;
            }

            matched.Dispose();
            return fallback;
        }

        public void Dispose()
        {
            foreach (var face in _faces.Values.Distinct())
            {
                face.Dispose();
            }

            foreach (var source in _sourceFaces)
            {
                source.Typeface.Dispose();
            }

            _faces.Clear();
            _sourceFaces.Clear();
        }

        private SKTypeface? GetSourceFace(string family, bool italic)
        {
            SKTypeface? regular = null;
            for (var index = 0; index < _sourceFaces.Count; index++)
            {
                var source = _sourceFaces[index];
                if (!source.FamilyName.Equals(family, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (source.Italic == italic)
                {
                    return source.Typeface;
                }

                if (!source.Italic)
                {
                    regular = source.Typeface;
                }
            }

            return regular;
        }

        private SKTypeface? GetFamily(
            string family,
            int weight,
            SKFontStyleSlant slant,
            SKFontStyle style)
        {
            var key = new FontKey(family, weight, slant);
            if (_faces.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var face = SKFontManager.Default.MatchFamily(family, style);
            if (face is null)
            {
                return null;
            }

            _faces.Add(key, face);
            return face;
        }

        private readonly record struct FontKey(
            string Family,
            int Weight,
            SKFontStyleSlant Slant);

        private sealed record SourceFace(
            string FamilyName,
            bool Italic,
            SKTypeface Typeface);
    }
}
