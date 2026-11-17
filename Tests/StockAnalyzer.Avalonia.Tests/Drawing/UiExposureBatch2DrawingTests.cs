using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Avalonia.Views.Chart;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Theme;
using Xunit;
using static StockAnalyzer.Avalonia.Tests.Drawing.DrawingTestCandles;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

/// <summary>
/// WebAI UI-exposure Batch 2: Kalman / HMM / DTW Data Window rows and the effective dynamic period display plumbing.
/// </summary>
public class UiExposureBatch2DrawingTests
{
    private static T Select<T>(T obj, IReadOnlyList<CoreCandleData> candles, int from, int to) where T : IChartObject
    {
        obj.Points.Add(new ChartPoint(candles[from].Timestamp, candles[from].Close));
        obj.Points.Add(new ChartPoint(candles[to].Timestamp, candles[to].Close));
        return obj;
    }

    // ---------------------------------------------------------------- Kalman

    [Fact]
    public void Kalman_BeforeRecalculation_ExposesNoRows()
    {
        Assert.Empty(new KalmanFilterProjectionObject().GetCalculatedValues(DateTime.UtcNow));
    }

    [Fact]
    public void Kalman_Recalculate_ExposesTargetVelocityGainAndInnovationRows()
    {
        var candles = CreateCandles(40);
        var obj = Select(new KalmanFilterProjectionObject { FutureSteps = 10 }, candles, 0, 29);
        obj.Recalculate(candles, TimeSpan.FromHours(1));

        var result = obj.CalculatedResult;
        Assert.NotNull(result);
        var values = obj.GetCalculatedValues(DateTime.UtcNow);
        Assert.Equal(new[] { "Kalman Target Price", "Kalman Velocity (v)", "Kalman Gain (K0, K1)", KalmanFilterProjectionObject.BandScaleRowName }, values.Select(v => v.Key));
        Assert.Equal("Kalman Band Scale", KalmanFilterProjectionObject.BandScaleRowName);

        Assert.Equal((decimal)obj.ProjectedPath[^1].Y, values[0].NumericValue);

        Assert.Equal(Math.Round(result!.TerminalVelocity, 4), values[1].NumericValue);
        Assert.Equal(result.TerminalVelocity >= 0m ? IndicatorColor.Bullish : IndicatorColor.Bearish, values[1].Color);
        Assert.EndsWith(" / bar", values[1].FormattedText);

        Assert.Contains($"K0={result.TerminalGainPosition:F3}", values[2].FormattedText);
        Assert.Equal(IndicatorColor.Gray, values[2].Color);

        Assert.Equal(Math.Round(result.InnovationScale, 4), values[3].NumericValue);
    }

    [Fact]
    public void Kalman_Recalculate_PathStillMatchesTheEngineSteps()
    {
        var candles = CreateCandles(40);
        var obj = Select(new KalmanFilterProjectionObject { FutureSteps = 7, ConfidenceMultiplier = 1.5m }, candles, 5, 30);
        obj.Recalculate(candles, TimeSpan.FromHours(1));

        var closes = candles.Skip(5).Take(26).Select(c => c.Close).ToArray();
        var expected = KalmanProjectionAnalysis.CalculateProjection(closes, 7, obj.Q, obj.R, 1.5m);

        Assert.Equal(expected.Steps.Count + 1, obj.ProjectedPath.Count);
        for (int k = 0; k < expected.Steps.Count; k++)
        {
            Assert.Equal((double)expected.Steps[k].ProjectedPrice, obj.ProjectedPath[k + 1].Y);
            Assert.Equal((double)expected.Steps[k].UpperBand, obj.UpperBandPath[k + 1].Y);
            Assert.Equal((double)expected.Steps[k].LowerBand, obj.LowerBandPath[k + 1].Y);
        }
    }

    [Fact]
    public void Kalman_InvalidSelection_ClearsTheResultAndTheRows()
    {
        var candles = CreateCandles(40);
        var obj = Select(new KalmanFilterProjectionObject(), candles, 0, 29);
        obj.Recalculate(candles, TimeSpan.FromHours(1));
        Assert.NotEmpty(obj.GetCalculatedValues(DateTime.UtcNow));

        obj.Recalculate(null);

        Assert.Null(obj.CalculatedResult);
        Assert.Empty(obj.GetCalculatedValues(DateTime.UtcNow));
    }

    // ---------------------------------------------------------------- HMM

    private static HmmProjectionObject CreateHmm(int states)
    {
        var obj = new HmmProjectionObject
        {
            States = states,
            FilteredStateProbabilities = new[] { 0.3, 0.7 },
            StateMeans = states == 2 ? new[] { -0.001, 0.002 } : new[] { -0.001, 0.0, 0.002 },
            StateStdDevs = states == 2 ? new[] { 0.02, 0.01 } : new[] { 0.02, 0.015, 0.01 },
            CurrentRegimeIndex = states - 1,
            BullStateProbability = 0.7
        };
        obj.TransitionMatrix = states == 2
            ? new[,] { { 0.9, 0.1 }, { 0.2, 0.8 } }
            : new[,] { { 0.9, 0.05, 0.05 }, { 0.1, 0.8, 0.1 }, { 0.05, 0.05, 0.9 } };
        return obj;
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void Hmm_ExposesVolatilityAndPersistencePerState_AfterTheExistingRows(int states)
    {
        var obj = CreateHmm(states);

        var values = obj.GetCalculatedValues(DateTime.UtcNow);

        // The pre-existing rows keep their leading order.
        Assert.Equal("HMM Current Regime", values[0].Key);
        Assert.Equal("HMM Bull Probability", values[1].Key);
        for (int i = 0; i < states; i++)
        {
            Assert.Equal($"HMM State #{i + 1} Mean Return", values[2 + i].Key);
            var volatility = Assert.Single(values, v => v.Key == $"HMM State #{i + 1} Volatility");
            var persistence = Assert.Single(values, v => v.Key == $"HMM State #{i + 1} Persistence");

            Assert.Equal((decimal)(obj.StateStdDevs[i] * 100.0), volatility.NumericValue);
            Assert.Equal($"{obj.StateStdDevs[i] * 100.0:F2}%", volatility.FormattedText);
            Assert.Equal((decimal)Math.Round(obj.TransitionMatrix[i, i] * 100.0, 1), persistence.NumericValue);
            Assert.Equal($"{obj.TransitionMatrix[i, i] * 100.0:F1}%", persistence.FormattedText);
        }
    }

    [Fact]
    public void Hmm_WithoutATransitionMatrix_SkipsPersistenceButKeepsVolatility()
    {
        var obj = CreateHmm(2);
        obj.TransitionMatrix = new double[0, 0];

        var values = obj.GetCalculatedValues(DateTime.UtcNow);

        Assert.DoesNotContain(values, v => v.Key.EndsWith("Persistence", StringComparison.Ordinal));
        Assert.Equal(2, values.Count(v => v.Key.EndsWith("Volatility", StringComparison.Ordinal)));
    }

    [Fact]
    public void Hmm_NonFiniteStandardDeviation_YieldsANullNumericValueInsteadOfThrowing()
    {
        var obj = CreateHmm(2);
        obj.StateStdDevs = new[] { double.NaN, 0.01 };

        var row = Assert.Single(obj.GetCalculatedValues(DateTime.UtcNow), v => v.Key == "HMM State #1 Volatility");

        Assert.Null(row.NumericValue);
    }

    // ---------------------------------------------------------------- DTW

    private static DtwProjectionObject CreateMatchedDtw(bool ensemble)
    {
        var start = new DateTime(2024, 3, 1);
        var obj = new DtwProjectionObject
        {
            UseDbaEnsemble = ensemble,
            TopKMatches = 4,
            MatchedStartTime = new DateTime(2023, 5, 2),
            MatchedEndTime = new DateTime(2023, 6, 20),
            BestMatchDistance = 1.2345,
            BestMatchSimilarity = 87.62
        };
        obj.ProjectedPath = new List<StockAnalyzer.Core.Models.Point>
        {
            new(start.Ticks, 100.0), new(start.AddDays(1).Ticks, 104.0)
        };
        if (ensemble)
        {
            obj.UpperBandPath = new List<StockAnalyzer.Core.Models.Point> { new(start.Ticks, 100.0), new(start.AddDays(1).Ticks, 108.5) };
            obj.LowerBandPath = new List<StockAnalyzer.Core.Models.Point> { new(start.Ticks, 100.0), new(start.AddDays(1).Ticks, 101.25) };
        }

        return obj;
    }

    [Fact]
    public void Dtw_WithoutAMatch_ExposesNoRows()
    {
        Assert.Empty(new DtwProjectionObject().GetCalculatedValues(DateTime.UtcNow));

        var unmatched = CreateMatchedDtw(false);
        unmatched.IsUnmatched = true;
        Assert.Empty(unmatched.GetCalculatedValues(DateTime.UtcNow));
    }

    [Fact]
    public void Dtw_SingleBestMatch_ExposesModeTargetWindowAndSimilarity()
    {
        var values = CreateMatchedDtw(false).GetCalculatedValues(DateTime.UtcNow);

        Assert.Equal(new[] { "DTW Mode", "DTW Target Price", "DTW Matched Window", "DTW Similarity" }, values.Select(v => v.Key));
        Assert.Equal("Single Best Match", values[0].FormattedText);
        Assert.Equal(104m, values[1].NumericValue);
        Assert.Contains("+4.00%", values[1].FormattedText);
        Assert.Equal("2023/05/02 ~ 2023/06/20", values[2].FormattedText);
        Assert.Null(values[2].NumericValue);
        Assert.Equal(87.6m, values[3].NumericValue);
        Assert.StartsWith("87.6% (Dist=", values[3].FormattedText);
    }

    [Fact]
    public void Dtw_Ensemble_AddsTheEmpiricalBandRow()
    {
        var values = CreateMatchedDtw(true).GetCalculatedValues(DateTime.UtcNow);

        Assert.Equal("DBA Ensemble (Top-4)", values[0].FormattedText);
        var band = Assert.Single(values, v => v.Key == "DTW Band (15%-85%)");
        Assert.Equal(7.25m, band.NumericValue);
        Assert.Equal("101.25 ~ 108.50", band.FormattedText);
    }

    [Fact]
    public void Dtw_ZeroSimilarity_OmitsTheSimilarityRow()
    {
        var obj = CreateMatchedDtw(false);
        obj.BestMatchSimilarity = 0.0;

        Assert.DoesNotContain(obj.GetCalculatedValues(DateTime.UtcNow), v => v.Key == "DTW Similarity");
    }

    // ---------------------------------------------------------------- Sliced result + information provider

    [Fact]
    public void SlicedIndicatorResult_SlicesTheEffectiveDynamicPeriodsLikeTheSeries()
    {
        var original = IndicatorResult.Success(new List<decimal?> { 1m, 2m, 3m, 4m, 5m, 6m });
        var plain = new SlicedIndicatorResult(original, 2, 3);
        Assert.Null(plain.EffectiveDynamicPeriods);

        original.SetEffectiveDynamicPeriods(new List<decimal?> { 10m, 11m, 12m, 13m, 14m, 15m });
        var sliced = new SlicedIndicatorResult(original, 2, 3);

        var periods = sliced.EffectiveDynamicPeriods;
        Assert.NotNull(periods);
        Assert.Equal(3, periods!.Count);
        Assert.Equal(12m, periods[0]);
        Assert.Equal(14m, periods[2]);
        Assert.Null(periods[3]);
    }

    private static (List<CoreCandleData> Candles, List<CoreIndicatorSettings> Settings, Dictionary<string, IIndicatorResult> Results) BuildDrivenIndicator(string? driverId)
    {
        var candles = CreateCandles(10);
        var setting = new CoreIndicatorSettings
        {
            Id = "Adaptive",
            IsEnabled = true,
            TypeEnum = IndicatorType.EMA,
            ParameterObject = new CoreSmaParameter { Period = 5 },
            DynamicPeriodIndicatorId = driverId
        };

        var result = IndicatorResult.Success(candles.Select(c => (decimal?)c.Close).ToList());
        result.SetEffectiveDynamicPeriods(new List<decimal?> { null, null, 6.4m, 7.5m, 8m, 9m, 10m, 11m, 12m, 13m });
        return (candles, new List<CoreIndicatorSettings> { setting }, new Dictionary<string, IIndicatorResult> { ["Adaptive"] = result });
    }

    private static readonly string EffectivePeriodLabel = EffectivePeriodText.Label(key => LocalizationManager.Instance[key]);

    [Fact]
    public void ChartInformation_DrivenIndicator_AddsAnEffectivePeriodItemAtTheHoveredBar()
    {
        var (candles, settings, results) = BuildDrivenIndicator("Driver");

        var snapshot = ChartInformationDataProvider.Extract(candles, settings, results, null, candles[3].Timestamp, theme: ThemeColors.Dark);

        var item = Assert.Single(snapshot.Indicators, i => i.Name.EndsWith(EffectivePeriodLabel, StringComparison.Ordinal));
        Assert.Equal("8 bars", item.FormattedValue);

        var early = ChartInformationDataProvider.Extract(candles, settings, results, null, candles[1].Timestamp, theme: ThemeColors.Dark);
        Assert.Equal(EffectiveDynamicPeriodDisplay.UnavailableText,
            Assert.Single(early.Indicators, i => i.Name.EndsWith(EffectivePeriodLabel, StringComparison.Ordinal)).FormattedValue);
    }

    [Fact]
    public void ChartInformation_IndicatorWithoutADriver_AddsNoEffectivePeriodItem()
    {
        var (candles, settings, results) = BuildDrivenIndicator(null);

        var snapshot = ChartInformationDataProvider.Extract(candles, settings, results, null, candles[3].Timestamp, theme: ThemeColors.Dark);

        Assert.DoesNotContain(snapshot.Indicators, i => i.Name.EndsWith(EffectivePeriodLabel, StringComparison.Ordinal));
    }
}
