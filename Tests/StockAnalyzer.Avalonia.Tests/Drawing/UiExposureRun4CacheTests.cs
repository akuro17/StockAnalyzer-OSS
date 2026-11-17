using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.GeometricPattern;
using Xunit;
using static StockAnalyzer.Avalonia.Tests.Drawing.DrawingTestCandles;
using CorePoint = StockAnalyzer.Core.Models.Point;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

/// <summary>
/// R-10: the value rows of the Geometric / Kalman / DTW / HMM objects are built once and returned from a cache (SA_RENDERING_PERFORMANCE:
/// fixed-size array, no per-hover allocation). Every property that feeds a row must invalidate the cache, which these tests prove one by one.
/// </summary>
public class UiExposureRun4CacheTests
{
    private static readonly DateTime T = new(2024, 1, 1);

    private static IReadOnlyList<DrawingCalculatedValue> Rebuilt(IDrawingCalculatedValuesProvider obj, Action mutate)
    {
        var before = obj.GetCalculatedValues(T);
        Assert.Same(before, obj.GetCalculatedValues(T)); // unchanged data: the cached reference comes back
        mutate();
        var after = obj.GetCalculatedValues(T);
        Assert.NotSame(before, after);                    // the mutation invalidated the cache
        return after;
    }

    // ------------------------------------------------------------------ GeometricPattern

    private static DetectedFormation Formation(GeometricFormationType type, int endDay, double confidence)
        => new(type, 0, endDay, 0.5, 110.0, -0.25, 100.0, confidence, false, T, T.AddDays(endDay));

    private static GeometricPatternObject Geometric() => new()
    {
        Formations = new[]
        {
            Formation(GeometricFormationType.AscendingChannel, 10, 0.8),
            Formation(GeometricFormationType.RisingWedge, 20, 0.7),
            Formation(GeometricFormationType.SymmetricalTriangle, 30, 0.6),
            Formation(GeometricFormationType.BullishFlag, 40, 0.5),
            Formation(GeometricFormationType.Megaphone, 50, 0.4)
        }
    };

    [Fact]
    public void Geometric_ReturnsTheCachedArray_AndEveryFilterInvalidatesIt()
    {
        var obj = Geometric();
        Assert.Equal(6, obj.GetCalculatedValues(T).Count);

        Assert.Equal(5, Rebuilt(obj, () => obj.ShowChannels = false).Count);
        Assert.Equal(4, Rebuilt(obj, () => obj.ShowWedges = false).Count);
        Assert.Equal(3, Rebuilt(obj, () => obj.ShowTriangles = false).Count);
        Assert.Equal(2, Rebuilt(obj, () => obj.ShowPennantsAndFlags = false).Count);
        Assert.Empty(Rebuilt(obj, () => obj.ShowMegaphone = false));
    }

    [Fact]
    public void Geometric_FormationsAndColor_InvalidateTheCache()
    {
        var obj = Geometric();
        obj.GetCalculatedValues(T);

        var replaced = Rebuilt(obj, () => obj.Formations = new[] { Formation(GeometricFormationType.HorizontalBox, 5, 0.9) });
        Assert.Equal(2, replaced.Count);
        Assert.Equal("Formation #1 (HorizontalBox)", replaced[1].Label);

        var recoloured = Rebuilt(obj, () => obj.Color = Color.FromRgb(1, 2, 3));
        Assert.All(recoloured, v => Assert.Equal(new IndicatorColor(255, 1, 2, 3), v.Color));
    }

    // ------------------------------------------------------------------ Kalman

    private static KalmanFilterProjectionObject Kalman(IReadOnlyList<CoreCandleData> candles)
    {
        var obj = new KalmanFilterProjectionObject { FutureSteps = 10 };
        obj.Points.Add(new ChartPoint(candles[0].Timestamp, candles[0].Close));
        obj.Points.Add(new ChartPoint(candles[29].Timestamp, candles[29].Close));
        obj.Recalculate(candles, TimeSpan.FromHours(1));
        return obj;
    }

    [Fact]
    public void Kalman_RecalculateTranslateAndColor_InvalidateTheCache()
    {
        var candles = CreateCandles(40);
        var obj = Kalman(candles);
        decimal target = obj.GetCalculatedValues(T)[0].NumericValue!.Value;

        var translated = Rebuilt(obj, () => obj.Translate(TimeSpan.Zero, 5m));
        Assert.Equal(target + 5m, translated[0].NumericValue!.Value, 6);

        var recoloured = Rebuilt(obj, () => obj.Color = Color.FromRgb(9, 8, 7));
        Assert.Equal(new IndicatorColor(255, 9, 8, 7), recoloured[0].Color);

        var recalculated = Rebuilt(obj, () => { obj.FutureSteps = 3; obj.Recalculate(candles, TimeSpan.FromHours(1)); });
        Assert.Equal(4, recalculated.Count);
    }

    [Fact]
    public void Kalman_PathsClearedInPlaceByTheDragHandler_GiveNoRowsEvenWithAWarmCache()
    {
        var obj = Kalman(CreateCandles(40));
        Assert.NotEmpty(obj.GetCalculatedValues(T));

        // ChartInteractionController clears the lists in place while a handle is dragged.
        obj.ProjectedPath.Clear();

        Assert.Empty(obj.GetCalculatedValues(T));
    }

    // ------------------------------------------------------------------ DTW

    private static DtwProjectionObject Dtw() => new()
    {
        UseDbaEnsemble = true,
        TopKMatches = 5,
        ProjectedPath = new List<CorePoint> { new(1, 100.0), new(2, 110.0) },
        UpperBandPath = new List<CorePoint> { new(1, 100.0), new(2, 115.0) },
        LowerBandPath = new List<CorePoint> { new(1, 100.0), new(2, 105.0) },
        MatchedStartTime = T,
        MatchedEndTime = T.AddDays(10),
        BestMatchDistance = 0.5,
        BestMatchSimilarity = 80.0
    };

    [Fact]
    public void Dtw_EveryInputProperty_InvalidatesTheCache()
    {
        var obj = Dtw();
        Assert.Equal(5, obj.GetCalculatedValues(T).Count); // Mode, Target, Window, Similarity, Band

        Assert.Equal(3m, Rebuilt(obj, () => obj.TopKMatches = 3)[0].NumericValue);
        Assert.Equal(new IndicatorColor(255, 4, 5, 6), Rebuilt(obj, () => obj.Color = Color.FromRgb(4, 5, 6))[0].Color);
        Assert.Contains("Dist=1.250", Rebuilt(obj, () => obj.BestMatchDistance = 1.25).Single(v => v.Key == "DTW Similarity").FormattedText);
        Assert.Contains("65.5%", Rebuilt(obj, () => obj.BestMatchSimilarity = 65.5).Single(v => v.Key == "DTW Similarity").FormattedText);
        Assert.Contains("120.00", Rebuilt(obj, () => obj.UpperBandPath = new List<CorePoint> { new(1, 100.0), new(2, 120.0) }).Single(v => v.Key == "DTW Band (15%-85%)").FormattedText);
        Assert.Contains("101.00", Rebuilt(obj, () => obj.LowerBandPath = new List<CorePoint> { new(1, 100.0), new(2, 101.0) }).Single(v => v.Key == "DTW Band (15%-85%)").FormattedText);
        Assert.Contains("130.00", Rebuilt(obj, () => obj.ProjectedPath = new List<CorePoint> { new(1, 100.0), new(2, 130.0) }).Single(v => v.Key == "DTW Target Price").FormattedText);
        Assert.Contains("2024/01/05", Rebuilt(obj, () => obj.MatchedStartTime = T.AddDays(4)).Single(v => v.Key == "DTW Matched Window").FormattedText);
        Assert.Contains("2024/01/20", Rebuilt(obj, () => obj.MatchedEndTime = T.AddDays(19)).Single(v => v.Key == "DTW Matched Window").FormattedText);
        Assert.DoesNotContain(Rebuilt(obj, () => obj.UseDbaEnsemble = false), v => v.Key == "DTW Band (15%-85%)");
        Assert.Empty(Rebuilt(obj, () => obj.IsUnmatched = true));
    }

    [Fact]
    public void Dtw_PathClearedInPlace_GivesNoRowsEvenWithAWarmCache()
    {
        var obj = Dtw();
        Assert.NotEmpty(obj.GetCalculatedValues(T));

        // ChartViewModel and the drag handler clear ProjectedPath in place.
        obj.ProjectedPath.Clear();

        Assert.Empty(obj.GetCalculatedValues(T));
    }

    // ------------------------------------------------------------------ HMM

    private static HmmProjectionObject Hmm() => new()
    {
        States = 2,
        FilteredStateProbabilities = new[] { 0.3, 0.7 },
        StateMeans = new[] { -0.01, 0.01 },
        StateStdDevs = new[] { 0.02, 0.01 },
        TransitionMatrix = new[,] { { 0.9, 0.1 }, { 0.2, 0.8 } },
        CurrentRegimeIndex = 1,
        BullStateProbability = 0.7
    };

    [Fact]
    public void Hmm_EveryInputProperty_InvalidatesTheCache()
    {
        var obj = Hmm();
        Assert.Equal("Bullish", obj.GetCalculatedValues(T)[0].FormattedText);

        Assert.Equal("Bearish", Rebuilt(obj, () => obj.CurrentRegimeIndex = 0)[0].FormattedText);
        Assert.Equal(55m, Rebuilt(obj, () => obj.BullStateProbability = 0.55)[1].NumericValue);
        Assert.Equal("State #1", Rebuilt(obj, () => obj.States = 4)[0].FormattedText);
        Assert.Equal(new IndicatorColor(255, 7, 7, 7), Rebuilt(obj, () => obj.Color = Color.FromRgb(7, 7, 7))[0].Color);
        Assert.Contains(Rebuilt(obj, () => obj.StateMeans = new[] { -0.02, 0.02, 0.0 }), v => v.Key == "HMM State #3 Mean Return");
        Assert.Contains(Rebuilt(obj, () => obj.StateStdDevs = new[] { 0.02, 0.01, 0.03 }), v => v.Key == "HMM State #3 Volatility");
        Assert.Equal(95m, Rebuilt(obj, () => obj.TransitionMatrix = new[,] { { 0.95, 0.05 }, { 0.2, 0.8 } }).Single(v => v.Key == "HMM State #1 Persistence").NumericValue);
        Assert.Empty(Rebuilt(obj, () => obj.FilteredStateProbabilities = Array.Empty<double>()));
    }
}
