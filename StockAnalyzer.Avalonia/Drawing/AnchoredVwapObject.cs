using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Constants;
using StockAnalyzer.Core.Drawing;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Drawing;

public class AnchoredVwapObject : IChartObject, IDisposable, IDrawingCalculatedValuesProvider
{
    public string? CustomName { get; set; }
    public DrawingMoveAxisMode MoveAxisMode { get; set; } = DrawingMoveAxisMode.XY;
    public bool IsMoveAxisModeExplicit { get; set; } = false;
    public Guid Id { get; } = Guid.NewGuid();
    public ChartObjectType Type => ChartObjectType.AnchoredVWAP;

    // Point[0] is the Anchor. Point[1] is just visual handle or optional end?
    // Usually Anchored VWAP goes to end of chart (Right Infinite).
    // Let's say Point[0] is anchor. 
    public List<ChartPoint> Points { get; private set; }
    
    public Color Color { get; set; } = DrawingThemeContext.DefaultColor;
    public double Thickness { get; set; } = DrawingThemeContext.DefaultStrokeThickness;
    public bool IsSelected { get; set; }
    public bool IsVisible { get; set; } = true;
    public bool IsLocked { get; set; } = false;
    public int PanelIndex { get; set; } = -1;
    public int ZIndex { get; set; } = 0;
    public int Priority { get; set; } = 0;
    public int AnchorPointIndex { get; set; } = 0;

    private const decimal DefaultBand1Multiplier = 1.0m;
    private const decimal DefaultBand2Multiplier = IndicatorDefaultConstants.VwapStdDevMultiplier;
    private const decimal MinBandMultiplier = (decimal)IndicatorDefaultConstants.VwapMinStdDevMultiplier;
    private const decimal MaxBandMultiplier = (decimal)IndicatorDefaultConstants.VwapMaxStdDevMultiplier;

    private decimal _band1Multiplier = DefaultBand1Multiplier;
    private decimal _band2Multiplier = DefaultBand2Multiplier;

    /// <summary>
    /// Price source selector used for VWAP calculation (SSoT: PriceType).
    /// Edited via AnchoredVwapPanel's dedicated ComboBox, so excluded from DynamicSettingsView.
    /// </summary>
    [Browsable(false)]
    public PriceType PriceSource { get; set; } = PriceType.Typical;

    [Category("Parameters")]
    [DisplayName("Show Band 1")]
    [ParameterTag(DrawingParameterTags.Analysis)]
    public bool ShowBand1 { get; set; } = false;

    [Category("Parameters")]
    [DisplayName("Band 1 Multiplier")]
    [Range(IndicatorDefaultConstants.VwapMinStdDevMultiplier, IndicatorDefaultConstants.VwapMaxStdDevMultiplier)]
    [ParameterTag(DrawingParameterTags.Analysis)]
    public decimal Band1Multiplier
    {
        get => _band1Multiplier;
        set => _band1Multiplier = Math.Clamp(value, MinBandMultiplier, MaxBandMultiplier);
    }

    [Category("Parameters")]
    [DisplayName("Show Band 2")]
    [ParameterTag(DrawingParameterTags.Analysis)]
    public bool ShowBand2 { get; set; } = false;

    [Category("Parameters")]
    [DisplayName("Band 2 Multiplier")]
    [Range(IndicatorDefaultConstants.VwapMinStdDevMultiplier, IndicatorDefaultConstants.VwapMaxStdDevMultiplier)]
    [ParameterTag(DrawingParameterTags.Analysis)]
    public decimal Band2Multiplier
    {
        get => _band2Multiplier;
        set => _band2Multiplier = Math.Clamp(value, MinBandMultiplier, MaxBandMultiplier);
    }

    // Calculated curve
    [Browsable(false)]
    public List<(DateTime Time, decimal Price)> VwapCurve { get; private set; } = new List<(DateTime, decimal)>();
    private readonly List<decimal?> _stdDevCurve = new();

    private readonly SKPaint _paint = new SKPaint
    {
        IsAntialias = true,
        Style = SKPaintStyle.Stroke
    };
    private readonly SKPath _path = new SKPath();

    public AnchoredVwapObject(ChartPoint anchor)
    {
        Points = new List<ChartPoint> { anchor };
    }

    public void Dispose()
    {
        _paint.Dispose();
        _path.Dispose();
        GC.SuppressFinalize(this);
    }

    public SKColor SkiaColor => new SKColor(Color.R, Color.G, Color.B, Color.A);

    public void Recalculate(IEnumerable<CoreCandleData> candles)
    {
        if (Points.Count < 1) return;

        var anchorTime = Points[0].Time;
        IReadOnlyList<CoreCandleData>? relevantCandles = null;

        if (candles is IReadOnlyList<CoreCandleData> list)
        {
            if (list.Count == 0)
            {
                VwapCurve.Clear();
                _stdDevCurve.Clear();
                return;
            }

            if (IsSortedAscending(list))
            {
                int startIndex = FindFirstIndexAtOrAfter(list, anchorTime);
                if (startIndex < 0)
                {
                    VwapCurve.Clear();
                    _stdDevCurve.Clear();
                    return;
                }

                relevantCandles = startIndex == 0 ? list : new CandleListSlice(list, startIndex);
            }
        }

        // Legacy fallback when the sequence is not an ascending IReadOnlyList<CoreCandleData>.
        if (relevantCandles == null)
        {
            var sorted = candles.Where(c => c.Timestamp >= anchorTime).OrderBy(c => c.Timestamp).ToList();
            if (sorted.Count == 0)
            {
                VwapCurve.Clear();
                _stdDevCurve.Clear();
                return;
            }

            relevantCandles = sorted;
        }

        if (!ShowBand1 && !ShowBand2)
        {
            VwapCurve = VolumeAnalysis.CalculateAnchoredVwap(relevantCandles, PriceSource);
            _stdDevCurve.Clear();
            return;
        }

        var detailed = VolumeAnalysis.CalculateAnchoredVwapWithDeviation(relevantCandles, PriceSource);
        VwapCurve.Clear();
        _stdDevCurve.Clear();
        if (VwapCurve.Capacity < detailed.Count) VwapCurve.Capacity = detailed.Count;
        if (_stdDevCurve.Capacity < detailed.Count) _stdDevCurve.Capacity = detailed.Count;

        for (int i = 0; i < detailed.Count; i++)
        {
            var point = detailed[i];
            VwapCurve.Add((point.Time, point.Vwap));
            _stdDevCurve.Add(point.StdDev);
        }
    }

    private static bool IsSortedAscending(IReadOnlyList<CoreCandleData> list)
    {
        for (int i = 1; i < list.Count; i++)
        {
            if (list[i].Timestamp < list[i - 1].Timestamp) return false;
        }
        return true;
    }

    private static int FindFirstIndexAtOrAfter(IReadOnlyList<CoreCandleData> list, DateTime anchorTime)
    {
        int low = 0;
        int high = list.Count - 1;
        int result = -1;
        while (low <= high)
        {
            int mid = low + ((high - low) >> 1);
            if (list[mid].Timestamp >= anchorTime)
            {
                result = mid;
                high = mid - 1;
            }
            else
            {
                low = mid + 1;
            }
        }
        return result;
    }

    private sealed class CandleListSlice : IReadOnlyList<CoreCandleData>
    {
        private readonly IReadOnlyList<CoreCandleData> _source;
        private readonly int _offset;

        public CandleListSlice(IReadOnlyList<CoreCandleData> source, int offset)
        {
            _source = source;
            _offset = offset;
            Count = source.Count - offset;
        }

        public int Count { get; }

        public CoreCandleData this[int index] => _source[_offset + index];

        public IEnumerator<CoreCandleData> GetEnumerator()
        {
            for (int i = 0; i < Count; i++) yield return _source[_offset + i];
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    public void Render(SKCanvas canvas, ICoordinateTransform transform)
    {
        if (VwapCurve == null || VwapCurve.Count < 2) return;

        _paint.Color = SkiaColor;
        _paint.StrokeWidth = (float)Thickness;
        _paint.PathEffect = null;
        _path.Reset();
        bool isFirst = true;

        foreach (var (time, price) in VwapCurve)
        {
            var screenPt = transform.ChartToScreen(new ChartPoint(time, price));
            if (isFirst)
            {
                _path.MoveTo((float)screenPt.X, (float)screenPt.Y);
                isFirst = false;
            }
            else
            {
                _path.LineTo((float)screenPt.X, (float)screenPt.Y);
            }
        }

        canvas.DrawPath(_path, _paint);

        if ((ShowBand1 || ShowBand2) && _stdDevCurve.Count == VwapCurve.Count)
        {
            _paint.PathEffect = DrawingStrokeStyle.Dash.ToSkPathEffect();
            if (ShowBand1)
            {
                DrawBandCurve(canvas, transform, Band1Multiplier);
                DrawBandCurve(canvas, transform, -Band1Multiplier);
            }
            if (ShowBand2)
            {
                DrawBandCurve(canvas, transform, Band2Multiplier);
                DrawBandCurve(canvas, transform, -Band2Multiplier);
            }
        }

        if (IsSelected)
        {
             // Draw Anchor Point
             var anchor = transform.ChartToScreen(Points[0]);
             SelectionHandleRenderer.Draw(canvas, anchor, AnchorPointIndex == 0 ? DrawingThemeContext.AnchorPointColor : (SKColor?)null, radius: ChartConstants.SelectedHandleRadius);
        }
    }

    private void DrawBandCurve(SKCanvas canvas, ICoordinateTransform transform, decimal signedMultiplier)
    {
        _path.Reset();
        bool hasSegmentPoint = false;
        int count = VwapCurve.Count;

        for (int i = 0; i < count; i++)
        {
            decimal? std = _stdDevCurve[i];
            if (std is not { } sigma)
            {
                hasSegmentPoint = false;
                continue;
            }

            var vwapPoint = VwapCurve[i];
            decimal bandPrice = vwapPoint.Price + signedMultiplier * sigma;
            var screenPt = transform.ChartToScreen(new ChartPoint(vwapPoint.Time, bandPrice));
            if (!double.IsFinite(screenPt.X) || !double.IsFinite(screenPt.Y))
            {
                hasSegmentPoint = false;
                continue;
            }

            if (!hasSegmentPoint)
            {
                _path.MoveTo((float)screenPt.X, (float)screenPt.Y);
                hasSegmentPoint = true;
            }
            else
            {
                _path.LineTo((float)screenPt.X, (float)screenPt.Y);
            }
        }

        canvas.DrawPath(_path, _paint);
    }

    public bool HitTest(global::Avalonia.Point screenPoint, ICoordinateTransform transform, double tolerance = ChartConstants.DefaultHitTestTolerance)
    {
        // 1. Check anchor point first (fast)
        if (Points.Count > 0)
        {
             var anchor = transform.ChartToScreen(Points[0]);
             double dist = Math.Sqrt(Math.Pow(anchor.X - screenPoint.X, 2) + Math.Pow(anchor.Y - screenPoint.Y, 2));
             if (dist < tolerance * 2) return true;
        }

        // 2. Check curve segments
        if (VwapCurve == null || VwapCurve.Count < 2) return false;

        // Optimization: Only check visible or simplified segments?
        // Basic implementation: Iterate all segments.
        global::Avalonia.Point? p1 = null;

        foreach (var (time, price) in VwapCurve)
        {
            var p2 = transform.ChartToScreen(new ChartPoint(time, price));
            
            if (p1.HasValue)
            {
                if (HitTestHelper.IsPointNearLine(screenPoint, p1.Value, p2, tolerance))
                {
                    return true;
                }
            }
            p1 = p2;
        }

        return false;
    }

    // Helper for HitTest logic (consider moving to a shared utility class if reused)
    private static class HitTestHelper
    {
        public static bool IsPointNearLine(global::Avalonia.Point p, global::Avalonia.Point a, global::Avalonia.Point b, double tolerance)
        {
             double l2 = Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2);
             if (l2 == 0) return Math.Sqrt(Math.Pow(p.X - a.X, 2) + Math.Pow(p.Y - a.Y, 2)) <= tolerance;

             double t = ((p.X - a.X) * (b.X - a.X) + (p.Y - a.Y) * (b.Y - a.Y)) / l2;
             t = Math.Max(0, Math.Min(1, t));

             global::Avalonia.Point projection = new global::Avalonia.Point(a.X + t * (b.X - a.X), a.Y + t * (b.Y - a.Y));
             return Math.Sqrt(Math.Pow(p.X - projection.X, 2) + Math.Pow(p.Y - projection.Y, 2)) <= tolerance;
        }
    }

    public void Translate(TimeSpan timeDelta, decimal priceDelta)
    {
        if (Points.Count > 0)
        {
            Points[0] = new ChartPoint(Points[0].Time.Add(timeDelta), Points[0].Price); // Price anchor isn't used for calc but for drawing anchor logic?
            // Actually AVWAP calculates from Time. Price is irrelevant for calculation.
        }
    }

    public IReadOnlyList<DrawingCalculatedValue> GetCalculatedValues(DateTime timestamp, decimal? currentPrice = null)
    {
        if (Points.Count == 0 || VwapCurve == null || VwapCurve.Count == 0) return Array.Empty<DrawingCalculatedValue>();

        var color = new IndicatorColor(Color.A, Color.R, Color.G, Color.B);

        // Find nearest point <= timestamp or fallback to latest
        int matchedIndex = VwapCurve.Count - 1;
        for (int i = VwapCurve.Count - 1; i >= 0; i--)
        {
            if (VwapCurve[i].Time <= timestamp)
            {
                matchedIndex = i;
                break;
            }
        }

        decimal vwapPrice = VwapCurve[matchedIndex].Price;

        if (!ShowBand1 && !ShowBand2)
        {
            return new DrawingCalculatedValue[]
            {
                new DrawingCalculatedValue("AnchorDate", "Anchor Date", null, Points[0].Time.ToString("yyyy/MM/dd"), IndicatorColor.Gray),
                new DrawingCalculatedValue("AnchoredVWAP", "Anchored VWAP", vwapPrice, $"{vwapPrice:F3}", color)
            };
        }

        int extraCount = (ShowBand1 ? 2 : 0) + (ShowBand2 ? 2 : 0);
        var values = new DrawingCalculatedValue[2 + extraCount];
        values[0] = new DrawingCalculatedValue("AnchorDate", "Anchor Date", null, Points[0].Time.ToString("yyyy/MM/dd"), IndicatorColor.Gray);
        values[1] = new DrawingCalculatedValue("AnchoredVWAP", "Anchored VWAP", vwapPrice, $"{vwapPrice:F3}", color);

        decimal? stdDev = _stdDevCurve.Count == VwapCurve.Count ? _stdDevCurve[matchedIndex] : null;
        int writeIdx = 2;
        if (ShowBand1)
        {
            WriteBandCalculatedValues(values, ref writeIdx, "Upper1", "Upper 1", "Lower1", "Lower 1", vwapPrice, stdDev, Band1Multiplier, color);
        }
        if (ShowBand2)
        {
            WriteBandCalculatedValues(values, ref writeIdx, "Upper2", "Upper 2", "Lower2", "Lower 2", vwapPrice, stdDev, Band2Multiplier, color);
        }

        return values;
    }

    private static void WriteBandCalculatedValues(
        DrawingCalculatedValue[] target,
        ref int writeIdx,
        string upperKey,
        string upperLabel,
        string lowerKey,
        string lowerLabel,
        decimal vwapPrice,
        decimal? stdDev,
        decimal multiplier,
        IndicatorColor color)
    {
        if (stdDev is { } sigma)
        {
            decimal offset = multiplier * sigma;
            decimal upper = vwapPrice + offset;
            decimal lower = vwapPrice - offset;
            target[writeIdx++] = new DrawingCalculatedValue(upperKey, upperLabel, upper, string.Create(DrawingValueFormat.Culture, $"{upper:F3}"), color);
            target[writeIdx++] = new DrawingCalculatedValue(lowerKey, lowerLabel, lower, string.Create(DrawingValueFormat.Culture, $"{lower:F3}"), color);
        }
        else
        {
            target[writeIdx++] = new DrawingCalculatedValue(upperKey, upperLabel, null, "-", color);
            target[writeIdx++] = new DrawingCalculatedValue(lowerKey, lowerLabel, null, "-", color);
        }
    }
}

