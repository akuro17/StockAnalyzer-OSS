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

namespace StockAnalyzer.Avalonia.Drawing.Objects;

public class SsaSupportResistanceObject : IChartObject, IDrawingCalculatedValuesProvider
{
    public string? CustomName { get; set; }
    public DrawingMoveAxisMode MoveAxisMode { get; set; } = DrawingMoveAxisMode.XY;
    public bool IsMoveAxisModeExplicit { get; set; } = false;
    public Guid Id { get; } = Guid.NewGuid();
    public virtual ChartObjectType Type => ChartObjectType.SsaSupportResistance;

    // Points[0] = Start Point (Time/Price)
    // Points[1] = End Point (Time/Price)
    public List<ChartPoint> Points { get; } = new(2);

    // Core visual properties
    public Color Color { get; set; } = DrawingThemeContext.DefaultColor;
    public double Thickness { get; set; } = DrawingThemeContext.DefaultStrokeThickness;
    public bool IsSelected { get; set; }
    public bool IsVisible { get; set; } = true;
    public bool IsLocked { get; set; } = false;
    public int ZIndex { get; set; } = 0;
    public int AnchorPointIndex { get; set; } = 0;

    // Selection background band
    public int FillOpacity { get; set; } = 10;
    public Color FillColor { get; set; } = DrawingThemeContext.DefaultColor;

    // Operational mode
    public SsaSupportResistanceMode Mode { get; set; } = SsaSupportResistanceMode.StructuralPivots;

    // Color customization for S/R and center lines
    public Color ResistanceColor { get; set; } = Color.FromRgb(239, 83, 80);  // Red / Bearish
    public Color SupportColor { get; set; } = Color.FromRgb(38, 166, 154);    // Green / Bullish
    public Color CenterLineColor { get; set; } = Color.FromRgb(41, 98, 255);  // Blue

    // Core SSA decomposition parameters
    public int EmbeddingDimension { get; set; } = 15;
    public int NumComponents { get; set; } = 2;
    public bool AutoRank { get; set; } = true;
    public SsaDetrendMode DetrendMethod { get; set; } = SsaDetrendMode.LeastSquaresLinear;
    public PriceType PriceSource { get; set; } = PriceType.Median;

    // Mode 1: Structural Pivots parameters
    public int MaxLevelsPerSide { get; set; } = 2;
    public decimal ClusterTolerance { get; set; } = 0.5m;
    public bool ExtendLinesToRight { get; set; } = true;

    // Mode 2: Dynamic Envelopes parameters
    public decimal Multiplier { get; set; } = 2.0m;
    public int ChannelFillOpacity { get; set; } = 15;

    // Mode 3: Projected Targets parameters
    public int FutureSteps { get; set; } = 20;
    public SsaForecastMode ForecastMode { get; set; } = SsaForecastMode.Vector;

    // Calculation result cache
    public SsaSupportResistanceResult? CalculatedResult { get; set; }

    public SkiaSharp.SKColor SkiaColor => new(Color.R, Color.G, Color.B, Color.A);
    public SkiaSharp.SKColor SkiaFillColor => new(FillColor.R, FillColor.G, FillColor.B, FillColor.A);
    public SkiaSharp.SKColor SkiaResistanceColor => new(ResistanceColor.R, ResistanceColor.G, ResistanceColor.B, ResistanceColor.A);
    public SkiaSharp.SKColor SkiaSupportColor => new(SupportColor.R, SupportColor.G, SupportColor.B, SupportColor.A);
    public SkiaSharp.SKColor SkiaCenterColor => new(CenterLineColor.R, CenterLineColor.G, CenterLineColor.B, CenterLineColor.A);

    private readonly Renderers.SsaSupportResistanceRenderer _renderer = new();

    public SsaSupportResistanceObject()
    {
    }

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

        if (ExtendLinesToRight && CalculatedResult != null && !CalculatedResult.IsEmpty && Mode == SsaSupportResistanceMode.StructuralPivots)
        {
            maxX = transform.CanvasWidth;
        }

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

    public void Recalculate(IReadOnlyList<CoreCandleData>? candles, TimeSpan timeframeSpan = default)
    {
        if (candles == null || candles.Count == 0 || Points.Count < 2)
        {
            CalculatedResult = SsaSupportResistanceResult.Empty;
            return;
        }

        var t1 = Points[0].Time;
        var t2 = Points[1].Time;
        var startTime = t1 < t2 ? t1 : t2;
        var endTime = t1 > t2 ? t1 : t2;

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
            CalculatedResult = SsaSupportResistanceResult.Empty;
            return;
        }

        int count = endIndex - startIndex + 1;
        if (count < SsaSupportResistanceEngine.MinSampleCount)
        {
            CalculatedResult = SsaSupportResistanceResult.Empty;
            return;
        }

        Func<CoreCandleData, double> selector = c => (double)PriceDataHelper.ExtractPrice(c, PriceSource);

        var samples = new List<double>(count);
        var timestamps = new List<DateTime>(count);

        for (int i = startIndex; i <= endIndex; i++)
        {
            samples.Add(selector(candles[i]));
            timestamps.Add(candles[i].Timestamp);
        }

        double? currentPrice = candles.Count > 0 ? (double?)candles[^1].Close : null;

        CalculatedResult = SsaSupportResistanceEngine.Calculate(
            samples: samples,
            timestamps: timestamps,
            mode: Mode,
            embeddingDimension: EmbeddingDimension,
            numComponents: NumComponents,
            autoRank: AutoRank,
            detrendMode: DetrendMethod,
            maxLevelsPerSide: MaxLevelsPerSide,
            clusterTolerance: ClusterTolerance,
            multiplier: Multiplier,
            futureSteps: FutureSteps,
            forecastMode: ForecastMode,
            timeframeSpan: timeframeSpan,
            currentPrice: currentPrice);
    }

    public IReadOnlyList<DrawingCalculatedValue> GetCalculatedValues(DateTime timestamp, decimal? currentPrice = null)
    {
        var result = CalculatedResult;
        if (result == null || result.IsEmpty) return Array.Empty<DrawingCalculatedValue>();

        var color = new IndicatorColor(Color.A, Color.R, Color.G, Color.B);
        var resColor = new IndicatorColor(ResistanceColor.A, ResistanceColor.R, ResistanceColor.G, ResistanceColor.B);
        var supColor = new IndicatorColor(SupportColor.A, SupportColor.R, SupportColor.G, SupportColor.B);

        var list = new List<DrawingCalculatedValue>
        {
            new DrawingCalculatedValue(
                "SSA S/R Mode",
                "SSA S/R Mode",
                (decimal)Mode,
                Mode.ToString(),
                color)
        };

        if (result.ActiveResistance.HasValue)
        {
            list.Add(new DrawingCalculatedValue(
                "SSA Active Resistance",
                "SSA Active Resistance",
                (decimal)result.ActiveResistance.Value,
                $"{result.ActiveResistance.Value:F2}",
                resColor));
        }
        else
        {
            list.Add(new DrawingCalculatedValue(
                "SSA Active Resistance",
                "SSA Active Resistance",
                0m,
                "None",
                color));
        }

        if (result.ActiveSupport.HasValue)
        {
            list.Add(new DrawingCalculatedValue(
                "SSA Active Support",
                "SSA Active Support",
                (decimal)result.ActiveSupport.Value,
                $"{result.ActiveSupport.Value:F2}",
                supColor));
        }
        else
        {
            list.Add(new DrawingCalculatedValue(
                "SSA Active Support",
                "SSA Active Support",
                0m,
                "None",
                color));
        }

        if (result.ActiveResistance.HasValue && result.ActiveSupport.HasValue)
        {
            double spread = result.ActiveResistance.Value - result.ActiveSupport.Value;
            double mid = (result.ActiveResistance.Value + result.ActiveSupport.Value) * 0.5;
            double pct = mid > 0 ? (spread / mid) * 100.0 : 0.0;
            list.Add(new DrawingCalculatedValue(
                "SSA S/R Spread",
                "SSA S/R Spread",
                (decimal)spread,
                $"{spread:F2} ({pct:F1}%)",
                color));
        }

        list.Add(new DrawingCalculatedValue(
            "SSA Residual Noise (σ)",
            "SSA Residual Noise (σ)",
            (decimal)result.ResidualStdDev,
            $"{result.ResidualStdDev:F2}",
            color));

        string grade = SsaDiagnostics.GetSeparabilityGrade(result.SeparabilityScore);
        list.Add(new DrawingCalculatedValue(
            "SSA Separability",
            "SSA Separability",
            (decimal)result.SeparabilityScore,
            $"{result.SeparabilityScore:F1}% ({grade})",
            result.SeparabilityScore >= 75.0 ? IndicatorColor.Bullish : IndicatorColor.Bearish));

        return list;
    }
}
