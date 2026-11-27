using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Calculator;
using StockAnalyzer.Core.Drawing;
using StockAnalyzer.Core.Drawing.Values;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Core.Tests.Drawing;

/// <summary>The bootstrap confidence band of a drawing range (DSL expansion run D-6, DX-7).</summary>
public sealed class BootstrapConfidenceBandValuesTests
{
    private static readonly DateTime Start = new(2026, 1, 1);

    private static List<CoreCandleData> Candles(int count, decimal first = 100m)
    {
        double state = 7.0;
        var list = new List<CoreCandleData>(count);
        decimal close = first;
        for (int i = 0; i < count; i++)
        {
            state = ((state * 1103515245.0) + 12345.0) % 2147483648.0;
            close += Math.Round((decimal)((state / 2147483648.0) - 0.5) * 2m, 4) + 0.05m;
            list.Add(new CoreCandleData(Start.AddDays(i), close, close, close, close, 1000L));
        }

        return list;
    }

    private static BootstrapBand Run(List<CoreCandleData> candles, int first, int last, BootstrapStatKind kind, decimal confidence = 0.95m, int resamples = 200, int block = 5, BootstrapBlockMode mode = BootstrapBlockMode.CircularBlock)
        => BootstrapConfidenceBandValues.Calculate(candles, first, last, kind, confidence, resamples, block, mode);

    [Fact]
    public void SameInput_GivesTheSameInterval_AndTheSeedIs42()
    {
        var candles = Candles(120);

        BootstrapBand a = Run(candles, 10, 100, BootstrapStatKind.Mean);
        BootstrapBand b = Run(candles, 10, 100, BootstrapStatKind.Mean);

        Assert.True(a.IsOk);
        Assert.Equal((a.Estimate, a.Lower, a.Upper, a.StandardError), (b.Estimate, b.Lower, b.Upper, b.StandardError));
        Assert.Equal(42UL, BootstrapConfidenceBandValues.Seed);

        // The tool adds nothing to the kernel: the same call on the same closes gives the same numbers.
        decimal[] closes = candles.Skip(10).Take(91).Select(c => c.Close).ToArray();
        DslExecutionBudget budget = DslExecutionBudget.Create(CalculatorLimits.Default.MaxExecutionOps);
        MathResult<BootstrapResult> direct = DslResamplingMath.BootConfidenceInterval(closes, BootstrapStatKind.Mean, 200, 5, 0.05, 42UL, BootstrapBlockMode.CircularBlock, ref budget);
        Assert.True(direct.IsOk);
        Assert.Equal(direct.Value.Estimate, a.Estimate);
        Assert.Equal(direct.Value.Lower, a.Lower);
        Assert.Equal(direct.Value.Upper, a.Upper);
        Assert.True(a.Lower <= a.Estimate && a.Estimate <= a.Upper);
        Assert.Equal(closes.Average(), a.Estimate, 6);
    }

    [Theory]
    [InlineData(BootstrapStatKind.Mean, 9, BootstrapBandStatus.TooFewSamples, 0, 0)]
    [InlineData(BootstrapStatKind.Mean, 10, BootstrapBandStatus.Ok, 10, 10)]
    [InlineData(BootstrapStatKind.Mean, 11, BootstrapBandStatus.Ok, 11, 11)]
    [InlineData(BootstrapStatKind.Median, 512, BootstrapBandStatus.Ok, 512, 512)]
    [InlineData(BootstrapStatKind.Median, 513, BootstrapBandStatus.Ok, 512, 512)]
    [InlineData(BootstrapStatKind.Mean, 514, BootstrapBandStatus.Ok, 512, 512)]
    [InlineData(BootstrapStatKind.StdDev, 10, BootstrapBandStatus.TooFewSamples, 0, 0)]
    [InlineData(BootstrapStatKind.StdDev, 11, BootstrapBandStatus.Ok, 11, 10)]
    [InlineData(BootstrapStatKind.Sharpe, 12, BootstrapBandStatus.Ok, 12, 11)]
    [InlineData(BootstrapStatKind.StdDev, 512, BootstrapBandStatus.Ok, 512, 511)]
    [InlineData(BootstrapStatKind.StdDev, 513, BootstrapBandStatus.Ok, 513, 512)]
    [InlineData(BootstrapStatKind.Sharpe, 514, BootstrapBandStatus.Ok, 513, 512)]
    public void RangeBoundaries_PriceStatisticsTakeBars_ReturnStatisticsTakeOneBarMore(BootstrapStatKind kind, int rangeBars, BootstrapBandStatus status, int effectiveBars, int samples)
    {
        var candles = Candles(600);

        BootstrapBand band = Run(candles, 20, 20 + rangeBars - 1, kind);

        Assert.Equal(status, band.Status);
        Assert.Equal(rangeBars, band.RangeBars);
        Assert.Equal(effectiveBars, band.EffectiveBars);
        Assert.Equal(samples, band.SampleCount);
        Assert.Equal(rangeBars > effectiveBars && effectiveBars > 0, band.IsTruncated);
    }

    [Fact]
    public void Truncation_UsesTheNewestBars_AndSaysSo()
    {
        var candles = Candles(600);

        BootstrapBand price = Run(candles, 0, 599, BootstrapStatKind.Mean);
        BootstrapBand ret = Run(candles, 0, 599, BootstrapStatKind.StdDev);

        Assert.Equal(candles[600 - 512].Timestamp, price.WindowStart);
        Assert.Equal(candles[599].Timestamp, price.WindowEnd);
        Assert.Equal(candles[600 - 513].Timestamp, ret.WindowStart);
        Assert.Equal("Last 512 bars", BootstrapConfidenceBandValues.TruncationBadge(price));
        Assert.Equal("Last 512 returns", BootstrapConfidenceBandValues.TruncationBadge(ret));
        Assert.Contains("Last 512 returns", BootstrapConfidenceBandValues.Label(ret));
        Assert.Equal(string.Empty, BootstrapConfidenceBandValues.TruncationBadge(Run(candles, 0, 99, BootstrapStatKind.Mean)));
    }

    [Fact]
    public void ReturnStatistics_UseSimpleReturnsOfTheClosesInsideTheRangeOnly()
    {
        var candles = Candles(120);
        BootstrapBand baseline = Run(candles, 30, 90, BootstrapStatKind.StdDev);

        // The bar before the range is never read: changing it (even to a non-positive close) changes nothing.
        var changed = candles.ToList();
        changed[29] = new CoreCandleData(changed[29].Timestamp, 0m, 0m, 0m, 0m, 1L);
        BootstrapBand again = Run(changed, 30, 90, BootstrapStatKind.StdDev);
        Assert.Equal((baseline.Estimate, baseline.Lower, baseline.Upper), (again.Estimate, again.Lower, again.Upper));

        // The series is the simple return (P_k - P_{k-1}) / P_{k-1}.
        decimal[] returns = new decimal[60];
        for (int i = 0; i < 60; i++)
        {
            returns[i] = (candles[31 + i].Close - candles[30 + i].Close) / candles[30 + i].Close;
        }

        DslExecutionBudget budget = DslExecutionBudget.Create(CalculatorLimits.Default.MaxExecutionOps);
        MathResult<BootstrapResult> direct = DslResamplingMath.BootConfidenceInterval(returns, BootstrapStatKind.StdDev, 200, 5, 0.05, 42UL, BootstrapBlockMode.CircularBlock, ref budget);
        Assert.True(direct.IsOk);
        Assert.Equal(direct.Value.Estimate, baseline.Estimate);
        Assert.Equal(direct.Value.Upper, baseline.Upper);
        Assert.Equal(60, baseline.SampleCount);
        Assert.False(baseline.IsPriceLevel);
        Assert.Equal("Return", BootstrapConfidenceBandValues.UnitName(BootstrapStatKind.Sharpe));
        Assert.Equal("Price", BootstrapConfidenceBandValues.UnitName(BootstrapStatKind.Median));
    }

    [Fact]
    public void ReturnStatistic_WithANonPositiveCloseInsideTheRange_Fails()
    {
        var candles = Candles(60);
        candles[40] = new CoreCandleData(candles[40].Timestamp, 0m, 0m, 0m, 0m, 1L);

        BootstrapBand band = Run(candles, 20, 55, BootstrapStatKind.Sharpe);

        Assert.Equal(BootstrapBandStatus.Failed, band.Status);
        Assert.Equal("Bootstrap failed", BootstrapConfidenceBandValues.StatusText(band.Status));
    }

    [Theory]
    [InlineData(200, 0.99, true)]    // 200 x 0.01 = 2: the smallest the kernel takes
    [InlineData(100, 0.99, false)]   // 1 resample in the tail
    [InlineData(50, 0.95, true)]     // 2.5
    [InlineData(50, 0.97, false)]    // 1.5
    [InlineData(1000, 0.99, true)]
    [InlineData(150, 0.99, false)]   // 1.5
    public void TailResolution_IsReportedNeverClamped(int resamples, double confidence, bool ok)
    {
        var candles = Candles(80);

        BootstrapBand band = Run(candles, 10, 70, BootstrapStatKind.Mean, (decimal)confidence, resamples);

        Assert.Equal(ok ? BootstrapBandStatus.Ok : BootstrapBandStatus.TailResolution, band.Status);
        Assert.Equal(resamples, band.Resamples);   // the setting is kept, not changed
        if (!ok)
        {
            Assert.Equal("Increase Resamples or lower Confidence", BootstrapConfidenceBandValues.StatusText(band.Status));
        }
    }

    [Fact]
    public void Settings_AreClamped_AndTheBlockIsAtMostHalfTheSeries()
    {
        var candles = Candles(80);

        BootstrapBand low = Run(candles, 10, 70, BootstrapStatKind.Mean, 0.1m, 5, 0);
        Assert.Equal(0.80m, low.ConfidenceLevel);
        Assert.Equal(BootstrapConfidenceBandValues.MinResamples, low.Resamples);
        Assert.Equal(BootstrapConfidenceBandValues.MinBlockLength, low.BlockLength);

        BootstrapBand high = Run(candles, 10, 70, BootstrapStatKind.Mean, 5m, 5000, 500);
        Assert.Equal(0.99m, high.ConfidenceLevel);
        Assert.Equal(BootstrapConfidenceBandValues.MaxResamples, high.Resamples);
        Assert.Equal(BootstrapConfidenceBandValues.MaxBlockLength, high.BlockLength);

        // 10 samples: half = 5, so a block of 20 becomes 5.
        BootstrapBand shortSeries = Run(candles, 10, 19, BootstrapStatKind.Mean, 0.95m, 200, 20);
        Assert.True(shortSeries.IsOk);
        Assert.Equal(5, shortSeries.BlockLength);
    }

    [Fact]
    public void BlockMode_ChangesTheResamplingNotTheEstimate()
    {
        var candles = Candles(100);

        BootstrapBand circular = Run(candles, 10, 90, BootstrapStatKind.Mean, mode: BootstrapBlockMode.CircularBlock);
        BootstrapBand stationary = Run(candles, 10, 90, BootstrapStatKind.Mean, mode: BootstrapBlockMode.Stationary);

        Assert.True(circular.IsOk && stationary.IsOk);
        Assert.Equal(circular.Estimate, stationary.Estimate);
        Assert.NotEqual((circular.Lower, circular.Upper), (stationary.Lower, stationary.Upper));
    }

    [Fact]
    public void NoRange_IsEmpty_AndHasNoValues()
    {
        var candles = Candles(50);

        Assert.Equal(BootstrapBandStatus.NoRange, BootstrapConfidenceBandValues.Calculate(candles, 5, 3, BootstrapStatKind.Mean, 0.95m, 200, 5, BootstrapBlockMode.CircularBlock).Status);
        Assert.Equal(BootstrapBandStatus.NoRange, BootstrapConfidenceBandValues.Calculate(candles, 0, 50, BootstrapStatKind.Mean, 0.95m, 200, 5, BootstrapBlockMode.CircularBlock).Status);
        Assert.Empty(BootstrapConfidenceBandValues.Calculate(BootstrapBand.Empty, IndicatorColor.Gray));
    }

    [Fact]
    public void Rows_PriceAndReturnKinds_AndStateRow()
    {
        var candles = Candles(120);

        IReadOnlyList<DrawingCalculatedValue> price = BootstrapConfidenceBandValues.Calculate(Run(candles, 10, 100, BootstrapStatKind.Median), IndicatorColor.Gray);
        Assert.Equal(
            new[] { "BootEstimate", "BootLower", "BootUpper", "BootStandardError", "BootConfidence", "BootResamples", "BootBlockLength", "SampleCount", "RangeBars", "EffectiveBars" },
            price.Select(v => v.Key).ToArray());
        Assert.Equal(91, price.Single(v => v.Key == "RangeBars").NumericValue);
        Assert.Equal(91, price.Single(v => v.Key == "EffectiveBars").NumericValue);
        Assert.Equal(91, price.Single(v => v.Key == "SampleCount").NumericValue);

        IReadOnlyList<DrawingCalculatedValue> ret = BootstrapConfidenceBandValues.Calculate(Run(candles, 10, 100, BootstrapStatKind.Sharpe), IndicatorColor.Gray);
        Assert.Equal(91, ret.Single(v => v.Key == "RangeBars").NumericValue);
        Assert.Equal(90, ret.Single(v => v.Key == "SampleCount").NumericValue);

        IReadOnlyList<DrawingCalculatedValue> state = BootstrapConfidenceBandValues.Calculate(Run(candles, 10, 15, BootstrapStatKind.Mean), IndicatorColor.Gray);
        Assert.Equal("State", state[0].Key);
        Assert.Equal("Insufficient samples (< 10)", state[0].FormattedText);
        Assert.Equal(new[] { "State", "SampleCount", "RangeBars", "EffectiveBars" }, state.Select(v => v.Key).ToArray());
    }

    [Fact]
    public void Label_ShowsTheStatisticTheUnitAndTheInterval()
    {
        var candles = Candles(120);

        string price = BootstrapConfidenceBandValues.Label(Run(candles, 10, 100, BootstrapStatKind.Mean));
        string ret = BootstrapConfidenceBandValues.Label(Run(candles, 10, 100, BootstrapStatKind.StdDev));

        Assert.StartsWith("Mean (Price) 95% CI:", price);
        Assert.StartsWith("StdDev (Return) 95% CI:", ret);
        Assert.Equal("Insufficient samples (< 10)", BootstrapConfidenceBandValues.Label(Run(candles, 10, 15, BootstrapStatKind.Mean)));
    }

    [Fact]
    public void MinBarsAndMaxBars_FollowTheStatisticKind()
    {
        Assert.Equal(10, BootstrapConfidenceBandValues.MinBars(BootstrapStatKind.Mean));
        Assert.Equal(512, BootstrapConfidenceBandValues.MaxBars(BootstrapStatKind.Median));
        Assert.Equal(11, BootstrapConfidenceBandValues.MinBars(BootstrapStatKind.StdDev));
        Assert.Equal(513, BootstrapConfidenceBandValues.MaxBars(BootstrapStatKind.Sharpe));
    }
}
