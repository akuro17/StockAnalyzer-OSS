using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Core.Tests.Analysis;

/// <summary>
/// WebAiExpansion plan 03 T7 (decision U-06=A): the allocation-free FillProfile / FindPointOfControlIndex(span) / ComputeValueArea must give exactly what the
/// list-based CalculateProfile / FindPointOfControl / CalculateValueArea give (the list-based functions are unchanged).
/// </summary>
public class VolumeAnalysisSpanTests
{
    private const int Seed = 20261114;

    /// <summary>300 candles of a seeded random walk; some candles are flat (High = Low), some have zero volume, some touch many rows.</summary>
    internal static List<CoreCandleData> RandomCandles(int count, int seed = Seed)
    {
        var rng = new Random(seed);
        var list = new List<CoreCandleData>(count);
        decimal price = 1000m;
        for (int i = 0; i < count; i++)
        {
            decimal open = price;
            decimal close = Math.Round(price * (decimal)(0.97 + rng.NextDouble() * 0.06), 4);
            decimal high = Math.Max(open, close) + Math.Round((decimal)rng.NextDouble() * 12m, 4);
            decimal low = Math.Min(open, close) - Math.Round((decimal)rng.NextDouble() * 12m, 4);
            if (i % 17 == 5) { high = close; low = close; open = close; }
            long volume = i % 23 == 7 ? 0 : rng.NextInt64(1, 5_000_000);
            list.Add(new CoreCandleData(new DateTime(2024, 1, 1).AddDays(i), open, high, low, close, volume));
            price = close;
        }

        return list;
    }

    [Theory]
    [InlineData(20, 10, VolumeDistributionMode.Proportional, 0.70)]
    [InlineData(50, 24, VolumeDistributionMode.Full, 0.70)]
    [InlineData(200, 50, VolumeDistributionMode.Proportional, 0.70)]
    [InlineData(30, 17, VolumeDistributionMode.Proportional, 0.35)]
    public void EveryWindowGivesTheSameBinVolumesPocVahAndValAsTheListBasedFunctions(int period, int rowCount, VolumeDistributionMode mode, double valueAreaPercent)
    {
        var candles = RandomCandles(300);
        decimal[] volumes = new decimal[rowCount];
        int compared = 0;

        for (int end = period - 1; end < candles.Count; end++)
        {
            var window = candles.Skip(end - period + 1).Take(period).ToList();
            var bins = VolumeAnalysis.CalculateProfile(window, rowCount, mode);

            bool filled = VolumeAnalysis.FillProfile(window.ToArray(), volumes, mode, out decimal minPrice, out decimal binSize);

            if (bins.Count == 0)
            {
                Assert.False(filled, $"end {end}: the list-based profile is empty");
                Assert.All(volumes, v => Assert.Equal(0m, v));
                continue;
            }

            Assert.True(filled, $"end {end}");
            Assert.Equal(rowCount, bins.Count);
            for (int k = 0; k < rowCount; k++)
            {
                Assert.Equal((decimal)bins[k].TotalVolume, volumes[k]);
                Assert.Equal(bins[k].LowerBound, VolumeAnalysis.BinLowerBound(minPrice, binSize, k));
                Assert.Equal(bins[k].UpperBound, VolumeAnalysis.BinUpperBound(minPrice, binSize, k));
                Assert.Equal(bins[k].Price, VolumeAnalysis.BinPrice(minPrice, binSize, k));
            }

            int pocIndex = VolumeAnalysis.FindPointOfControlIndexFromVolumes(volumes);
            Assert.Equal(VolumeAnalysis.FindPointOfControlIndex(bins), pocIndex);
            Assert.Equal(VolumeAnalysis.FindPointOfControl(bins)!.Price, VolumeAnalysis.BinPrice(minPrice, binSize, pocIndex));

            var expected = VolumeAnalysis.CalculateValueArea(bins, valueAreaPercent);
            VolumeAnalysis.ComputeValueArea(volumes, minPrice, binSize, valueAreaPercent, out decimal vah, out decimal val);
            Assert.Equal(expected.VAH, vah);
            Assert.Equal(expected.VAL, val);
            compared++;
        }

        Assert.True(compared > 100, "most windows must give a profile");
    }

    [Fact]
    public void AnEmptyWindowOrAWindowOfOnePriceGivesNoProfile_LikeTheListBasedFunction()
    {
        decimal[] volumes = new decimal[10];
        Assert.False(VolumeAnalysis.FillProfile(ReadOnlySpan<CoreCandleData>.Empty, volumes, VolumeDistributionMode.Proportional, out _, out _));

        var flat = Enumerable.Range(0, 5).Select(i => new CoreCandleData(new DateTime(2024, 1, 1).AddDays(i), 100m, 100m, 100m, 100m, 1000)).ToArray();
        Assert.False(VolumeAnalysis.FillProfile(flat, volumes, VolumeDistributionMode.Proportional, out _, out _));
        Assert.Empty(VolumeAnalysis.CalculateProfile(flat, 10));
    }

    [Fact]
    public void FillProfileClearsTheArrayFirst()
    {
        var candles = RandomCandles(40);
        decimal[] volumes = Enumerable.Repeat(999m, 12).ToArray();

        VolumeAnalysis.FillProfile(candles.Take(30).ToArray(), volumes, VolumeDistributionMode.Proportional, out _, out _);
        decimal[] fresh = new decimal[12];
        VolumeAnalysis.FillProfile(candles.Take(30).ToArray(), fresh, VolumeDistributionMode.Proportional, out _, out _);

        Assert.Equal(fresh, volumes);
    }

    [Fact]
    public void PointOfControlTie_TakesTheLowestIndex_AndAnEmptySpanGivesMinusOne()
    {
        Assert.Equal(1, VolumeAnalysis.FindPointOfControlIndexFromVolumes(new decimal[] { 5m, 9m, 9m, 2m }));
        Assert.Equal(0, VolumeAnalysis.FindPointOfControlIndexFromVolumes(new decimal[] { 0m, 0m, 0m }));
        Assert.Equal(-1, VolumeAnalysis.FindPointOfControlIndexFromVolumes(ReadOnlySpan<decimal>.Empty));
    }

    [Fact]
    public void ValueAreaOfAnEmptyOrSingleBinSpan_FollowsTheListBasedRules()
    {
        VolumeAnalysis.ComputeValueArea(ReadOnlySpan<decimal>.Empty, 10m, 2m, 0.7, out decimal vah, out decimal val);
        Assert.Equal((0m, 0m), (vah, val));

        VolumeAnalysis.ComputeValueArea(new decimal[] { 5m }, 10m, 2m, 0.7, out vah, out val);
        Assert.Equal((12m, 10m), (vah, val));
    }
}
