using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// A 2-point range drawing tool that performs FFT spectral analysis on candles within
/// the selected time range and displays a frequency/period power bar graph within the bounding box.
/// </summary>
public class FftSpectrumObject : IChartObject, IDisposable, IDrawingCalculatedValuesProvider
{
    public string? CustomName { get; set; }
    public DrawingMoveAxisMode MoveAxisMode { get; set; } = DrawingMoveAxisMode.XY;
    public bool IsMoveAxisModeExplicit { get; set; } = false;
    public Guid Id { get; } = Guid.NewGuid();
    public ChartObjectType Type => ChartObjectType.FftSpectrum;

    public List<ChartPoint> Points { get; private set; }
    public Color Color { get; set; } = DrawingThemeContext.DefaultColor;
    public Color FillColor { get; set; } = DrawingThemeContext.DefaultColor;
    public int FillOpacity { get; set; } = 10;
    public Color PeakColor { get; set; } = Colors.Orange;
    public double Thickness { get; set; } = DrawingThemeContext.DefaultStrokeThickness;
    public bool IsSelected { get; set; }
    public bool IsVisible { get; set; } = true;
    public bool IsLocked { get; set; } = false;
    public int ZIndex { get; set; } = 0;
    public int AnchorPointIndex { get; set; } = 0;

    // Analysis Configuration
    public double Opacity { get; set; } = 0.6;
    public bool ApplyDetrend { get; set; } = true;
    public bool ApplyWindow { get; set; } = true;
    public double MinPeriod { get; set; } = 2.0;
    public double MaxPeriod { get; set; } = 100.0;

    /// <summary>
    /// Price source selector used for Fourier spectrum analysis (SSoT: PriceType).
    /// </summary>
    public PriceType PriceSource { get; set; } = PriceType.Median;

    /// <summary>
    /// Backward-compatibility property for PriceField.
    /// </summary>
    public PriceField PriceField
    {
        get => PriceSource switch
        {
            PriceType.Close => PriceField.Close,
            PriceType.Open => PriceField.Open,
            PriceType.High => PriceField.High,
            PriceType.Low => PriceField.Low,
            PriceType.Median => PriceField.MedianHL,
            PriceType.Typical => PriceField.TypicalHLC,
            PriceType.Weighted => PriceField.WeightedHLCC,
            _ => PriceField.MedianHL
        };
        set => PriceSource = value switch
        {
            PriceField.Close => PriceType.Close,
            PriceField.Open => PriceType.Open,
            PriceField.High => PriceType.High,
            PriceField.Low => PriceType.Low,
            PriceField.MedianHL => PriceType.Median,
            PriceField.TypicalHLC => PriceType.Typical,
            PriceField.WeightedHLCC => PriceType.Weighted,
            _ => PriceType.Median
        };
    }

    // Output Data
    public IReadOnlyList<FftSpectrumBin> SpectrumBins { get; private set; } = Array.Empty<FftSpectrumBin>();
    public double DominantPeriod { get; private set; }
    public double DominantPower { get; private set; }

    public FftSpectrumObject(ChartPoint p1, ChartPoint p2)
    {
        Points = new List<ChartPoint> { p1, p2 };
    }

    public SKColor SkiaColor => new SKColor(Color.R, Color.G, Color.B, Color.A);
    public SKColor SkiaFillColor => new SKColor(FillColor.R, FillColor.G, FillColor.B, FillColor.A);
    public SKColor SkiaPeakColor => new SKColor(PeakColor.R, PeakColor.G, PeakColor.B, (byte)(255 * Opacity));
    public SKColor SkiaBarColor => new SKColor(Color.R, Color.G, Color.B, (byte)(255 * Opacity));

    // ZeroAllocation cached render paints
    private readonly SKPaint _previewPaint = new SKPaint { Style = SKPaintStyle.Stroke, IsAntialias = true };
    private readonly SKPaint _boxBgPaint = new SKPaint { Style = SKPaintStyle.Fill };
    private readonly SKPaint _boxBorderPaint = new SKPaint { Style = SKPaintStyle.Stroke, IsAntialias = true };
    private readonly SKPaint _barPaint = new SKPaint { Style = SKPaintStyle.Fill };
    private readonly SKPaint _peakBarPaint = new SKPaint { Style = SKPaintStyle.Fill };
    private readonly SKPaint _textPaint = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true };

    public void Dispose()
    {
        _previewPaint.Dispose();
        _boxBgPaint.Dispose();
        _boxBorderPaint.Dispose();
        _barPaint.Dispose();
        _peakBarPaint.Dispose();
        _textPaint.Dispose();
        GC.SuppressFinalize(this);
    }

    public void Recalculate(IEnumerable<CoreCandleData> candles)
    {
        if (Points.Count < 2) return;

        var t1 = Points[0].Time;
        var t2 = Points[1].Time;
        var start = t1 < t2 ? t1 : t2;
        var end = t1 < t2 ? t2 : t1;

        var range = candles.Where(c => c.Timestamp >= start && c.Timestamp <= end).ToList();
        if (range.Count < FftSpectrumAnalysis.MinSampleCount)
        {
            SpectrumBins = Array.Empty<FftSpectrumBin>();
            DominantPeriod = 0;
            DominantPower = 0;
            return;
        }

        Func<CoreCandleData, double> selector = c => (double)PriceDataHelper.ExtractPrice(c, PriceSource);

        var result = FftSpectrumAnalysis.CalculateSpectrum(
            range,
            selector,
            applyDetrend: ApplyDetrend,
            applyHanningWindow: ApplyWindow,
            minPeriod: MinPeriod,
            maxPeriod: MaxPeriod);

        SpectrumBins = result.Bins;
        DominantPeriod = result.DominantPeriod;
        DominantPower = result.DominantPower;
    }

    public void Render(SKCanvas canvas, ICoordinateTransform transform)
    {
        if (Points.Count < 2) return;

        var p1 = transform.ChartToScreen(Points[0]);
        var p2 = transform.ChartToScreen(Points[1]);

        var clip = canvas.LocalClipBounds;
        float x1 = (float)p1.X;
        float x2 = (float)p2.X;
        float left = Math.Min(x1, x2);
        float right = Math.Max(x1, x2);
        float width = right - left;
        float top = clip.Top;
        float bottom = clip.Bottom;
        float height = bottom - top;

        // 1. Draw Selection Background Band (matching HarmonicPattern)
        var bandRect = new SKRect(left, top, right, bottom);
        _boxBgPaint.Color = new SKColor(FillColor.R, FillColor.G, FillColor.B, (byte)(255 * FillOpacity / 100.0));
        canvas.DrawRect(bandRect, _boxBgPaint);

        // 2. Draw Full-Height Vertical Lines (matching HarmonicPattern)
        _boxBorderPaint.Color = IsSelected ? SkiaColor : SkiaColor.WithAlpha(180);
        _boxBorderPaint.StrokeWidth = IsSelected ? (float)Thickness + 1 : (float)Thickness;
        canvas.DrawLine(x1, top, x1, bottom, _boxBorderPaint);
        canvas.DrawLine(x2, top, x2, bottom, _boxBorderPaint);

        // 3. Draw Spectrum Bars inside the vertical band
        int count = SpectrumBins.Count;
        if (count > 0 && width > 4 && height > 40)
        {
            float maxH = Math.Min(180f, height * 0.35f);
            float barAreaBottom = bottom - 10f;
            float barAreaTop = barAreaBottom - maxH;
            float barSlotWidth = width / count;

            _barPaint.Color = SkiaBarColor;
            _peakBarPaint.Color = SkiaPeakColor;

            for (int i = 0; i < count; i++)
            {
                var bin = SpectrumBins[i];
                float barLeft = left + i * barSlotWidth + 1;
                float barRight = barLeft + Math.Max(1f, barSlotWidth - 1);
                float barHeight = (float)(bin.NormalizedPower * maxH);
                float barTop = barAreaBottom - barHeight;

                var barRect = new SKRect(barLeft, barTop, barRight, barAreaBottom);
                canvas.DrawRect(barRect, bin.IsDominant ? _peakBarPaint : _barPaint);
            }

            // Draw Dominant Header text
            if (DominantPeriod > 0)
            {
                _textPaint.Color = DrawingThemeContext.MainTextSkColor;
                _textPaint.TextSize = DrawingThemeContext.DrawingFontSize;
                string label = $"FFT Dominant: {DominantPeriod:F1} bars (Power: {DominantPower:F1})";
                canvas.DrawText(label, left + 6, barAreaTop - 6, _textPaint);
            }
        }

        // 4. Draw selection handles
        SelectionHandleRenderer.Draw(canvas, p1, AnchorPointIndex == 0 ? DrawingThemeContext.AnchorPointColor : (SKColor?)null, radius: ChartConstants.SelectedHandleRadius);
        SelectionHandleRenderer.Draw(canvas, p2, AnchorPointIndex == 1 ? DrawingThemeContext.AnchorPointColor : (SKColor?)null, radius: ChartConstants.SelectedHandleRadius);
    }

    public bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
        if (Points.Count < 2) return false;

        var p1 = transform.ChartToScreen(Points[0]);
        var p2 = transform.ChartToScreen(Points[1]);

        double minX = Math.Min(p1.X, p2.X) - tolerance;
        double maxX = Math.Max(p1.X, p2.X) + tolerance;

        return screenPoint.X >= minX && screenPoint.X <= maxX;
    }

    public void Translate(TimeSpan timeDelta, decimal priceDelta)
    {
        for (int i = 0; i < Points.Count; i++)
        {
            Points[i] = new ChartPoint(Points[i].Time.Add(timeDelta), Points[i].Price + priceDelta);
        }
    }

    public IReadOnlyList<DrawingCalculatedValue> GetCalculatedValues(DateTime timestamp, decimal? currentPrice = null)
    {
        if (SpectrumBins == null || SpectrumBins.Count == 0) return Array.Empty<DrawingCalculatedValue>();

        var color = new IndicatorColor(Color.A, Color.R, Color.G, Color.B);
        var peakColor = new IndicatorColor(PeakColor.A, PeakColor.R, PeakColor.G, PeakColor.B);

        return new DrawingCalculatedValue[]
        {
            new DrawingCalculatedValue("FFT Dominant Period", "FFT Dominant Period", (decimal)DominantPeriod, $"{DominantPeriod:F1} bars", peakColor),
            new DrawingCalculatedValue("FFT Dominant Power", "FFT Dominant Power", (decimal)DominantPower, $"{DominantPower:F2}", color),
            new DrawingCalculatedValue("FFT Sample Bins", "FFT Sample Bins", SpectrumBins.Count, $"{SpectrumBins.Count} bins", color)
        };
    }
}
