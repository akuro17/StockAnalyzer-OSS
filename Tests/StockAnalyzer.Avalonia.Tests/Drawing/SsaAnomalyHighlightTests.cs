using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Avalonia.Views.Dialogs;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

public class SsaAnomalyHighlightTests
{
    private static List<CoreCandleData> MakeSampleCandles(int count, decimal startPrice = 100m)
    {
        var list = new List<CoreCandleData>();
        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < count; i++)
        {
            var p = startPrice + 0.1m * i + (decimal)Math.Sin(i * 0.4) * 10m;
            // Inject sudden shock at i = 40..43
            if (i >= 40 && i <= 43)
            {
                p -= 20m;
            }

            list.Add(new CoreCandleData(
                baseTime.AddHours(i),
                p - 1m,
                p + 2m,
                p - 2m,
                p,
                1000
            ));
        }
        return list;
    }

    [Fact]
    public void SsaAnomalyHighlightObject_InitialPlacement_Defaults()
    {
        var obj = new SsaAnomalyHighlightObject();

        Assert.Equal(ChartObjectType.SsaAnomalyHighlight, obj.Type);
        Assert.Equal(DrawingThemeContext.DefaultColor, obj.Color);
        Assert.Equal(25, obj.HighlightOpacity);
        Assert.Equal(15, obj.EmbeddingDimension);
        Assert.Equal(2, obj.NumComponents);
        Assert.True(obj.AutoRank);
        Assert.Equal(SsaDetrendMode.LeastSquaresLinear, obj.DetrendMethod);
        Assert.Equal(PriceType.Close, obj.PriceSource);
        Assert.Equal(2.0, obj.EnterThreshold);
        Assert.Equal(1.0, obj.ExitThreshold);
        Assert.Equal(3, obj.CoolDownPeriod);
        Assert.Equal(2, obj.MinDuration);
        Assert.True(obj.ShowStructuralLine);
        Assert.True(obj.ShowBoundaryBands);
        Assert.True(obj.ShowAnomalyBadges);
        Assert.Null(obj.CalculatedResult);
    }

    [Fact]
    public void SsaAnomalyHighlightObject_Recalculate_ExtractsResultAndDataWindow()
    {
        var candles = MakeSampleCandles(80);
        var obj = new SsaAnomalyHighlightObject
        {
            EmbeddingDimension = 15,
            NumComponents = 2,
            AutoRank = false,
            EnterThreshold = 2.0,
            ExitThreshold = 1.0,
            CoolDownPeriod = 2,
            MinDuration = 2
        };

        obj.Points.Add(new ChartPoint(candles[0].Timestamp, candles[0].Close));
        obj.Points.Add(new ChartPoint(candles[^1].Timestamp, candles[^1].Close));

        obj.Recalculate(candles, TimeSpan.FromHours(1));

        Assert.NotNull(obj.CalculatedResult);
        Assert.False(obj.CalculatedResult.IsEmpty);
        Assert.NotEmpty(obj.CalculatedResult.Intervals);
        Assert.Equal(80, obj.CalculatedResult.ReconstructedPoints.Count);
        Assert.Equal(80, obj.CalculatedResult.UpperBandPoints.Count);
        Assert.Equal(80, obj.CalculatedResult.LowerBandPoints.Count);

        var dataValues = obj.GetCalculatedValues();
        Assert.NotEmpty(dataValues);
        Assert.Contains(dataValues, v => v.Key == "SSA Anomaly Intervals");
        Assert.Contains(dataValues, v => v.Key == "SSA Max Anomaly Z");
        Assert.Contains(dataValues, v => v.Key == "SSA Peak Price Deviation");
        Assert.Contains(dataValues, v => v.Key == "SSA Latest State");
        Assert.Contains(dataValues, v => v.Key == "SSA Residual Noise (σ)");
        Assert.Contains(dataValues, v => v.Key == "SSA Separability");
    }

    [Fact]
    public void SsaAnomalyHighlightObject_Recalculate_WithEmptyCandles_ClearsResult()
    {
        var candles = MakeSampleCandles(80);
        var obj = new SsaAnomalyHighlightObject();
        obj.Points.Add(new ChartPoint(candles[0].Timestamp, candles[0].Close));
        obj.Points.Add(new ChartPoint(candles[^1].Timestamp, candles[^1].Close));
        obj.Recalculate(candles);
        Assert.NotNull(obj.CalculatedResult);

        obj.Recalculate(new List<CoreCandleData>());
        Assert.Null(obj.CalculatedResult);
    }

    [Fact]
    public void SsaAnomalyHighlightObject_Translate_ShiftsPointsAndInvalidates()
    {
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var t1 = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);

        var obj = new SsaAnomalyHighlightObject();
        obj.Points.Add(new ChartPoint(t0, 100m));
        obj.Points.Add(new ChartPoint(t1, 120m));

        obj.Translate(TimeSpan.FromHours(2), 5m);

        Assert.Equal(t0.AddHours(2), obj.Points[0].Time);
        Assert.Equal(105m, obj.Points[0].Price);
        Assert.Equal(t1.AddHours(2), obj.Points[1].Time);
        Assert.Equal(125m, obj.Points[1].Price);
        Assert.Null(obj.CalculatedResult);
    }

    [Fact]
    public void SsaAnomalyHighlightObject_HandleDragAndMove_RecalculatesInRealTime()
    {
        var candles = MakeSampleCandles(100);
        var obj = new SsaAnomalyHighlightObject
        {
            EmbeddingDimension = 15,
            NumComponents = 2,
            AutoRank = false
        };

        // Initially placed across candles[0..50]
        obj.Points.Add(new ChartPoint(candles[0].Timestamp, candles[0].Close));
        obj.Points.Add(new ChartPoint(candles[50].Timestamp, candles[50].Close));
        obj.Recalculate(candles);

        Assert.NotNull(obj.CalculatedResult);
        Assert.Equal(51, obj.CalculatedResult.ReconstructedPoints.Count);

        // 1. Simulate handle drag: change end point to candles[80] and recalculate
        obj.Points[1] = new ChartPoint(candles[80].Timestamp, candles[80].Close);
        obj.Recalculate(candles);

        Assert.NotNull(obj.CalculatedResult);
        Assert.Equal(81, obj.CalculatedResult.ReconstructedPoints.Count);

        // 2. Simulate object move: translate by 10 hours and recalculate
        var timeDelta = candles[10].Timestamp - candles[0].Timestamp;
        obj.Translate(timeDelta, 0m);
        Assert.Null(obj.CalculatedResult); // Cache invalidated by Translate

        // Live recalculation during object drag
        obj.Recalculate(candles);
        Assert.NotNull(obj.CalculatedResult);
        Assert.Equal(candles[10].Timestamp.Ticks, (long)obj.CalculatedResult.ReconstructedPoints[0].X);
        Assert.Equal(candles[90].Timestamp.Ticks, (long)obj.CalculatedResult.ReconstructedPoints[^1].X);
    }
}
