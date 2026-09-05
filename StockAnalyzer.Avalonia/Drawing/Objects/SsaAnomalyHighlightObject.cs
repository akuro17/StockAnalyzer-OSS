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
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Avalonia.Drawing.Objects;

public class SsaAnomalyHighlightObject : IChartObject, IDrawingCalculatedValuesProvider
{
    public string? CustomName { get; set; }
    public DrawingMoveAxisMode MoveAxisMode { get; set; } = DrawingMoveAxisMode.XY;
    public bool IsMoveAxisModeExplicit { get; set; } = false;
    public Guid Id { get; } = Guid.NewGuid();
    public ChartObjectType Type => ChartObjectType.SsaAnomalyHighlight;

    public List<ChartPoint> Points { get; } = new(2);

    public Color Color { get; set; } = DrawingThemeContext.DefaultColor;
    public double Thickness { get; set; } = DrawingThemeContext.DefaultStrokeThickness;
    public bool IsSelected { get; set; }
    public bool IsVisible { get; set; } = true;
    public bool IsLocked { get; set; } = false;
    public int ZIndex { get; set; } = 0;
    public int AnchorPointIndex { get; set; } = 0;

    // Visual options
    public int HighlightOpacity { get; set; } = 25; // 0-100%
    public Color BullishColor { get; set; } = Color.FromRgb(38, 166, 154); // Greenish Teal
    public Color BearishColor { get; set; } = Color.FromRgb(239, 83, 80);   // Reddish Coral
    public Color StructuralLineColor { get; set; } = Color.FromRgb(0, 191, 255); // DeepSkyBlue
    public bool ShowStructuralLine { get; set; } = true;
    public bool ShowBoundaryBands { get; set; } = true;
    public bool ShowAnomalyBadges { get; set; } = true;

    // SSA Decomposition Parameters
    public int EmbeddingDimension { get; set; } = 15;
    public int NumComponents { get; set; } = 2;
    public bool AutoRank { get; set; } = true;
    public SsaDetrendMode DetrendMethod { get; set; } = SsaDetrendMode.LeastSquaresLinear;
    public PriceType PriceSource { get; set; } = PriceType.Close;

    // Anomaly Detection Parameters
    public double EnterThreshold { get; set; } = 2.0;
    public double ExitThreshold { get; set; } = 1.0;
    public int CoolDownPeriod { get; set; } = 3;
    public int MinDuration { get; set; } = 2;

    public SsaAnomalyResult? CalculatedResult { get; set; }

    public SKColor SkiaColor => new(Color.R, Color.G, Color.B, Color.A);
    public SKColor SkiaBullishColor => new(BullishColor.R, BullishColor.G, BullishColor.B, BullishColor.A);
    public SKColor SkiaBearishColor => new(BearishColor.R, BearishColor.G, BearishColor.B, BearishColor.A);
    public SKColor SkiaStructuralColor => new(StructuralLineColor.R, StructuralLineColor.G, StructuralLineColor.B, StructuralLineColor.A);

    private readonly Renderers.SsaAnomalyHighlightRenderer _renderer = new();

    public void InvalidateCache()
    {
        CalculatedResult = null;
        _renderer.InvalidateCache();
    }

    public void Render(SKCanvas canvas, ICoordinateTransform transform)
    {
        _renderer.Render(canvas, this, transform, IsSelected);
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
            Points[i] = new ChartPoint(
                Points[i].Time + timeDelta,
                Points[i].Price + priceDelta
            );
        }

        InvalidateCache();
    }

    public void Recalculate(IReadOnlyList<CoreCandleData>? candles, TimeSpan timeframe = default)
    {
        if (candles == null || candles.Count == 0 || Points.Count < 2)
        {
            InvalidateCache();
            return;
        }

        DateTime startTime = Points[0].Time < Points[1].Time ? Points[0].Time : Points[1].Time;
        DateTime endTime = Points[0].Time < Points[1].Time ? Points[1].Time : Points[0].Time;

        int startIndex = -1;
        int endIndex = -1;

        for (int i = 0; i < candles.Count; i++)
        {
            if (startIndex == -1 && candles[i].Timestamp >= startTime)
            {
                startIndex = i;
            }
            if (candles[i].Timestamp <= endTime)
            {
                endIndex = i;
            }
        }

        if (startIndex < 0 || endIndex < 0 || startIndex > endIndex)
        {
            InvalidateCache();
            return;
        }

        int count = endIndex - startIndex + 1;
        if (count < SsaAnomalyDetectionEngine.MinSampleCount)
        {
            InvalidateCache();
            return;
        }

        var priceList = new List<double>(count);
        var timeList = new List<DateTime>(count);

        for (int i = startIndex; i <= endIndex; i++)
        {
            priceList.Add((double)PriceDataHelper.ExtractPrice(candles[i], PriceSource));
            timeList.Add(candles[i].Timestamp);
        }

        CalculatedResult = SsaAnomalyDetectionEngine.CalculateAnomaly(
            priceList,
            timeList,
            EmbeddingDimension,
            NumComponents,
            AutoRank,
            DetrendMethod,
            EnterThreshold,
            ExitThreshold,
            CoolDownPeriod,
            MinDuration);
    }

    public IReadOnlyList<DrawingCalculatedValue> GetCalculatedValues(DateTime currentHoverTime = default, decimal? currentHoverPrice = null)
    {
        var result = CalculatedResult;
        if (result == null || result.IsEmpty)
        {
            return Array.Empty<DrawingCalculatedValue>();
        }

        var list = new List<DrawingCalculatedValue>(6);
        var color = new IndicatorColor(Color.A, Color.R, Color.G, Color.B);
        var bullishCol = new IndicatorColor(BullishColor.A, BullishColor.R, BullishColor.G, BullishColor.B);
        var bearishCol = new IndicatorColor(BearishColor.A, BearishColor.R, BearishColor.G, BearishColor.B);

        list.Add(new DrawingCalculatedValue(
            "SSA Anomaly Intervals",
            "SSA Anomaly Intervals",
            result.Intervals.Count,
            $"{result.Intervals.Count} interval(s)",
            color));

        if (result.Intervals.Count > 0)
        {
            // Find absolute max Z
            var maxAnomaly = result.Intervals.OrderByDescending(x => Math.Abs(x.RawPeakZScore != 0 ? x.RawPeakZScore : x.PeakZ)).First();
            string dirStr = maxAnomaly.Direction == SsaAnomalyDirection.Bullish ? "Bullish Spike" : "Bearish Crash";
            var badgeColor = maxAnomaly.Direction == SsaAnomalyDirection.Bullish ? bullishCol : bearishCol;
            double reportedZ = maxAnomaly.RawPeakZScore != 0 ? maxAnomaly.RawPeakZScore : maxAnomaly.PeakZ;

            list.Add(new DrawingCalculatedValue(
                "SSA Max Anomaly Z",
                "SSA Max Anomaly Z",
                (decimal)reportedZ,
                FormattableString.Invariant($"{reportedZ:+0.00;-0.00;0.00}σ ({dirStr})"),
                badgeColor));

            list.Add(new DrawingCalculatedValue(
                "SSA Peak Price Deviation",
                "SSA Peak Price Deviation",
                (decimal)maxAnomaly.MaxPriceDeviation,
                FormattableString.Invariant($"{maxAnomaly.MaxPriceDeviation:+0.00;-0.00;0.00} ({maxAnomaly.PercentDeviation:+0.0;-0.0;0.0}%)"),
                badgeColor));

            var latest = result.Intervals.Last();
            list.Add(new DrawingCalculatedValue(
                "SSA Latest State",
                "SSA Latest State",
                (decimal)latest.PeakZ,
                $"Abnormal ({latest.Direction})",
                latest.Direction == SsaAnomalyDirection.Bullish ? bullishCol : bearishCol));
        }
        else
        {
            list.Add(new DrawingCalculatedValue(
                "SSA Latest State",
                "SSA Latest State",
                0m,
                "Normal",
                bullishCol));
        }

        list.Add(new DrawingCalculatedValue(
            "SSA Residual Noise (σ)",
            "SSA Residual Noise (σ)",
            (decimal)result.ResidualStdDev,
            FormattableString.Invariant($"{result.ResidualStdDev:F3}"),
            color));

        string grade = SsaDiagnostics.GetSeparabilityGrade(result.Separability);
        list.Add(new DrawingCalculatedValue(
            "SSA Separability",
            "SSA Separability",
            (decimal)result.Separability,
            FormattableString.Invariant($"{result.Separability:F1}% ({grade})"),
            result.Separability >= 75.0 ? bullishCol : bearishCol));

        return list;
    }
}
