using System.Globalization;
using System.Text;
using SkiaSharp;

namespace StockAnalyzer.Core.Rendering;

/// <summary>
/// Draws the headless AI chart image: price panel (grid, candles, overlay lines, markers, legend), volume panel, oscillator panels, axis labels, PNG. It has no state, reads no setting
/// and no theme, and draws the same pixels for the same request. Every Skia object lives for one call only (<c>using</c>); the token is checked between the steps.
/// Shapes with straight horizontal or vertical edges (bodies, wicks, grid, borders, volume bars) are whole-pixel rectangles without anti-aliasing; lines, markers and text are anti-aliased.
/// </summary>
public static class AiChartImageRenderer
{
    public static AiChartImageResult Render(AiChartImageRequest request, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<CandleData> candles = request.Candles;
        if (candles.Count == 0)
        {
            throw new ArgumentException("At least one candle.", nameof(request));
        }

        IReadOnlyList<AiChartOscillator> oscillators = request.Oscillators ?? Array.Empty<AiChartOscillator>();
        IReadOnlyList<AiChartMarker> markers = request.Markers ?? Array.Empty<AiChartMarker>();
        ValidateRequest(request, candles.Count, oscillators, markers);

        var layout = AiChartLayout.Create(request.WidthPx, request.HeightPx, request.IncludeVolume, oscillators.Count);
        AiChartPanelRect pricePanel = layout.PricePanel;

        IReadOnlyList<AiChartStyledLine> overlayLines = AiChartLineAssigner.AssignOverlays(request.Overlays ?? Array.Empty<AiChartOverlay>());
        (decimal priceMin, decimal priceMax) = AiChartPriceRange.Compute(candles, overlayLines.Select(l => l.Values), pricePanel.HeightPx);
        var priceTransform = new AiChartPlotTransform(layout.PlotLeftPx, pricePanel.TopPx, layout.PlotWidthPx, pricePanel.HeightPx, priceMin, priceMax, candles.Count);

        AiChartPanelRect? volumePanel = request.IncludeVolume ? layout.Panels[1] : null;
        (decimal volumeMin, decimal volumeMax) = AiChartPriceRange.ComputeVolume(candles);
        AiChartPlotTransform? volumeTransform = volumePanel is { } vp
            ? new AiChartPlotTransform(layout.PlotLeftPx, vp.TopPx, layout.PlotWidthPx, vp.HeightPx, volumeMin, volumeMax, candles.Count)
            : null;

        int firstOscillatorPanel = request.IncludeVolume ? 2 : 1;
        var oscillatorPanels = new List<OscillatorPanel>(oscillators.Count);
        for (int i = 0; i < oscillators.Count; i++)
        {
            AiChartPanelRect rect = layout.Panels[firstOscillatorPanel + i];
            IReadOnlyList<AiChartStyledLine> lines = AiChartLineAssigner.AssignOscillator(oscillators[i]);
            (decimal min, decimal max) = AiChartPriceRange.ComputeOscillator(lines.Select(l => l.Values), oscillators[i].FixedMin, oscillators[i].FixedMax, oscillators[i].IncludeZeroBaseline);
            var transform = new AiChartPlotTransform(layout.PlotLeftPx, rect.TopPx, layout.PlotWidthPx, rect.HeightPx, min, max, candles.Count);
            oscillatorPanels.Add(new OscillatorPanel(oscillators[i], rect, transform, lines, min, max));
        }

        int bodyWidth = BodyWidthPx(priceTransform.RawBodyWidthPx);
        List<AiChartBarPosition> bars = Positions(candles, priceTransform);

        using SKSurface surface = SKSurface.Create(new SKImageInfo(request.WidthPx, request.HeightPx, SKColorType.Rgba8888, SKAlphaType.Premul))
            ?? throw new InvalidOperationException("SkiaSharp could not create a drawing surface for the image.");
        SKCanvas canvas = surface.Canvas;
        canvas.Clear(AiChartImageStyle.Color(AiChartImageStyle.BackgroundHex(request.Background)));

        using var fill = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = false };
        using var font = new SKFont(SKTypeface.Default, AiChartImageStyle.FontSizePx);
        // The paint carries the same typeface and size as the font, because the text is measured through the paint.
        using var textPaint = new SKPaint
        {
            Color = AiChartImageStyle.Color(AiChartImageStyle.TextHex),
            IsAntialias = true,
            Typeface = SKTypeface.Default,
            TextSize = AiChartImageStyle.FontSizePx,
        };

        cancellationToken.ThrowIfCancellationRequested();
        DrawGridAndBorders(canvas, fill, layout, bars);
        foreach (OscillatorPanel panel in oscillatorPanels)
        {
            DrawZeroLine(canvas, panel, layout);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (volumeTransform is not null && volumePanel is { } volume)
        {
            DrawVolume(canvas, fill, candles, volumeTransform, volume, bars, bodyWidth);
        }

        DrawCandles(canvas, fill, candles, bars, bodyWidth);
        cancellationToken.ThrowIfCancellationRequested();

        DrawLines(canvas, pricePanel, priceTransform, overlayLines, bars);
        DrawMarkers(canvas, pricePanel, candles, bars, markers);
        foreach (OscillatorPanel panel in oscillatorPanels)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DrawLines(canvas, panel.Rect, panel.Transform, panel.Lines, bars);
        }

        var legend = new List<AiChartLegendEntry>();
        foreach (AiChartStyledLine line in overlayLines.Concat(oscillatorPanels.SelectMany(p => p.Lines)))
        {
            legend.Add(new AiChartLegendEntry(line.Label, line.ColorHex, line.LineStyle, line.WidthPx));
        }

        int legendDrawn = DrawLegend(canvas, font, textPaint, layout, legend);
        DrawAxisLabels(canvas, font, textPaint, layout, priceTransform, volumePanel, volumeMax, oscillatorPanels, bars);

        using SKImage image = surface.Snapshot();
        using SKData data = image.Encode(SKEncodedImageFormat.Png, AiChartImageStyle.PngQuality);
        byte[] png = data.ToArray();

        var panels = new List<AiChartPanelInfo>
        {
            new("price", AiChartPanelKind.Price, pricePanel.TopPx, pricePanel.BottomPx, priceMin, priceMax),
        };
        if (volumePanel is { } v)
        {
            panels.Add(new AiChartPanelInfo("volume", AiChartPanelKind.Volume, v.TopPx, v.BottomPx, volumeMin, volumeMax));
        }

        foreach (OscillatorPanel panel in oscillatorPanels)
        {
            panels.Add(new AiChartPanelInfo(panel.Oscillator.Id, AiChartPanelKind.Oscillator, panel.Rect.TopPx, panel.Rect.BottomPx, panel.Min, panel.Max));
        }

        var axes = new AiChartAxes(
            priceMin,
            priceMax,
            layout.PlotLeftPx,
            layout.PlotRightPx,
            pricePanel.TopPx,
            pricePanel.BottomPx,
            AiChartPlotTransform.Snap(priceTransform.BarSpacingPx),
            bars[0].XPx);
        return new AiChartImageResult(png, request.WidthPx, request.HeightPx, axes, panels, bars, legend, legendDrawn);
    }

    private sealed record OscillatorPanel(
        AiChartOscillator Oscillator,
        AiChartPanelRect Rect,
        AiChartPlotTransform Transform,
        IReadOnlyList<AiChartStyledLine> Lines,
        decimal Min,
        decimal Max);

    private static void ValidateRequest(AiChartImageRequest request, int barCount, IReadOnlyList<AiChartOscillator> oscillators, IReadOnlyList<AiChartMarker> markers)
    {
        AiChartSizeCheck check = AiChartLayout.CheckSize(request.WidthPx, request.HeightPx, barCount, request.IncludeVolume, oscillators.Count);
        if (!check.IsValid)
        {
            throw new ArgumentException($"The image size {request.WidthPx}x{request.HeightPx} does not hold {barCount} bars with these panels; check AiChartLayout.CheckSize first.", nameof(request));
        }

        foreach (AiChartOverlay overlay in request.Overlays ?? Array.Empty<AiChartOverlay>())
        {
            foreach (AiChartLineSeries series in overlay.Series)
            {
                RequireWindowLength(series, barCount, overlay.Id);
            }
        }

        foreach (AiChartOscillator oscillator in oscillators)
        {
            foreach (AiChartLineSeries series in oscillator.Series)
            {
                RequireWindowLength(series, barCount, oscillator.Id);
            }
        }

        foreach (AiChartMarker marker in markers)
        {
            if (marker.BarIndex < 0 || marker.BarIndex >= barCount)
            {
                throw new ArgumentException($"A marker names bar {marker.BarIndex}, outside the {barCount} drawn bars.", nameof(request));
            }
        }
    }

    private static void RequireWindowLength(AiChartLineSeries series, int barCount, string id)
    {
        if (series.Values.Count != barCount)
        {
            throw new ArgumentException($"The series '{series.Name}' of '{id}' has {series.Values.Count} values for {barCount} drawn bars.");
        }
    }

    /// <summary>The candle body width in whole pixels: the transformation layer's width cut down to an odd number, at least the style's minimum.</summary>
    internal static int BodyWidthPx(double rawWidth)
    {
        int width = (int)Math.Floor(rawWidth);
        if (width % 2 == 0)
        {
            width--;
        }

        return Math.Max(AiChartImageStyle.MinBodyWidthPx, width);
    }

    private static List<AiChartBarPosition> Positions(IReadOnlyList<CandleData> candles, AiChartPlotTransform transform)
    {
        var bars = new List<AiChartBarPosition>(candles.Count);
        for (int i = 0; i < candles.Count; i++)
        {
            CandleData c = candles[i];
            bars.Add(new AiChartBarPosition(
                i,
                c.Timestamp,
                AiChartPlotTransform.Snap(transform.CenterXPx(i)),
                AiChartPlotTransform.Snap(transform.YPx(c.Open)),
                AiChartPlotTransform.Snap(transform.YPx(c.High)),
                AiChartPlotTransform.Snap(transform.YPx(c.Low)),
                AiChartPlotTransform.Snap(transform.YPx(c.Close))));
        }

        return bars;
    }

    private static void FillRect(SKCanvas canvas, SKPaint paint, int left, int top, int width, int height, SKColor color)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        paint.Color = color;
        canvas.DrawRect(left, top, width, height, paint);
    }

    private static IReadOnlyList<decimal> GridFractions(AiChartPanelKind kind) => kind switch
    {
        AiChartPanelKind.Price => AiChartImageStyle.PriceTickFractions,
        AiChartPanelKind.Volume => AiChartImageStyle.VolumeTickFractions,
        _ => AiChartImageStyle.OscillatorTickFractions,
    };

    private static void DrawGridAndBorders(SKCanvas canvas, SKPaint fill, AiChartLayout layout, IReadOnlyList<AiChartBarPosition> bars)
    {
        SKColor grid = AiChartImageStyle.Color(AiChartImageStyle.GridHex);
        SKColor border = AiChartImageStyle.Color(AiChartImageStyle.BorderHex);
        int left = layout.PlotLeftPx;
        int width = layout.PlotWidthPx;

        foreach (AiChartPanelRect panel in layout.Panels)
        {
            foreach (decimal fraction in GridFractions(panel.Kind))
            {
                FillRect(canvas, fill, left, TickRow(panel, fraction), width, AiChartImageStyle.GridWidthPx, grid);
            }
        }

        // Vertical grid lines at the date labels, over the whole stack of panels.
        int top = layout.Panels[0].TopPx;
        int bottom = layout.Panels[^1].BottomPx;
        foreach (int index in DateLabelIndices(layout.PlotWidthPx, bars.Count))
        {
            FillRect(canvas, fill, AiChartPlotTransform.PixelOf(bars[index].XPx), top, AiChartImageStyle.GridWidthPx, bottom - top, grid);
        }

        foreach (AiChartPanelRect panel in layout.Panels)
        {
            int b = AiChartImageStyle.BorderWidthPx;
            FillRect(canvas, fill, left, panel.TopPx, width, b, border);
            FillRect(canvas, fill, left, panel.BottomPx - b, width, b, border);
            FillRect(canvas, fill, left, panel.TopPx, b, panel.HeightPx, border);
            FillRect(canvas, fill, left + width - b, panel.TopPx, b, panel.HeightPx, border);
        }
    }

    /// <summary>The pixel row of a tick at a fraction of the panel height measured from its bottom.</summary>
    private static int TickRow(AiChartPanelRect panel, decimal fraction)
        => Math.Clamp(panel.BottomPx - (int)Math.Round(panel.HeightPx * fraction, MidpointRounding.AwayFromZero), panel.TopPx, panel.BottomPx - 1);

    /// <summary>A dashed line at zero in an oscillator panel that asks for a zero baseline, when zero is inside its range.</summary>
    private static void DrawZeroLine(SKCanvas canvas, OscillatorPanel panel, AiChartLayout layout)
    {
        if (!panel.Oscillator.IncludeZeroBaseline || panel.Min >= 0m || panel.Max <= 0m)
        {
            return;
        }

        float y = AiChartPlotTransform.PixelOf(AiChartPlotTransform.Snap(panel.Transform.YPx(0m))) + AiChartImageStyle.PixelCentreOffsetPx;
        using var paint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            IsAntialias = false,
            Color = AiChartImageStyle.Color(AiChartImageStyle.GridHex),
            StrokeWidth = AiChartImageStyle.GridWidthPx,
        };
        using SKPathEffect? effect = DashEffect(AiChartLineStyle.Dash);
        paint.PathEffect = effect;
        canvas.DrawLine(layout.PlotLeftPx, y, layout.PlotRightPx, y, paint);
    }

    private static void DrawCandles(SKCanvas canvas, SKPaint fill, IReadOnlyList<CandleData> candles, IReadOnlyList<AiChartBarPosition> bars, int bodyWidth)
    {
        SKColor up = AiChartImageStyle.Color(AiChartImageStyle.UpHex);
        SKColor down = AiChartImageStyle.Color(AiChartImageStyle.DownHex);
        SKColor doji = AiChartImageStyle.Color(AiChartImageStyle.DojiHex);
        int half = (bodyWidth - 1) / 2;

        for (int i = 0; i < candles.Count; i++)
        {
            CandleData c = candles[i];
            AiChartBarPosition bar = bars[i];
            SKColor color = c.Close > c.Open ? up : c.Close < c.Open ? down : doji;
            int column = AiChartPlotTransform.PixelOf(bar.XPx);
            int highRow = AiChartPlotTransform.PixelOf(bar.HighYPx);
            int lowRow = AiChartPlotTransform.PixelOf(bar.LowYPx);
            int openRow = AiChartPlotTransform.PixelOf(bar.OpenYPx);
            int closeRow = AiChartPlotTransform.PixelOf(bar.CloseYPx);
            int bodyTop = Math.Min(openRow, closeRow);
            int bodyBottom = Math.Max(openRow, closeRow);

            // The wick is a 1 px column from the high row to the low row; the body is a filled rectangle that includes the open row and the close row.
            FillRect(canvas, fill, column, highRow, AiChartImageStyle.WickWidthPx, lowRow - highRow + 1, color);
            FillRect(canvas, fill, column - half, bodyTop, bodyWidth, bodyBottom - bodyTop + 1, color);
        }
    }

    private static void DrawVolume(
        SKCanvas canvas,
        SKPaint fill,
        IReadOnlyList<CandleData> candles,
        AiChartPlotTransform transform,
        AiChartPanelRect panel,
        IReadOnlyList<AiChartBarPosition> bars,
        int bodyWidth)
    {
        SKColor color = AiChartImageStyle.Color(AiChartImageStyle.VolumeHex);
        int half = (bodyWidth - 1) / 2;
        int inner = AiChartImageStyle.BorderWidthPx;
        int floor = panel.BottomPx - inner;
        for (int i = 0; i < candles.Count; i++)
        {
            if (candles[i].Volume <= 0)
            {
                continue;
            }

            int topRow = Math.Clamp(AiChartPlotTransform.PixelOf(AiChartPlotTransform.Snap(transform.YPx(candles[i].Volume))), panel.TopPx + inner, floor - 1);
            FillRect(canvas, fill, AiChartPlotTransform.PixelOf(bars[i].XPx) - half, topRow, bodyWidth, floor - topRow, color);
        }
    }

    /// <summary>
    /// Draws lines through the centres of the bars inside a panel (clipped to it). A missing value breaks the line; a single value between two gaps is a dot.
    /// Points are at the centre of the pixel column and row of the bar and the value, as the candles are.
    /// </summary>
    private static void DrawLines(SKCanvas canvas, AiChartPanelRect panel, AiChartPlotTransform transform, IReadOnlyList<AiChartStyledLine> lines, IReadOnlyList<AiChartBarPosition> bars)
    {
        if (lines.Count == 0)
        {
            return;
        }

        canvas.Save();
        canvas.ClipRect(new SKRect(transform.PlotLeftPx, panel.TopPx, transform.PlotLeftPx + transform.PlotWidthPx, panel.BottomPx));
        foreach (AiChartStyledLine line in lines)
        {
            using var paint = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                IsAntialias = true,
                Color = AiChartImageStyle.Color(line.ColorHex),
                StrokeWidth = line.WidthPx,
                StrokeJoin = SKStrokeJoin.Round,
                StrokeCap = line.LineStyle == AiChartLineStyle.Solid ? SKStrokeCap.Round : SKStrokeCap.Butt,
            };
            using SKPathEffect? effect = DashEffect(line.LineStyle);
            paint.PathEffect = effect;
            using var path = new SKPath();
            int runLength = 0;
            SKPoint single = default;
            for (int i = 0; i <= line.Values.Count; i++)
            {
                decimal? value = i < line.Values.Count ? line.Values[i] : null;
                if (value is null)
                {
                    if (runLength == 1 && line.LineStyle == AiChartLineStyle.Solid)
                    {
                        using var dot = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true, Color = paint.Color };
                        canvas.DrawCircle(single, line.WidthPx / 2f, dot);
                    }

                    runLength = 0;
                    continue;
                }

                var point = new SKPoint(
                    AiChartPlotTransform.PixelOf(bars[i].XPx) + AiChartImageStyle.PixelCentreOffsetPx,
                    AiChartPlotTransform.PixelOf(AiChartPlotTransform.Snap(transform.YPx(value.Value))) + AiChartImageStyle.PixelCentreOffsetPx);
                if (runLength == 0)
                {
                    path.MoveTo(point);
                    single = point;
                }
                else
                {
                    path.LineTo(point);
                }

                runLength++;
            }

            canvas.DrawPath(path, paint);
        }

        canvas.Restore();
    }

    /// <summary>
    /// Buy markers point up under the low of their bar, sell markers point down over the high; filled triangles of the style's side length in the signal colour, clipped to the price panel.
    /// </summary>
    private static void DrawMarkers(SKCanvas canvas, AiChartPanelRect panel, IReadOnlyList<CandleData> candles, IReadOnlyList<AiChartBarPosition> bars, IReadOnlyList<AiChartMarker> markers)
    {
        if (markers.Count == 0)
        {
            return;
        }

        float side = AiChartImageStyle.MarkerSidePx;
        float height = side * AiChartImageStyle.MarkerHeightRatio;
        canvas.Save();
        canvas.ClipRect(new SKRect(0, panel.TopPx, canvas.DeviceClipBounds.Right, panel.BottomPx));
        using var paint = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true, Color = AiChartImageStyle.Color(AiChartImageStyle.SignalHex) };
        foreach (AiChartMarker marker in markers)
        {
            AiChartBarPosition bar = bars[marker.BarIndex];
            float x = AiChartPlotTransform.PixelOf(bar.XPx) + AiChartImageStyle.PixelCentreOffsetPx;
            using var path = new SKPath();
            if (marker.IsBuy)
            {
                float apex = AiChartPlotTransform.PixelOf(bar.LowYPx) + 1 + AiChartImageStyle.MarkerGapPx;
                path.MoveTo(x, apex);
                path.LineTo(x - (side / 2f), apex + height);
                path.LineTo(x + (side / 2f), apex + height);
            }
            else
            {
                float apex = AiChartPlotTransform.PixelOf(bar.HighYPx) - AiChartImageStyle.MarkerGapPx;
                path.MoveTo(x, apex);
                path.LineTo(x - (side / 2f), apex - height);
                path.LineTo(x + (side / 2f), apex - height);
            }

            path.Close();
            canvas.DrawPath(path, paint);
        }

        canvas.Restore();
    }

    private static int DrawLegend(SKCanvas canvas, SKFont font, SKPaint textPaint, AiChartLayout layout, IReadOnlyList<AiChartLegendEntry> legend)
    {
        if (legend.Count == 0)
        {
            return 0;
        }

        int count = Math.Min(legend.Count, AiChartImageStyle.MaxLegendEntries);
        AiChartPanelRect panel = layout.PricePanel;
        int pad = AiChartImageStyle.LegendPaddingPx;
        int rowHeight = AiChartImageStyle.LegendRowHeightPx;
        float labelWidth = 0;
        var labels = new string[count];
        for (int i = 0; i < count; i++)
        {
            labels[i] = Ascii(legend[i].Label);
            labelWidth = Math.Max(labelWidth, textPaint.MeasureText(labels[i]));
        }

        float boxWidth = pad + AiChartImageStyle.LegendSwatchWidthPx + pad + labelWidth + pad;
        float boxLeft = layout.PlotRightPx - boxWidth - pad;
        float boxTop = panel.TopPx + pad;
        using var boxPaint = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = false, Color = AiChartImageStyle.Color(AiChartImageStyle.LegendFillHex).WithAlpha(AiChartImageStyle.LegendFillAlpha) };
        canvas.DrawRect(boxLeft, boxTop, boxWidth, (count * rowHeight) + pad, boxPaint);

        for (int i = 0; i < count; i++)
        {
            float y = boxTop + (pad / 2f) + (i * rowHeight) + (rowHeight / 2f);
            using var linePaint = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                IsAntialias = true,
                Color = AiChartImageStyle.Color(legend[i].ColorHex),
                StrokeWidth = legend[i].LineWidthPx,
            };
            using SKPathEffect? effect = DashEffect(legend[i].LineStyle);
            linePaint.PathEffect = effect;
            canvas.DrawLine(boxLeft + pad, y, boxLeft + pad + AiChartImageStyle.LegendSwatchWidthPx, y, linePaint);
            canvas.DrawText(labels[i], boxLeft + pad + AiChartImageStyle.LegendSwatchWidthPx + pad, y + (AiChartImageStyle.FontSizePx * AiChartImageStyle.TextCentreRatio), font, textPaint);
        }

        return count;
    }

    /// <summary>The dash pattern of a line style, to be disposed by the caller; null for a solid line.</summary>
    internal static SKPathEffect? DashEffect(AiChartLineStyle style) => style switch
    {
        AiChartLineStyle.Dash => SKPathEffect.CreateDash(AiChartImageStyle.DashIntervalsPx.ToArray(), 0),
        AiChartLineStyle.Dot => SKPathEffect.CreateDash(AiChartImageStyle.DotIntervalsPx.ToArray(), 0),
        _ => null,
    };

    private static void DrawAxisLabels(
        SKCanvas canvas,
        SKFont font,
        SKPaint textPaint,
        AiChartLayout layout,
        AiChartPlotTransform priceTransform,
        AiChartPanelRect? volumePanel,
        decimal volumeMax,
        IReadOnlyList<OscillatorPanel> oscillatorPanels,
        IReadOnlyList<AiChartBarPosition> bars)
    {
        float labelX = layout.PlotRightPx + AiChartImageStyle.AxisLabelOffsetPx;
        float baseline = AiChartImageStyle.FontSizePx * AiChartImageStyle.TextCentreRatio;

        foreach (decimal fraction in AiChartImageStyle.PriceTickFractions)
        {
            int row = TickRow(layout.PricePanel, fraction);
            canvas.DrawText(FormatPrice(priceTransform.ValueAt(row + AiChartImageStyle.PixelCentreOffsetPx)), labelX, row + AiChartImageStyle.PixelCentreOffsetPx + baseline, font, textPaint);
        }

        if (volumePanel is { } panel)
        {
            foreach (decimal fraction in AiChartImageStyle.VolumeTickFractions)
            {
                int row = TickRow(panel, fraction);
                canvas.DrawText(FormatVolume(volumeMax * fraction), labelX, row + AiChartImageStyle.PixelCentreOffsetPx + baseline, font, textPaint);
            }
        }

        foreach (OscillatorPanel oscillator in oscillatorPanels)
        {
            foreach (decimal fraction in AiChartImageStyle.OscillatorTickFractions)
            {
                int row = TickRow(oscillator.Rect, fraction);
                canvas.DrawText(FormatPrice(oscillator.Transform.ValueAt(row + AiChartImageStyle.PixelCentreOffsetPx)), labelX, row + AiChartImageStyle.PixelCentreOffsetPx + baseline, font, textPaint);
            }
        }

        float dateY = layout.Panels[^1].BottomPx + AiChartImageStyle.FontSizePx + AiChartImageStyle.DateLabelOffsetPx;
        foreach (int index in DateLabelIndices(layout.PlotWidthPx, bars.Count))
        {
            string text = bars[index].Date.ToString(AiChartImageStyle.DateFormat, CultureInfo.InvariantCulture);
            float width = textPaint.MeasureText(text);
            float x = Math.Clamp((float)bars[index].XPx - (width / 2f), AiChartImageStyle.DateLabelEdgePx, layout.WidthPx - width - AiChartImageStyle.DateLabelEdgePx);
            canvas.DrawText(text, x, dateY, font, textPaint);
        }
    }

    /// <summary>The bars that carry a date label: evenly spread, as many as fit with <c>DateLabelMinSpacingPx</c>, at most <c>MaxDateLabels</c>, always the first bar.</summary>
    internal static IReadOnlyList<int> DateLabelIndices(int plotWidthPx, int barCount)
    {
        int labels = Math.Clamp(plotWidthPx / AiChartImageStyle.DateLabelMinSpacingPx, 1, Math.Min(AiChartImageStyle.MaxDateLabels, barCount));
        if (labels == 1)
        {
            return new[] { 0 };
        }

        var indices = new List<int>(labels);
        for (int k = 0; k < labels; k++)
        {
            indices.Add((int)Math.Round((barCount - 1) * (double)k / (labels - 1), MidpointRounding.AwayFromZero));
        }

        return indices.Distinct().ToList();
    }

    /// <summary>A price label: two decimals from 1 upward, four below 1 (the instrument's price unit).</summary>
    internal static string FormatPrice(decimal value)
        => value.ToString(Math.Abs(value) >= AiChartImageStyle.PriceFewDecimalsFrom ? AiChartImageStyle.PriceFormatFewDecimals : AiChartImageStyle.PriceFormatManyDecimals, CultureInfo.InvariantCulture);

    /// <summary>A volume label with a K, M or B suffix from a thousand upward.</summary>
    internal static string FormatVolume(decimal value)
    {
        decimal abs = Math.Abs(value);
        foreach ((decimal unit, string suffix) in AiChartImageStyle.VolumeUnits)
        {
            if (abs >= unit)
            {
                return (value / unit).ToString(AiChartImageStyle.VolumeScaledFormat, CultureInfo.InvariantCulture) + suffix;
            }
        }

        return value.ToString(AiChartImageStyle.VolumePlainFormat, CultureInfo.InvariantCulture);
    }

    /// <summary>The text with every character outside printable ASCII replaced by '?' (the image holds English letters and digits only).</summary>
    internal static string Ascii(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            builder.Append(c is >= ' ' and <= '~' ? c : '?');
        }

        return builder.ToString();
    }
}
