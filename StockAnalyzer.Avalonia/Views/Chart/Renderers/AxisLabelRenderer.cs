using System;
using System.Collections.Generic;
using Avalonia;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Theme;

namespace StockAnalyzer.Avalonia.Views.Chart.Renderers;

/// <summary>
/// Renders axis projection labels on the right side of the Y-axis.
/// Collects <see cref="AxisLabelRequest"/> entries and draws them
/// with automatic anti-overlap collision avoidance.
/// </summary>
public sealed class AxisLabelRenderer : IDisposable
{
    private readonly SKPaint _textPaint;
    private readonly SKPaint _backgroundPaint;
    private readonly SKPaint _borderPaint;
    private readonly SKPaint _arrowPaint;
    private readonly SKFont _labelFont;
    private readonly SKPath _arrowPath = new();

    // Reusable list and set to avoid per-frame allocation
    private readonly List<ResolvedLabel> _resolvedLabels = new();
    private readonly HashSet<int> _seenYCoordinates = new();

    public AxisLabelRenderer()
    {
        _textPaint = new SKPaint
        {
            IsAntialias = true
        };

        _backgroundPaint = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };

        _borderPaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1,
            IsAntialias = true
        };

        _arrowPaint = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };

        _labelFont = new SKFont(SKTypeface.Default, ChartTheme.AxisLabelFontSize);
    }

    /// <summary>
    /// Resolves positions and renders the given axis label requests.
    /// </summary>
    public void Render(
        SKCanvas canvas,
        ChartLayoutContext layout,
        ChartDataSnapshot snapshot,
        IChartRenderConfig config,
        IReadOnlyList<AxisLabelRequest> labels)
    {
        ResolveLabels(layout, snapshot, config, labels);
        if (_resolvedLabels.Count == 0) return;

        Draw(canvas, (float)layout.ChartArea.Right);
    }

    /// <summary>
    /// Phase 1 & 2 & 3: Resolve screen positions and handle overlaps.
    /// </summary>
    public void ResolveLabels(
        ChartLayoutContext layout,
        ChartDataSnapshot snapshot,
        IChartRenderConfig config,
        IReadOnlyList<AxisLabelRequest> labels,
        IReadOnlyDictionary<int, (decimal Min, decimal Max)>? panelRanges = null)
    {
        _resolvedLabels.Clear();
        _seenYCoordinates.Clear();
        if (labels.Count == 0) return;

        var transform = config.Transform;
        if (transform == null) return;

        for (int i = 0; i < labels.Count; i++)
        {
            var req = labels[i];
            
            float y;
            if (req.PanelIndex < 0)
            {
                // Main Chart
                if (snapshot.PriceRange <= 0) continue;
                y = (float)transform.ChartToScreen(new ChartPoint(DateTime.MinValue, req.Value)).Y + (float)layout.ChartArea.Top;
            }
            else
            {
                // Sub-window Panel
                if (panelRanges == null || !panelRanges.TryGetValue(req.PanelIndex, out var range)) continue;
                if (req.PanelIndex >= layout.PanelAreas.Count) continue;
                
                var area = layout.PanelAreas[req.PanelIndex];
                decimal rangeVal = range.Max - range.Min;
                if (rangeVal <= 0) rangeVal = 1m;
                
                decimal normalizedY = 1m - (req.Value - range.Min) / rangeVal;
                y = (float)(area.Height * (double)normalizedY + area.Top);
            }

            // FINITENESS GUARD: Skip if screen projection is invalid (prevents Sort() crash)
            if (!float.IsFinite(y)) continue;

            // Step 3: Deduplicate identical visual positions (occlusion handling)
            int ry = (int)Math.Round(y);
            if (_seenYCoordinates.Contains(ry)) continue;
            _seenYCoordinates.Add(ry);
            // Neighbor suppression to prevent tight clustering
            _seenYCoordinates.Add(ry - 1);
            _seenYCoordinates.Add(ry + 1);

            // Bounds check for clipping
            if (req.PanelIndex < 0)
            {
                if (y < (float)layout.ChartArea.Top - ChartTheme.AxisLabelHeight ||
                    y > (float)layout.ChartArea.Bottom + ChartTheme.AxisLabelHeight)
                    continue;
            }
            else
            {
                var area = layout.PanelAreas[req.PanelIndex];
                if (y < (float)area.Top - 2 || y > (float)area.Bottom + 2)
                    continue;
            }

            float priceY = y;
            float boxTopY = MathF.Floor(priceY - ChartTheme.AxisLabelHeight / 2f);
            
            float textWidth = _textPaint.MeasureText(req.Label);
            _resolvedLabels.Add(new ResolvedLabel(
                boxTopY, // Resolved Box Top
                priceY,  // TRUE Price Anchor (for Arrow Tip)
                req.Label,
                req.Color,
                req.Style,
                textWidth
            ));
        }

        if (_resolvedLabels.Count == 0) return;

        _resolvedLabels.Sort();

        float topLimit = (float)layout.ChartArea.Top;
        float lastBottom = (layout.PanelAreas.Count > 0) 
            ? (float)layout.PanelAreas[layout.PanelAreas.Count - 1].Bottom 
            : (float)layout.ChartArea.Bottom;
            
        // Tight clamping to ensure the entire label box (Height) stays within chart area boundaries
        float minTop = topLimit + 1f;
        float maxBottom = lastBottom - ChartTheme.AxisLabelHeight - 1f;
        ResolveOverlaps(_resolvedLabels, minTop, maxBottom);
    }

    /// <summary>
    /// Phase 4: Draw current resolved labels.
    /// </summary>
    public void Draw(SKCanvas canvas, float axisX)
    {
        for (int i = 0; i < _resolvedLabels.Count; i++)
        {
            DrawAxisLabel(canvas, axisX, _resolvedLabels[i]);
        }
    }

    /// <summary>
    /// Gets the Y-ranges currently occupied by resolved labels.
    /// Used for background occlusion (Step 3).
    /// </summary>
    public IReadOnlyList<ResolvedLabel> GetResolvedLabels() => _resolvedLabels;

    /// <summary>
    /// Resolves overlapping labels using a bidirectional averaging sweep.
    /// This centers the labels around their ideal price targets while respecting boundaries.
    /// </summary>
    internal static void ResolveOverlaps(List<ResolvedLabel> labels, float topLimit, float bottomLimit)
    {
        if (labels.Count <= 1) return;

        float spacing = 2f;
        float totalHeight = ChartTheme.AxisLabelHeight + spacing;

        // 1. Top-Down Sweep (Greedy Downward)
        float[] downY = new float[labels.Count];
        downY[0] = Math.Max(topLimit, labels[0].Y);
        for (int i = 1; i < labels.Count; i++)
        {
            downY[i] = Math.Max(downY[i - 1] + totalHeight, labels[i].Y);
        }

        // 2. Bottom-Up Sweep (Greedy Upward)
        float[] upY = new float[labels.Count];
        int last = labels.Count - 1;
        upY[last] = Math.Min(bottomLimit, labels[last].Y);
        for (int i = last - 1; i >= 0; i--)
        {
            upY[i] = Math.Min(upY[i + 1] - totalHeight, labels[i].Y);
        }

        // 3. Average and Clamp
        // The average of down-sweep and up-sweep naturally produces a centered result.
        for (int i = 0; i < labels.Count; i++)
        {
            float avgY = (downY[i] + upY[i]) / 2f;
            labels[i] = labels[i] with { Y = avgY };
        }

        // 4. Final safety pass (ensure no overlap remains due to averaging logic near boundaries)
        for (int i = 1; i < labels.Count; i++)
        {
            if (labels[i].Y < labels[i - 1].Y + totalHeight)
            {
                labels[i] = labels[i] with { Y = labels[i - 1].Y + totalHeight };
            }
        }

        // 5. Final boundary clamp
        if (labels[last].Y > bottomLimit)
        {
            float shift = labels[last].Y - bottomLimit;
            for (int i = 0; i < labels.Count; i++)
            {
                labels[i] = labels[i] with { Y = labels[i].Y - shift };
            }
        }
    }

    private void DrawAxisLabel(SKCanvas canvas, float axisX, ResolvedLabel label)
    {
        float labelWidth = label.TextWidth + ChartTheme.AxisLabelPadding * 2;
        float labelX = axisX + ChartTheme.AxisLabelArrowWidth;
        float labelY = label.Y;

        // Determine colors based on style
        SKColor bgColor;
        SKColor textColor;

        switch (label.Style)
        {
            case AxisLabelStyle.CurrentPrice:
                bgColor = label.Color;
                textColor = GetContrastTextColor(label.Color);
                break;
            case AxisLabelStyle.TargetPrice:
                bgColor = label.Color.WithAlpha(40);
                textColor = label.Color;
                break;
            default:
                bgColor = label.Color.WithAlpha(200);
                textColor = GetContrastTextColor(label.Color);
                break;
        }

        // Draw arrow pointer (triangle pointing left toward the chart)
        // TIP is fixed to OriginalY (Price Center), BASE spans the box from Y to Y+Height
        // RATIO-102: 精密なアライメントを実現するため、矢印の先端（Price位置）とラベルの背景ボックスをピクセル単位でスナップさせる。
        float arrowTipX = MathF.Floor(axisX) + 0.5f;
        float arrowCenterY = MathF.Floor(label.OriginalY) + 0.5f; // FIXED TO PRICE
        _arrowPaint.Color = bgColor;

        float snappedLabelX = MathF.Floor(labelX) + 0.5f;
        float snappedLabelTopY = MathF.Floor(label.Y) + 0.5f;
        float snappedLabelBottomY = snappedLabelTopY + ChartTheme.AxisLabelHeight;

        _arrowPath.Reset();
        _arrowPath.MoveTo(arrowTipX, arrowCenterY);
        _arrowPath.LineTo(snappedLabelX, snappedLabelTopY);
        _arrowPath.LineTo(snappedLabelX, snappedLabelBottomY);
        _arrowPath.Close();
        canvas.DrawPath(_arrowPath, _arrowPaint);

        // Draw rounded rectangle background
        // RATIO-102: Use consistent snapping for the background box to match the arrow tip
        var rect = new SKRect(
            MathF.Floor(labelX) + 0.5f, 
            MathF.Floor(labelY) + 0.5f, 
            MathF.Floor(labelX + labelWidth) + 0.5f, 
            MathF.Floor(labelY + ChartTheme.AxisLabelHeight) + 0.5f);
            
        _backgroundPaint.Color = bgColor;
        canvas.DrawRoundRect(rect, ChartTheme.AxisLabelCornerRadius, ChartTheme.AxisLabelCornerRadius, _backgroundPaint);

        // Draw border for TargetPrice style
        if (label.Style == AxisLabelStyle.TargetPrice)
        {
            _borderPaint.Color = label.Color;
            canvas.DrawRoundRect(rect, ChartTheme.AxisLabelCornerRadius, ChartTheme.AxisLabelCornerRadius, _borderPaint);
        }

        // Draw text
        _textPaint.Color = textColor;
        float textX = labelX + ChartTheme.AxisLabelPadding;
        float textY = labelY + ChartTheme.AxisLabelHeight - ChartTheme.AxisLabelPadding;
        canvas.DrawText(label.Label, textX, textY, _labelFont, _textPaint);
    }

    /// <summary>
    /// Returns white or black text color based on the luminance of the background.
    /// </summary>
    private static SKColor GetContrastTextColor(SKColor background)
    {
        // Relative luminance calculation (simplified)
        float luminance = (0.299f * background.Red + 0.587f * background.Green + 0.114f * background.Blue) / 255f;
        return luminance > 0.5f ? SKColors.Black : SKColors.White;
    }

    public void Dispose()
    {
        _textPaint.Dispose();
        _backgroundPaint.Dispose();
        _borderPaint.Dispose();
        _arrowPaint.Dispose();
        _labelFont.Dispose();
        _arrowPath.Dispose();
    }

    /// <summary>
    /// Internal struct representing a label with its resolved screen position.
    /// </summary>
    public record struct ResolvedLabel(
        float Y,
        float OriginalY,
        string Label,
        SKColor Color,
        AxisLabelStyle Style,
        float TextWidth
    ) : IComparable<ResolvedLabel>
    {
        public int CompareTo(ResolvedLabel other) => Y.CompareTo(other.Y);
    }
}
