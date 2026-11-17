using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Drawing;
using StockAnalyzer.Core.Drawing.Values;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Core.Tests.Drawing;

/// <summary>The eight drawing tools of phase 2 and 3 of the headless drawing values: known numbers worked out by hand.</summary>
public sealed class HeadlessDrawingValuesPhase23Tests
{
    private static readonly IndicatorColor Color = IndicatorColor.Gray;
    private static readonly DateTime T0 = new(2026, 1, 1);

    private static DrawingAnchor At(int day, decimal price) => new(T0.AddDays(day), price);

    private static DrawingValueDescriptor Tool(string name)
    {
        Assert.True(DrawingValueCatalog.TryGet(name, out DrawingValueDescriptor descriptor));
        return descriptor;
    }

    private static IReadOnlyList<DrawingCalculatedValue> Run(string name, DrawingAnchor[] points, DateTime timestamp, decimal? currentPrice = null, DrawingValueOptions? options = null, IReadOnlyList<CoreCandleData>? candles = null)
        => Tool(name).Calculate(new DrawingValueRequest(points, timestamp, currentPrice, Color, options ?? new DrawingValueOptions(), candles));

    private static DrawingCalculatedValue Item(IReadOnlyList<DrawingCalculatedValue> values, string key) => values.Single(v => v.Key == key);

    // ---- AngleTool

    [Fact]
    public void AngleTool_ReturnsTheLinePriceTheChangeAndTheSlopePerDay()
    {
        var values = Run("AngleTool", new[] { At(0, 100m), At(10, 150m) }, T0.AddDays(5));

        Assert.Equal(new[] { "LinePrice", "PriceDelta", "Slope" }, values.Select(v => v.Key));
        Assert.Equal(125m, Item(values, "LinePrice").NumericValue);
        Assert.Equal("125.000", Item(values, "LinePrice").FormattedText);
        Assert.Equal(50m, Item(values, "PriceDelta").NumericValue);
        Assert.Equal("+50.000 (+50.00%)", Item(values, "PriceDelta").FormattedText);
        Assert.Equal(5m, Item(values, "Slope").NumericValue);
        Assert.Equal("+5.000 / day", Item(values, "Slope").FormattedText);
    }

    [Fact]
    public void AngleTool_WithTheSameTimeTwice_HasNoSlope()
    {
        var values = Run("AngleTool", new[] { At(3, 100m), At(3, 90m) }, T0.AddDays(3));

        Assert.DoesNotContain(values, v => v.Key == "Slope");
        Assert.Equal("-10.000 (-10.00%)", Item(values, "PriceDelta").FormattedText);
    }

    // ---- GannSquareOfNine

    [Fact]
    public void GannSquareOfNine_ProjectsTheSquareRootLevels_AboveAndBelow()
    {
        var values = Run("GannSquareOfNine", new[] { At(0, 100m) }, T0);

        Assert.Equal(17, values.Count);
        Assert.Equal("Base", values[0].Key);
        Assert.Equal(105.0625m, Item(values, "Res_45°").NumericValue);
        Assert.Equal(121m, Item(values, "Res_180°").NumericValue);
        Assert.Equal(144m, Item(values, "Res_360°").NumericValue);
        Assert.Equal(95.0625m, Item(values, "Sup_45°").NumericValue);
        Assert.Equal(81m, Item(values, "Sup_180°").NumericValue);
        Assert.Equal(64m, Item(values, "Sup_360°").NumericValue);
        Assert.Equal("105.063", Item(values, "Res_45°").FormattedText);
    }

    [Fact]
    public void GannSquareOfNine_DropsTheSupportsWhoseRootIsNotPositive_AndRefusesANegativePrice()
    {
        // Base 1: sqrt 1; the supports exist only for the factor 0.25, 0.5 and 0.75 (1 - 1.0 is not positive).
        var small = Run("GannSquareOfNine", new[] { At(0, 1m) }, T0);
        Assert.Equal(new[] { "Sup_45°", "Sup_90°", "Sup_135°" }, small.Where(v => v.Key.StartsWith("Sup_", StringComparison.Ordinal)).Select(v => v.Key));

        Assert.Empty(Run("GannSquareOfNine", new[] { At(0, -5m) }, T0));
    }

    [Fact]
    public void GannSquareOfNine_ForAPriceThatTheDecimalRangeCannotCarryAfterTheSquare_LeavesTheLevelOut_NeverThrows()
    {
        // decimal.MaxValue is about 7.9e28: (sqrt + factor)^2 no longer fits, so every level is left out and only the base remains (a raw (decimal) cast threw OverflowException here).
        var atTheMaximum = Run("GannSquareOfNine", new[] { At(0, decimal.MaxValue) }, T0);
        Assert.Equal(new[] { "Base" }, atTheMaximum.Select(v => v.Key));

        // A large price that does fit keeps every level.
        var large = Run("GannSquareOfNine", new[] { At(0, 4e27m) }, T0);
        Assert.Equal(17, large.Count);
    }

    // ---- AnchoredVWAP: causality contract

    [Fact]
    public void AnchoredVwap_WithAnAnchorAfterTheLastBarPassed_HasNoValues_NotTheFallbackOfTheCurve()
    {
        // A causal caller (the MCP tools) passes the bars up to the evaluation time only: an anchor after the last of them leaves no curve.
        IReadOnlyList<CoreCandleData> bars = DrawingValueTestCandles.Daily(60);
        DateTime lastBar = bars[^1].Timestamp;

        Assert.Empty(Run("AnchoredVWAP", new[] { new DrawingAnchor(lastBar.AddDays(5), 100m) }, lastBar, candles: bars));
        Assert.NotEmpty(Run("AnchoredVWAP", new[] { new DrawingAnchor(lastBar.AddDays(-5), 100m) }, lastBar, candles: bars));
    }

    [Fact]
    public void AnchoredVwap_MatchedIndex_KeepsTheChartFallbackToTheLatestPoint_WhenNoPointIsAtOrBeforeTheTime()
    {
        var curve = new List<(DateTime Time, decimal Price)> { (T0.AddDays(5), 1m), (T0.AddDays(6), 2m) };

        Assert.Equal(1, AnchoredVwapValues.MatchedIndex(curve, T0));
        Assert.Equal(0, AnchoredVwapValues.MatchedIndex(curve, T0.AddDays(5)));
    }

    [Fact]
    public void TheVwapBandMultiplierBounds_HaveOneDecimalDefinition_AndTheDoubleFormsAreDerivedFromIt()
    {
        Assert.Equal(0.1m, AnchoredVwapValues.MinBandMultiplier);
        Assert.Equal(10.0m, AnchoredVwapValues.MaxBandMultiplier);
        Assert.Equal((double)IndicatorDefaultConstants.VwapMinStdDevMultiplierDecimal, IndicatorDefaultConstants.VwapMinStdDevMultiplier);
        Assert.Equal((double)IndicatorDefaultConstants.VwapMaxStdDevMultiplierDecimal, IndicatorDefaultConstants.VwapMaxStdDevMultiplier);
    }

    // ---- TargetPriceProjection

    [Fact]
    public void TargetPriceProjection_GivesTheFiveTargets_InDrawingOrder_WithTheDistanceToTheCurrentPrice()
    {
        // A = 100, B = 150, C = 130.
        var values = Run("TargetPriceProjection", new[] { At(0, 100m), At(10, 150m), At(8, 130m) }, T0.AddDays(10), currentPrice: 140m);

        Assert.Equal(new[] { "Target_V", "Target_N", "Target_%", "Target_E", "Target_NT" }, values.Select(v => v.Key));
        Assert.Equal(170m, Item(values, "Target_V").NumericValue);
        Assert.Equal(180m, Item(values, "Target_N").NumericValue);
        Assert.Equal(195m, Item(values, "Target_%").NumericValue);
        Assert.Equal(200m, Item(values, "Target_E").NumericValue);
        Assert.Equal(160m, Item(values, "Target_NT").NumericValue);
        Assert.Equal("170.000 (+30.000)", Item(values, "Target_V").FormattedText);
        Assert.Equal("Target V (Double Return)", Item(values, "Target_V").Label);
    }

    [Fact]
    public void TargetPriceProjection_WithoutACurrentPrice_ShowsThePriceOnly_AndLeavesOutThePercentageForAZeroFirstPrice()
    {
        var values = Run("TargetPriceProjection", new[] { At(0, 0m), At(10, 150m), At(8, 130m) }, T0.AddDays(10));

        Assert.DoesNotContain(values, v => v.Key == "Target_%");
        Assert.Equal("170.000", Item(values, "Target_V").FormattedText);
    }

    [Fact]
    public void TargetPriceProjection_TargetsAnchorIndex_NamesThePointEachTargetHangsFrom()
    {
        var targets = TargetPriceProjectionValues.Targets(new[] { At(0, 100m), At(10, 150m), At(8, 130m) }, ProjectionTargetSelection.All);

        Assert.Equal(new[] { 2, 2, 2, 1, 2 }, targets.Select(t => t.AnchorIndex));
        Assert.Empty(TargetPriceProjectionValues.Targets(new[] { At(0, 100m), At(10, 150m) }, ProjectionTargetSelection.All));
    }

    [Fact]
    public void TargetPriceProjection_ASelectionShowsOnlyTheEnabledTargets()
    {
        var selection = new ProjectionTargetSelection(ShowEqualDistance: false, ShowEqualPercentage: false, ShowDoubleReturn: true, ShowEProjection: false, ShowNTProjection: true);

        var values = TargetPriceProjectionValues.Calculate(new[] { At(0, 100m), At(10, 150m), At(8, 130m) }, null, Color, selection);

        Assert.Equal(new[] { "Target_V", "Target_NT" }, values.Select(v => v.Key));
    }

    // ---- LongPosition and ShortPosition

    [Fact]
    public void LongPosition_ReturnsEntryTargetStopAndTheRiskRewardRatio()
    {
        var values = Run("LongPosition", new[] { At(0, 100m), At(0, 95m), At(0, 110m) }, T0);

        Assert.Equal(new[] { "Entry", "Target", "Stop", "RiskReward" }, values.Select(v => v.Key));
        Assert.Equal("100.00", Item(values, "Entry").FormattedText);
        Assert.Equal("110.00 (+10.00)", Item(values, "Target").FormattedText);
        Assert.Equal("95.00 (-5.00)", Item(values, "Stop").FormattedText);
        Assert.Equal(2m, Item(values, "RiskReward").NumericValue);
        Assert.Equal("1 : 2.00", Item(values, "RiskReward").FormattedText);
    }

    [Fact]
    public void ShortPosition_UsesTheSameCalculation_WithTheSignsOfItsOwnPoints()
    {
        var values = Run("ShortPosition", new[] { At(0, 100m), At(0, 105m), At(0, 90m) }, T0);

        Assert.Equal("90.00 (-10.00)", Item(values, "Target").FormattedText);
        Assert.Equal("105.00 (+5.00)", Item(values, "Stop").FormattedText);
        Assert.Equal(2m, Item(values, "RiskReward").NumericValue);
    }

    [Theory]
    [InlineData("LongPosition", 100, 105, 90)]
    [InlineData("LongPosition", 100, 100, 100)]
    [InlineData("ShortPosition", 100, 95, 110)]
    [InlineData("ShortPosition", 100, 100, 100)]
    public void APositionInTheInvalidOrder_IsCalculatedNotRefused_AndIsOrderedSaysWhy(string tool, int entry, int stop, int target)
    {
        var values = Run(tool, new[] { At(0, entry), At(0, stop), At(0, target) }, T0);

        Assert.Equal(4, values.Count);
        Assert.False(LongShortPositionValues.IsOrdered(entry, stop, target, tool == "LongPosition"));
    }

    [Fact]
    public void APositionWithNoRisk_HasARatioOfZero_AndTheAtrItemIsAddedOnlyWhenGiven()
    {
        var withoutAtr = LongShortPositionValues.Calculate(At(0, 100m), At(0, 100m), At(0, 110m), Color, Color, Color);
        Assert.Equal(0m, Item(withoutAtr, "RiskReward").NumericValue);
        Assert.DoesNotContain(withoutAtr, v => v.Key == "ATR_Entry");

        var withAtr = LongShortPositionValues.Calculate(At(0, 100m), At(0, 95m), At(0, 110m), Color, Color, Color, 2.5m);
        Assert.Equal("2.500", Item(withAtr, "ATR_Entry").FormattedText);

        var viaCatalog = Run("LongPosition", new[] { At(0, 100m), At(0, 95m), At(0, 110m) }, T0, options: new DrawingValueOptions { IndicatorValueAtEntry = 2.5m });
        Assert.Equal(withAtr, viaCatalog);
    }

    [Theory]
    [InlineData(95, 100, 110, true, true)]
    [InlineData(100, 100, 110, true, false)]
    [InlineData(110, 100, 90, false, true)]
    [InlineData(90, 100, 110, false, false)]
    public void IsOrdered_RequiresTheStrictOrderOfTheKind(int stop, int entry, int target, bool isLong, bool expected)
    {
        Assert.Equal(expected, LongShortPositionValues.IsOrdered(entry, stop, target, isLong));
    }

    // ---- RegressionTrend

    [Fact]
    public void RegressionTrend_OnARisingLine_HasTheBarCloseAsCentre_AndNoBand()
    {
        // The bars close at 100 + 0.5 * day, so the fit is exact (standard deviation 0) and the centre is the close of the bar.
        IReadOnlyList<CoreCandleData> candles = DrawingValueTestCandles.Daily();
        DateTime first = DrawingValueTestCandles.First;
        var points = new[] { new DrawingAnchor(first.AddDays(10), 1m), new DrawingAnchor(first.AddDays(30), 2m) };

        var values = Run("RegressionTrend", points, first.AddDays(20), candles: candles);

        Assert.Equal(new[] { "RegressionCenter", "RegressionUpper2", "RegressionLower2", "Slope", "StdDev" }, values.Select(v => v.Key));
        Assert.Equal(110m, Item(values, "RegressionCenter").NumericValue);
        Assert.Equal(110m, Item(values, "RegressionUpper2").NumericValue);
        Assert.Equal("+2σ Band", Item(values, "RegressionUpper2").Label);
        Assert.Equal("-2σ Band", Item(values, "RegressionLower2").Label);
        Assert.Equal(0.5m, Item(values, "Slope").NumericValue);
        Assert.Equal("0.5000", Item(values, "Slope").FormattedText);
        Assert.Equal(0m, Item(values, "StdDev").NumericValue);
    }

    [Fact]
    public void RegressionTrend_TheBandNameFollowsTheMultiplier_AndTheRangeMayBeGivenBackwards()
    {
        IReadOnlyList<CoreCandleData> candles = DrawingValueTestCandles.Daily();
        DateTime first = DrawingValueTestCandles.First;
        var points = new[] { new DrawingAnchor(first.AddDays(30), 1m), new DrawingAnchor(first.AddDays(10), 2m) };

        var values = Run("RegressionTrend", points, first.AddDays(20), options: new DrawingValueOptions(StdDevMultiplier: 1.5m), candles: candles);

        Assert.Equal("+1.5σ Band", Item(values, "RegressionUpper2").Label);
        Assert.Equal(110m, Item(values, "RegressionCenter").NumericValue);
    }

    [Fact]
    public void RegressionTrend_ExtendRight_ContinuesTheFitOverTheExistingBarsAfterTheRange()
    {
        // Days 31 to 33 have no bar, so the bar of day 36 is the third bar after the range (index 2), not the 26th day of 20.
        DateTime first = DrawingValueTestCandles.First;
        IReadOnlyList<CoreCandleData> candles = DrawingValueTestCandles.Daily().Where(c => c.Timestamp < first.AddDays(31) || c.Timestamp > first.AddDays(33)).ToList();
        var points = new[] { new DrawingAnchor(first.AddDays(10), 1m), new DrawingAnchor(first.AddDays(30), 2m) };
        DateTime after = first.AddDays(36);

        var plain = Run("RegressionTrend", points, after, candles: candles);
        var extended = Run("RegressionTrend", points, after, options: new DrawingValueOptions(ExtendRight: true), candles: candles);

        // The range holds 21 bars (x = 0 to 20, intercept 105, slope 0.5). Extended: x = 21 + 2 = 23, so 116.5.
        // Not extended: the time is placed on the range by proportion, x = 26 / 20 * 20 = 26, so 118.
        Assert.Equal(116.5m, Item(extended, "RegressionCenter").NumericValue);
        Assert.Equal(118m, Item(plain, "RegressionCenter").NumericValue);
    }

    [Fact]
    public void RegressionTrend_WithFewerThanTwoBarsInTheRange_ReturnsNoValues()
    {
        IReadOnlyList<CoreCandleData> candles = DrawingValueTestCandles.Daily();
        DateTime first = DrawingValueTestCandles.First;

        Assert.Empty(Run("RegressionTrend", new[] { new DrawingAnchor(first.AddDays(5), 1m), new DrawingAnchor(first.AddDays(5), 1m) }, first.AddDays(5), candles: candles));
        Assert.Empty(Run("RegressionTrend", new[] { new DrawingAnchor(first.AddDays(-30), 1m), new DrawingAnchor(first.AddDays(-20), 1m) }, first, candles: candles));
    }

    // ---- AnchoredVWAP

    [Fact]
    public void AnchoredVwap_StartsAtTheTypicalPriceOfTheAnchorBar_AndListsTheAnchorDate()
    {
        IReadOnlyList<CoreCandleData> candles = DrawingValueTestCandles.Daily();
        DateTime anchor = DrawingValueTestCandles.First.AddDays(50);

        var values = Run("AnchoredVWAP", new[] { new DrawingAnchor(anchor, 1m) }, anchor, candles: candles);

        Assert.Equal(new[] { "AnchorDate", "AnchoredVWAP" }, values.Select(v => v.Key));
        Assert.Equal("2025/12/21", Item(values, "AnchorDate").FormattedText);
        Assert.Null(Item(values, "AnchorDate").NumericValue);
        // Typical = (high + low + close) / 3 = close for these bars.
        Assert.Equal(125m, Item(values, "AnchoredVWAP").NumericValue);
    }

    [Fact]
    public void AnchoredVwap_LaterBars_AreVolumeWeighted_AndTheBandsFollowTheOptions()
    {
        IReadOnlyList<CoreCandleData> candles = DrawingValueTestCandles.Daily();
        DateTime anchor = DrawingValueTestCandles.First.AddDays(50);
        DateTime evaluation = DrawingValueTestCandles.First.AddDays(80);
        var options = new DrawingValueOptions(ShowBand1: true, Band1Multiplier: 1.0m, ShowBand2: true, Band2Multiplier: 2.0m);

        var values = Run("AnchoredVWAP", new[] { new DrawingAnchor(anchor, 1m) }, evaluation, options: options, candles: candles);

        var expectedCurve = VolumeAnalysis.CalculateAnchoredVwapWithDeviation(candles.Where(c => c.Timestamp >= anchor).ToList(), PriceType.Typical);
        var atEvaluation = expectedCurve.Single(p => p.Time == evaluation);
        Assert.Equal(new[] { "AnchorDate", "AnchoredVWAP", "Upper1", "Lower1", "Upper2", "Lower2" }, values.Select(v => v.Key));
        Assert.Equal(atEvaluation.Vwap, Item(values, "AnchoredVWAP").NumericValue);
        Assert.Equal(atEvaluation.Vwap + atEvaluation.StdDev, Item(values, "Upper1").NumericValue);
        Assert.Equal(atEvaluation.Vwap - 2m * atEvaluation.StdDev, Item(values, "Lower2").NumericValue);
        Assert.True(Item(values, "AnchoredVWAP").NumericValue < 165m);
    }

    [Fact]
    public void AnchoredVwap_AnEvaluationTimeBeforeTheAnchor_FallsBackToTheFirstCurvePointBeforeIt_AndNoBarsAfterTheAnchorGivesNoValues()
    {
        IReadOnlyList<CoreCandleData> candles = DrawingValueTestCandles.Daily(60);
        DateTime anchor = DrawingValueTestCandles.First.AddDays(40);

        var beforeAnchor = Run("AnchoredVWAP", new[] { new DrawingAnchor(anchor, 1m) }, anchor.AddDays(-5), candles: candles);

        // No curve point is at or before the time: the latest curve point is used, as the chart's Data tab has always done.
        decimal latest = VolumeAnalysis.CalculateAnchoredVwap(candles.Where(c => c.Timestamp >= anchor).ToList(), PriceType.Typical)[^1].Vwap;
        Assert.Equal(new[] { "AnchorDate", "AnchoredVWAP" }, beforeAnchor.Select(v => v.Key));
        Assert.Equal(latest, Item(beforeAnchor, "AnchoredVWAP").NumericValue);

        Assert.Empty(Run("AnchoredVWAP", new[] { new DrawingAnchor(DrawingValueTestCandles.First.AddDays(500), 1m) }, anchor, candles: candles));
    }

    // ---- ClassicPivotPoints

    [Fact]
    public void ClassicPivotPoints_Day_UsesTheHighLowCloseOfThePreviousDay()
    {
        IReadOnlyList<CoreCandleData> candles = DrawingValueTestCandles.Daily();
        DateTime anchor = DrawingValueTestCandles.First.AddDays(50); // 2025-12-21; the day before closes at 124.5, high 125.5, low 123.5

        var values = Run("ClassicPivotPoints", new[] { new DrawingAnchor(anchor, 1m) }, anchor, candles: candles);

        Assert.Equal(new[] { "Period", "ExtendLines", "HBOP", "R2", "R1", "Pivot", "S1", "S2", "LBOP" }, values.Select(v => v.Key));
        Assert.Equal("Day", Item(values, "Period").FormattedText);
        Assert.Equal("None", Item(values, "ExtendLines").FormattedText);
        Assert.Equal(124.5m, Item(values, "Pivot").NumericValue);
        Assert.Equal(125.5m, Item(values, "R1").NumericValue);
        Assert.Equal(123.5m, Item(values, "S1").NumericValue);
        Assert.Equal(126.5m, Item(values, "R2").NumericValue);
        Assert.Equal(122.5m, Item(values, "S2").NumericValue);
        Assert.Equal(127.5m, Item(values, "HBOP").NumericValue);
        Assert.Equal(121.5m, Item(values, "LBOP").NumericValue);
        Assert.Equal("124.500", Item(values, "Pivot").FormattedText);
    }

    [Fact]
    public void ClassicPivotPoints_Week_UsesTheWholePreviousWeekFromMondayToSunday()
    {
        IReadOnlyList<CoreCandleData> candles = DrawingValueTestCandles.Daily();
        DateTime anchor = new(2025, 12, 24); // a Wednesday; the previous week is Monday 2025-12-15 to Sunday 2025-12-21 (days 44 to 50)

        var values = Run("ClassicPivotPoints", new[] { new DrawingAnchor(anchor, 1m) }, anchor, options: new DrawingValueOptions(PivotPeriod: PivotPeriod.Week, PivotExtendMode: PivotLineExtendMode.ExtendBoth), candles: candles);

        // High 126 (day 50: close 125 + 1), low 121 (day 44: close 122 - 1), close 125 (day 50).
        Assert.Equal("Week", Item(values, "Period").FormattedText);
        Assert.Equal("ExtendBoth", Item(values, "ExtendLines").FormattedText);
        Assert.Equal(124m, Item(values, "Pivot").NumericValue);
        Assert.Equal(127m, Item(values, "R1").NumericValue);
        Assert.Equal(122m, Item(values, "S1").NumericValue);
        Assert.Equal(129m, Item(values, "R2").NumericValue);
        Assert.Equal(119m, Item(values, "S2").NumericValue);
    }

    [Fact]
    public void ClassicPivotPoints_Month_UsesThePreviousCalendarMonth()
    {
        IReadOnlyList<CoreCandleData> candles = DrawingValueTestCandles.Daily();

        Assert.True(ClassicPivotPointsValues.TryCompute(new DateTime(2025, 12, 15), candles, PivotPeriod.Month, out ClassicPivotLevels levels));

        // November 2025 is days 0 to 29: high 114.5 + 1 = 115.5 (day 29: close 114.5), low 99 (day 0: close 100 - 1), close 114.5.
        Assert.Equal(new DateTime(2025, 12, 1), levels.PeriodStart);
        Assert.Equal(new DateTime(2026, 1, 1), levels.PeriodEnd);
        Assert.Equal((115.5m + 99m + 114.5m) / 3m, levels.Pivot);
    }

    [Fact]
    public void ClassicPivotPoints_WithNoBarBeforeThePeriod_OrANonPositiveLow_HasNoLevels()
    {
        IReadOnlyList<CoreCandleData> candles = DrawingValueTestCandles.Daily();

        Assert.False(ClassicPivotPointsValues.TryCompute(DrawingValueTestCandles.First, candles, PivotPeriod.Day, out _));
        Assert.Empty(ClassicPivotPointsValues.Calculate(DrawingValueTestCandles.First, candles, PivotPeriod.Day, PivotLineExtendMode.None, Color));

        var zeroLow = new[]
        {
            new CoreCandleData(new DateTime(2026, 1, 1), 1m, 2m, 0m, 1m, 10),
            new CoreCandleData(new DateTime(2026, 1, 2), 1m, 2m, 1m, 1m, 10),
        };
        Assert.False(ClassicPivotPointsValues.TryCompute(new DateTime(2026, 1, 2), zeroLow, PivotPeriod.Day, out _));
    }
}
