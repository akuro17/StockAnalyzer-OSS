using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Advanced;
using StockAnalyzer.Core.Models.Indicators.Trend;
using StockAnalyzer.Core.Models.Parameters;
using Xunit;

namespace StockAnalyzer.Core.Tests.Analysis;

/// <summary>
/// WebAI UI-exposure Batch 1: SSA AutoRank plumbing (Core side) and the pure HoughChannel geometry helpers.
/// </summary>
public class UiExposureBatch1Tests
{
    private const int SeriesLength = 80;
    private const int EmbeddingDimension = 10;

    private static (double[] Samples, DateTime[] Timestamps) CreateSeries(int n = SeriesLength)
    {
        var samples = new double[n];
        var timestamps = new DateTime[n];
        var start = new DateTime(2025, 1, 1);
        for (int i = 0; i < n; i++)
        {
            samples[i] = 100.0 + 0.3 * i + 4.0 * Math.Sin(2.0 * Math.PI * i / 9.0) + 1.5 * Math.Sin(2.0 * Math.PI * i / 5.0);
            timestamps[i] = start.AddDays(i);
        }

        return (samples, timestamps);
    }

    private static HoughDetectedLine Line(decimal startPrice, double slope) =>
        new(0.0, 0.0, 5, slope, 0.0, 0, 20, startPrice, startPrice + (decimal)(slope * 20), 0.5, 3, HoughLineType.Support);

    // ---------------------------------------------------------------- SSA: rank selector

    [Theory]
    [InlineData(SsaRankSelectionMethod.KaiserGuttman)]
    [InlineData(SsaRankSelectionMethod.ScreeMaxCurvature)]
    [InlineData(SsaRankSelectionMethod.CumulativeEnergy)]
    public void EstimateSignalRank_Decomposition_ReadsEigenvaluesInDescendingOrder(SsaRankSelectionMethod method)
    {
        var (samples, _) = CreateSeries();
        var decomp = SsaDecompositionEngine.Decompose(samples, EmbeddingDimension, SsaDetrendMode.LeastSquaresLinear);

        double[] sorted = decomp.SortedIndices.Select(i => decomp.Eigenvalues[i]).ToArray();
        Assert.True(sorted.Zip(sorted.Skip(1), (a, b) => a >= b).All(x => x));

        int expected = SsaRankSelector.EstimateSignalRank(sorted, method, SsaRankSelector.DefaultTargetEnergy, EmbeddingDimension - 1);
        int actual = SsaRankSelector.EstimateSignalRank(decomp, method, EmbeddingDimension - 1);

        Assert.Equal(expected, actual);
    }

    // ---------------------------------------------------------------- SSA: projection

    [Fact]
    public void CalculateProjection_AutoRankOff_IsIdenticalToTheDefaultCall()
    {
        var (samples, timestamps) = CreateSeries();

        var baseline = SsaProjectionAnalysis.CalculateProjection(
            samples, timestamps, futureSteps: 10, embeddingDimension: EmbeddingDimension, numComponents: 3);
        var explicitOff = SsaProjectionAnalysis.CalculateProjection(
            samples, timestamps, futureSteps: 10, embeddingDimension: EmbeddingDimension, numComponents: 3,
            autoRank: false, rankMethod: SsaRankSelectionMethod.KaiserGuttman);

        Assert.Equal(3, baseline.NumComponents);
        Assert.Equal(baseline.NumComponents, explicitOff.NumComponents);
        Assert.Equal(baseline.ProjectedPoints, explicitOff.ProjectedPoints);
        Assert.Equal(baseline.NuSquared, explicitOff.NuSquared);
    }

    [Theory]
    [InlineData(SsaRankSelectionMethod.KaiserGuttman)]
    [InlineData(SsaRankSelectionMethod.ScreeMaxCurvature)]
    [InlineData(SsaRankSelectionMethod.CumulativeEnergy)]
    public void CalculateProjection_AutoRankOn_UsesTheEstimatedRank_AndIgnoresNumComponents(SsaRankSelectionMethod method)
    {
        var (samples, timestamps) = CreateSeries();
        int k = SeriesLength - EmbeddingDimension + 1;
        var decomp = SsaDecompositionEngine.Decompose(samples, EmbeddingDimension, SsaDetrendMode.LeastSquaresLinear);
        int expectedRank = SsaRankSelector.EstimateSignalRank(decomp, method, Math.Min(EmbeddingDimension - 1, k));

        var result = SsaProjectionAnalysis.CalculateProjection(
            samples, timestamps, futureSteps: 10, embeddingDimension: EmbeddingDimension, numComponents: 1,
            autoRank: true, rankMethod: method);

        Assert.Equal(expectedRank, result.NumComponents);
        Assert.Equal(expectedRank, result.Components.Count);
        Assert.InRange(result.NumComponents, 1, EmbeddingDimension - 1);
    }

    [Fact]
    public void CalculateProjection_LegacyBoolOverload_StillBindsToTheNonAutoPath()
    {
        var (samples, timestamps) = CreateSeries();

        var legacy = SsaProjectionAnalysis.CalculateProjection(
            samples, timestamps, 10, TimeSpan.Zero, EmbeddingDimension, 2, true, true, 2.0m);

        Assert.Equal(2, legacy.NumComponents);
    }

    // ---------------------------------------------------------------- SSA: indicator

    private static List<CoreCandleData> CreateCandles(int count)
    {
        var (samples, timestamps) = CreateSeries(count);
        var candles = new List<CoreCandleData>(count);
        for (int i = 0; i < count; i++)
        {
            decimal close = Math.Round((decimal)samples[i], 4);
            candles.Add(new CoreCandleData(timestamps[i], close - 0.5m, close + 1m, close - 1m, close, 1000));
        }

        return candles;
    }

    [Fact]
    public void SsaIndicator_AutoRankDefaultsOff_AndExplicitOffMatchesTheDefaultSeries()
    {
        var candles = CreateCandles(SeriesLength);
        var baseline = new CoreSSAIndicator { WindowSize = 40, EmbeddingDimension = 10, NumComponents = 2 };
        var explicitOff = new CoreSSAIndicator { WindowSize = 40, EmbeddingDimension = 10, NumComponents = 2, AutoRank = false, RankMethod = SsaRankSelectionMethod.KaiserGuttman };

        Assert.False(baseline.AutoRank);
        baseline.Calculate(candles);
        explicitOff.Calculate(candles);

        Assert.Equal(baseline.Values.ToArray(), explicitOff.Values.ToArray());
    }

    [Fact]
    public void SsaIndicator_AutoRankOn_MatchesAManualPerWindowEstimate_AndStaysCausal()
    {
        var candles = CreateCandles(SeriesLength);
        const int window = 40;
        var indicator = new CoreSSAIndicator { WindowSize = window, EmbeddingDimension = 10, NumComponents = 1, AutoRank = true, RankMethod = SsaRankSelectionMethod.CumulativeEnergy };

        indicator.Calculate(candles);

        Assert.Equal(SeriesLength, indicator.Values.Count);
        Assert.All(indicator.Values.Take(window - 1), v => Assert.Null(v));

        // Manual reference for the last bar (the window only contains bars up to and including it).
        double[] closes = candles.Select(c => (double)c.Close).ToArray();
        var windowSpan = closes.AsSpan(SeriesLength - window, window);
        int l = Math.Clamp(10, 2, Math.Max(2, window / 2));
        int k = window - l + 1;
        var decomp = SsaDecompositionEngine.Decompose(windowSpan, l, SsaDetrendMode.LeastSquaresLinear);
        int r = SsaRankSelector.EstimateSignalRank(decomp, SsaRankSelectionMethod.CumulativeEnergy, Math.Min(l - 1, k));
        double expected = SsaDecompositionEngine.ComputeCausalEndpoint(windowSpan, decomp, l, r, SsaDetrendMode.LeastSquaresLinear);

        Assert.Equal(decimal.Round((decimal)expected, 8, MidpointRounding.AwayFromZero), indicator.Values[^1]);

        // Causality: truncating the future must not change an already computed bar.
        var truncated = new CoreSSAIndicator { WindowSize = window, EmbeddingDimension = 10, NumComponents = 1, AutoRank = true, RankMethod = SsaRankSelectionMethod.CumulativeEnergy };
        truncated.Calculate(candles.Take(SeriesLength - 10).ToList());
        Assert.Equal(truncated.Values[^1], indicator.Values[SeriesLength - 11]);
    }

    [Fact]
    public void SsaParameter_Defaults_KeepTheDisplayNameAndLegacyBehavior()
    {
        var parameter = new CoreSSAParameter();

        Assert.False(parameter.AutoRank);
        Assert.Equal(SsaRankSelectionMethod.CumulativeEnergy, parameter.RankMethod);

        var indicator = new CoreSSAIndicator();
        indicator.Configure(new CoreSSAParameter { AutoRank = true, RankMethod = SsaRankSelectionMethod.ScreeMaxCurvature });
        Assert.True(indicator.AutoRank);
        Assert.Equal(SsaRankSelectionMethod.ScreeMaxCurvature, indicator.RankMethod);
    }

    // ---------------------------------------------------------------- Hough: channel geometry

    [Fact]
    public void HoughChannel_GetWidthAt_ParallelLines_ReturnsTheLineGap()
    {
        var channel = new HoughChannel(Line(110m, 0.5), Line(100m, 0.5), 10m, 0.5, 0.7);

        Assert.Equal(10m, channel.GetWidthAt(0));
        Assert.Equal(10m, channel.GetWidthAt(8));
        Assert.Equal(0.7, channel.RelativePosition);
    }

    [Fact]
    public void HoughChannel_GetWidthAt_CrossedLines_NeverNegative()
    {
        var channel = new HoughChannel(Line(100m, -1.0), Line(100m, 1.0), 0m, 0.0, 0.5);

        Assert.Equal(0m, channel.GetWidthAt(5));
    }

    [Theory]
    [InlineData(100.0, 0.0)]
    [InlineData(105.0, 50.0)]
    [InlineData(110.0, 100.0)]
    [InlineData(120.0, 200.0)]
    [InlineData(95.0, -50.0)]
    [InlineData(1000000.0, 999.9)]
    [InlineData(-1000000.0, -999.9)]
    public void HoughChannel_GetPricePositionPercent_MapsLowerToZeroAndUpperToHundred(double price, double expectedPercent)
    {
        var channel = new HoughChannel(Line(110m, 0.0), Line(100m, 0.0), 10m, 0.0, 0.5);

        Assert.Equal(expectedPercent, (double)channel.GetPricePositionPercent(7, (decimal)price), 6);
    }

    [Fact]
    public void HoughChannel_GetPricePositionPercent_DegenerateWidth_ReturnsNeutral()
    {
        var channel = new HoughChannel(Line(100m, 0.0), Line(100m, 0.0), 0m, 0.0, 0.5);

        Assert.Equal(HoughChannel.NeutralPositionPercent, channel.GetPricePositionPercent(3, 123m));
    }

    // ---------------------------------------------------------------- Hough: parameter + indicators

    [Fact]
    public void HoughParameters_UseVolumeWeight_DefaultsOff_AndParticipatesInEquality()
    {
        Assert.False(new CoreHoughTrendStrengthParameter().UseVolumeWeight);
        Assert.False(new CoreHoughTrendAngleParameter().UseVolumeWeight);

        var off = new CoreHoughTrendStrengthParameter();
        var on = new CoreHoughTrendStrengthParameter { UseVolumeWeight = true };
        Assert.NotEqual(off, on);
        Assert.NotEqual(off.GetHashCode(), on.GetHashCode());

        var angleOff = new CoreHoughTrendAngleParameter();
        var angleOn = new CoreHoughTrendAngleParameter { UseVolumeWeight = true };
        Assert.NotEqual(angleOff, angleOn);
        Assert.Equal(angleOff, new CoreHoughTrendAngleParameter());
    }

    [Fact]
    public void HoughIndicators_Configure_ForwardsUseVolumeWeight_AndOffMatchesTheLegacySeries()
    {
        var candles = CreateCandles(70);

        var legacy = new CoreHoughTrendStrengthIndicator { Lookback = 30, PivotWindow = 2, VoteThreshold = 2 };
        var explicitOff = new CoreHoughTrendStrengthIndicator();
        explicitOff.Configure(new CoreHoughTrendStrengthParameter { Lookback = 30, PivotWindow = 2, VoteThreshold = 2, UseVolumeWeight = false });
        legacy.Calculate(candles);
        explicitOff.Calculate(candles);
        Assert.Equal(legacy.Values.ToArray(), explicitOff.Values.ToArray());

        var weighted = new CoreHoughTrendAngleIndicator();
        weighted.Configure(new CoreHoughTrendAngleParameter { UseVolumeWeight = true });
        Assert.True(weighted.UseVolumeWeight);

        var strengthWeighted = new CoreHoughTrendStrengthIndicator();
        strengthWeighted.Configure(new CoreHoughTrendStrengthParameter { Lookback = 30, PivotWindow = 2, VoteThreshold = 2, UseVolumeWeight = true });
        Assert.True(strengthWeighted.UseVolumeWeight);
        Assert.True(strengthWeighted.Calculate(candles).IsSuccessful);
    }
}
