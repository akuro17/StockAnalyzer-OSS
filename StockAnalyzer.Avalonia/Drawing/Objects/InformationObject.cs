using System;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Views.Chart.Renderers;

namespace StockAnalyzer.Avalonia.Drawing.Objects;

/// <summary>
/// Information drawing tool object.
/// Displays an on-chart HUD card with candle data, indicators, and drawings at the top-left of the chart.
/// Managed via ChartObjectManager and displayed in the Layers Panel.
/// Provides individual configuration for Color, LineThickness, Fill Color, Fill Opacity, Font Color, and Font Size.
/// </summary>
public class InformationObject : RelativeGeometricRenderer, IDisposable
{
    public override ChartObjectType Type => ChartObjectType.Information;

    private Color? _customFillColor;
    /// <summary>
    /// Background fill color. Defaults to theme application background.
    /// </summary>
    public Color FillColor
    {
        get => _customFillColor ?? DrawingThemeContext.AppBackgroundColor;
        set => _customFillColor = value;
    }
    public bool HasCustomFillColor => _customFillColor.HasValue;

    /// <summary>
    /// Background opacity (0 to 100%). Default is 95%.
    /// </summary>
    public int FillOpacity { get; set; } = 95;

    private Color? _customFontColor;
    /// <summary>
    /// Text font color. Defaults to theme main text color, allowing individual customization.
    /// </summary>
    public Color FontColor
    {
        get => _customFontColor ?? DrawingThemeContext.MainTextColor;
        set => _customFontColor = value;
    }
    public bool HasCustomFontColor => _customFontColor.HasValue;

    private double? _customFontSize;
    /// <summary>
    /// Detail text font size. Defaults to theme detail font size, allowing individual customization.
    /// </summary>
    public double FontSize
    {
        get => _customFontSize ?? (double)DrawingThemeContext.DetailFontSize;
        set => _customFontSize = value;
    }
    public bool HasCustomFontSize => _customFontSize.HasValue;

    /// <summary>
    /// Current displayed snapshot conforming to DataWindow presentation values.
    /// Ephemeral runtime data, ignored during JSON serialization.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public ChartInformationSnapshot? Snapshot { get; set; }

    /// <summary>
    /// Last rendered bounds in local chart coordinates (used for hit testing).
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Rect LastRenderedBounds { get; set; }

    // Cached SkiaSharp rendering resources
    private readonly SKPaint _bgPaint;
    private readonly SKPaint _borderPaint;
    private readonly SKPaint _separatorPaint;
    private readonly SKPaint _headerTextPaint;
    private readonly SKPaint _labelPaint;
    private readonly SKPaint _valuePaint;
    private readonly SKPaint _colorValuePaint;
    private readonly SKPaint _itemDotPaint;
    private readonly SKPaint _shadowPaint;
    private readonly SKFont _headerFont;
    private readonly SKFont _detailFont;
    private bool _disposed;

    public InformationObject() : base()
    {
        Color = DrawingThemeContext.DefaultColor;
        Thickness = 1.0;

        _bgPaint = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };

        _borderPaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1f,
            IsAntialias = true
        };

        _separatorPaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1f,
            IsAntialias = true
        };

        _headerTextPaint = new SKPaint
        {
            IsAntialias = true
        };

        _labelPaint = new SKPaint
        {
            IsAntialias = true
        };

        _valuePaint = new SKPaint
        {
            IsAntialias = true
        };

        _colorValuePaint = new SKPaint
        {
            IsAntialias = true
        };

        _itemDotPaint = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };

        _shadowPaint = new SKPaint
        {
            Color = new SKColor(0, 0, 0, 50),
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };

        _headerFont = new SKFont(SKTypeface.Default, (float)Math.Max(9.0, FontSize * 0.9));
        _detailFont = new SKFont(SKTypeface.Default, (float)FontSize);
    }

    protected override void DrawGeometry(SKCanvas canvas, ICoordinateTransform transform)
    {
        if (Snapshot == null) return;

        // Configure dynamic sizes and paints
        float detailFontSize = (float)FontSize;
        float headerFontSize = (float)Math.Max(9.0, detailFontSize * 0.9);
        _detailFont.Size = detailFontSize;
        _headerFont.Size = headerFontSize;

        var fontSk = new SKColor(FontColor.R, FontColor.G, FontColor.B, FontColor.A);
        var mainBorderSk = SkiaColor;
        var fillAlpha = (byte)Math.Clamp((int)(FillOpacity * 255.0 / 100.0), 0, 255);

        _bgPaint.Color = new SKColor(FillColor.R, FillColor.G, FillColor.B, fillAlpha);
        _borderPaint.Color = mainBorderSk;
        _borderPaint.StrokeWidth = (float)Thickness;
        _separatorPaint.Color = mainBorderSk.WithAlpha(80);

        _valuePaint.Color = fontSk;
        _valuePaint.TextSize = detailFontSize;
        _labelPaint.Color = fontSk.WithAlpha(200);
        _labelPaint.TextSize = detailFontSize;
        _colorValuePaint.TextSize = detailFontSize;
        _headerTextPaint.Color = fontSk.WithAlpha(200);
        _headerTextPaint.TextSize = headerFontSize;

        // Measure content layout
        float padX = 10f;
        float padY = 8f;
        float lineHeight = detailFontSize + 5f;
        float headerHeight = headerFontSize + 4f;

        float maxContentWidth = 180f;

        // Date Header
        string dateStr = Snapshot.Candle?.DateText ?? Snapshot.Timestamp.ToString("yyyy/MM/dd HH:mm");
        float dateWidth = _headerTextPaint.MeasureText(dateStr);
        if (dateWidth > maxContentWidth) maxContentWidth = dateWidth;

        int candleRows = 0;
        if (Snapshot.Candle != null)
        {
            candleRows = 3; // O/H, L/C, Vol
            if (!string.IsNullOrEmpty(Snapshot.Candle.YesterdayChangeText))
            {
                candleRows++; // Chg
            }
        }

        var indicators = Snapshot.Indicators;
        int indicatorRows = indicators.Count;
        for (int i = 0; i < indicatorRows; i++)
        {
            var ind = indicators[i];
            float w = _labelPaint.MeasureText(ind.Name) + _valuePaint.MeasureText(ind.FormattedValue) + 30f;
            if (w > maxContentWidth) maxContentWidth = w;
        }

        var drawings = Snapshot.Drawings;
        int drawingRows = drawings.Count;
        for (int i = 0; i < drawingRows; i++)
        {
            var drw = drawings[i];
            float w = _labelPaint.MeasureText(drw.FullLabel) + _valuePaint.MeasureText(drw.FormattedValue) + 30f;
            if (w > maxContentWidth) maxContentWidth = w;
        }

        float cardWidth = Math.Min(340f, Math.Max(200f, maxContentWidth + padX * 2));
        float totalHeight = padY * 2 + headerHeight;
        if (candleRows > 0) totalHeight += 4f + candleRows * lineHeight;
        if (indicatorRows > 0) totalHeight += 6f + indicatorRows * lineHeight;
        if (drawingRows > 0) totalHeight += 6f + drawingRows * lineHeight;

        float cardHeight = totalHeight;

        // Position fixed at top-left
        var chartArea = new Rect(0, 0, transform.CanvasWidth, transform.CanvasHeight);
        var cardPos = InformationRenderer.CalculateCardPosition(chartArea, cardWidth, cardHeight);
        float boxX = cardPos.X;
        float boxY = cardPos.Y;
        LastRenderedBounds = new Rect(boxX, boxY, cardWidth, cardHeight);

        // Control point is placed at the top-left corner of the Information card
        var topLeftChart = transform.ScreenToChart(new global::Avalonia.Point(boxX, boxY));
        if (Points.Count == 0)
        {
            Points.Add(topLeftChart);
        }
        else
        {
            Points[0] = topLeftChart;
        }

        // 1. Drop Shadow
        var shadowRect = new SKRect(boxX + 2f, boxY + 3f, boxX + cardWidth + 2f, boxY + cardHeight + 3f);
        canvas.DrawRoundRect(shadowRect, 6f, 6f, _shadowPaint);

        // 2. Card Background & Border (always drawn in configured Color without selection-color overwrite)
        var cardRect = new SKRect(boxX, boxY, boxX + cardWidth, boxY + cardHeight);
        canvas.DrawRoundRect(cardRect, 6f, 6f, _bgPaint);
        canvas.DrawRoundRect(cardRect, 6f, 6f, _borderPaint);

        // 3. Draw Content Rows
        float currentY = boxY + padY + _headerFont.Size;
        float leftTextX = boxX + padX;
        float rightTextX = boxX + cardWidth - padX;

        // Header: Date
        canvas.DrawText(dateStr, leftTextX, currentY, _headerFont, _headerTextPaint);
        currentY += headerHeight - _headerFont.Size + 4f;

        // Candle OHLCV
        if (Snapshot.Candle != null)
        {
            canvas.DrawLine(leftTextX, currentY - 2f, rightTextX, currentY - 2f, _separatorPaint);
            currentY += 4f;

            var c = Snapshot.Candle;
            float col2X = leftTextX + (cardWidth - padX * 2) * 0.5f;

            // Row 1: O / H
            canvas.DrawText($"O: {c.OpenText}", leftTextX, currentY, _detailFont, _valuePaint);
            canvas.DrawText($"H: {c.HighText}", col2X, currentY, _detailFont, _valuePaint);
            currentY += lineHeight;

            // Row 2: L / C
            canvas.DrawText($"L: {c.LowText}", leftTextX, currentY, _detailFont, _valuePaint);
            canvas.DrawText($"C: {c.CloseText}", col2X, currentY, _detailFont, _valuePaint);
            currentY += lineHeight;

            // Row 3: Vol
            canvas.DrawText($"Vol: {c.VolumeText}", leftTextX, currentY, _detailFont, _labelPaint);
            currentY += lineHeight;

            // Row 4: Change
            if (!string.IsNullOrEmpty(c.YesterdayChangeText))
            {
                _colorValuePaint.Color = c.YesterdayChangeColor.ToSkColor();
                string chgText = $"Chg: {c.YesterdayChangeText} ({c.YesterdayChangeRatioText})";
                canvas.DrawText(chgText, leftTextX, currentY, _detailFont, _colorValuePaint);
                currentY += lineHeight;
            }
        }

        // Indicators
        if (indicatorRows > 0)
        {
            canvas.DrawLine(leftTextX, currentY - 2f, rightTextX, currentY - 2f, _separatorPaint);
            currentY += 4f;

            for (int i = 0; i < indicatorRows; i++)
            {
                var ind = indicators[i];
                // Dot
                _itemDotPaint.Color = ind.Color.ToSkColor();
                canvas.DrawCircle(leftTextX + 3f, currentY - _detailFont.Size * 0.35f, 3f, _itemDotPaint);

                // Value (Right-aligned)
                float valWidth = _valuePaint.MeasureText(ind.FormattedValue);
                canvas.DrawText(ind.FormattedValue, rightTextX - valWidth, currentY, _detailFont, _valuePaint);

                // Label (Truncated with "..." if too long to prevent overlapping with value)
                float labelStartX = leftTextX + 12f;
                float spacing = 8f;
                float maxLabelWidth = Math.Max(0f, (rightTextX - valWidth - spacing) - labelStartX);
                string labelText = InformationRenderer.TruncateWithEllipsis(ind.Name, maxLabelWidth, _labelPaint);
                canvas.DrawText(labelText, labelStartX, currentY, _detailFont, _labelPaint);

                currentY += lineHeight;
            }
        }

        // Drawings
        if (drawingRows > 0)
        {
            canvas.DrawLine(leftTextX, currentY - 2f, rightTextX, currentY - 2f, _separatorPaint);
            currentY += 4f;

            for (int i = 0; i < drawingRows; i++)
            {
                var drw = drawings[i];
                // Dot
                _itemDotPaint.Color = drw.Color.ToSkColor();
                canvas.DrawCircle(leftTextX + 3f, currentY - _detailFont.Size * 0.35f, 3f, _itemDotPaint);

                // Value (Right-aligned)
                float valWidth = _valuePaint.MeasureText(drw.FormattedValue);
                canvas.DrawText(drw.FormattedValue, rightTextX - valWidth, currentY, _detailFont, _valuePaint);

                // Label (Truncated with "..." if too long to prevent overlapping with value)
                float labelStartX = leftTextX + 12f;
                float spacing = 8f;
                float maxLabelWidth = Math.Max(0f, (rightTextX - valWidth - spacing) - labelStartX);
                string labelText = InformationRenderer.TruncateWithEllipsis(drw.FullLabel, maxLabelWidth, _labelPaint);
                canvas.DrawText(labelText, labelStartX, currentY, _detailFont, _labelPaint);

                currentY += lineHeight;
            }
        }
    }

    public override bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
        if (!IsVisible) return false;

        // 1. Hit test the top-left card bounds
        if (LastRenderedBounds.Width > 0 && LastRenderedBounds.Height > 0)
        {
            var inflated = LastRenderedBounds.Inflate(tolerance);
            if (inflated.Contains(screenPoint)) return true;
        }

        // 2. Hit test the control point at the top-left corner
        if (Points.Count > 0)
        {
            var anchor = transform.ChartToScreen(Points[0]);
            double dx = screenPoint.X - anchor.X;
            double dy = screenPoint.Y - anchor.Y;
            if (Math.Sqrt(dx * dx + dy * dy) <= tolerance + 4.0) return true;
        }

        return false;
    }

    public override void Translate(TimeSpan timeDelta, decimal priceDelta)
    {
        // Information card is anchored to the top-left of the chart view; no-op translation preserves fixed position
    }

    public override void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _bgPaint.Dispose();
        _borderPaint.Dispose();
        _separatorPaint.Dispose();
        _headerTextPaint.Dispose();
        _labelPaint.Dispose();
        _valuePaint.Dispose();
        _colorValuePaint.Dispose();
        _itemDotPaint.Dispose();
        _shadowPaint.Dispose();
        _headerFont.Dispose();
        _detailFont.Dispose();

        base.Dispose();
    }
}
