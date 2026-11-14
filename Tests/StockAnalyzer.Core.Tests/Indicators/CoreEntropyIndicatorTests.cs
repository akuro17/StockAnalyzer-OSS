using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Advanced;
using StockAnalyzer.Core.Models.Parameters;
using Xunit;

namespace StockAnalyzer.Core.Tests.Indicators;

/// <summary>
/// WebAiExpansion plan 04 T5: Shannon entropy of the log returns of the last Period bars in BinCount equal-width bins, divided by ln(BinCount); 0 when all returns are equal.
/// First value at bar Period.
/// </summary>
public class CoreEntropyIndicatorTests
{
    private const double Tolerance = 1e-9;

    private static CoreCandleData Bar(int index, decimal close) =>
        new(new DateTime(2020, 1, 1).AddDays(index), close, close, close, close, 1000);

    private static List<CoreCandleData> FromCloses(IEnumerable<decimal> closes) => closes.Select((c, i) => Bar(i, c)).ToList();

    private static List<CoreCandleData> FromReturns(IReadOnlyList<double> returns)
    {
        var closes = new List<decimal> { 100m };
        double price = 100.0;
        foreach (double r in returns)
        {
            price *= Math.Exp(r);
            closes.Add((decimal)price);
        }

        return FromCloses(closes);
    }

    private static IReadOnlyList<decimal?> Run(IReadOnlyList<CoreCandleData> candles, int period, int binCount) =>
        new CoreEntropyIndicator { Period = period, BinCount = binCount }.Calculate(candles).MainValues;

    /// <summary>Prices 100, 200, 400, ...: returns +ln 2; where <paramref name="downAt"/> contains the return index the price halves instead (return -ln 2).</summary>
    private static List<CoreCandleData> DoublingPrices(int returnCount, params int[] downAt)
    {
        var closes = new List<decimal> { 100m };
        decimal price = 100m;
        for (int k = 1; k <= returnCount; k++)
        {
            price = downAt.Contains(k) ? price / 2m : price * 2m;
            closes.Add(price);
        }

        return FromCloses(closes);
    }

    [Fact]
    public void TwoValuesInEqualNumbers_WithTwoBins_GiveOne()
    {
        // Prices 100, 200, 100, 200, ...: the returns alternate +ln 2, -ln 2, five of each in a window of ten.
        var closes = Enumerable.Range(0, 21).Select(i => i % 2 == 0 ? 100m : 200m);

        var values = Run(FromCloses(closes), 10, 2);

        Assert.All(values.Take(10), v => Assert.Null(v));
        Assert.All(values.Skip(10), v => Assert.Equal(1.0, (double)v!.Value, Tolerance));
    }

    [Fact]
    public void TheResultIsNormalisedByLnOfTheBinCount()
    {
        // The same half-and-half window with ten bins: two bins hold 0.5 each -> ln 2 / ln 10.
        var closes = Enumerable.Range(0, 21).Select(i => i % 2 == 0 ? 100m : 200m);

        var values = Run(FromCloses(closes), 10, 10);

        Assert.Equal(Math.Log(2.0) / Math.Log(10.0), (double)values[10]!.Value, Tolerance);
    }

    [Fact]
    public void IdenticalReturns_GiveZero()
    {
        // Doubling every bar: all ten returns are exactly ln 2, so the range is 0.
        var values = Run(DoublingPrices(15), 10, 10);

        Assert.All(values.Take(10), v => Assert.Null(v));
        Assert.All(values.Skip(10), v => Assert.Equal(0m, v));
    }

    [Fact]
    public void AConstantPriceGivesZero()
    {
        var values = Run(FromCloses(Enumerable.Repeat(100m, 20)), 10, 10);

        Assert.All(values.Skip(10), v => Assert.Equal(0m, v));
    }

    [Fact]
    public void TenEquallySpacedReturnsInTenBins_GiveOne()
    {
        // r_k = -0.045 + 0.01 (k - 1) for k = 1..10: bins floor(10 k / 9) with the last one clamped to bin 9 -> one value per bin.
        var returns = Enumerable.Range(0, 10).Select(k => -0.045 + 0.01 * k).ToList();

        var values = Run(FromReturns(returns), 10, 10);

        Assert.Equal(11, values.Count);
        Assert.Equal(1.0, (double)values[10]!.Value, Tolerance);
    }

    [Fact]
    public void NineAgainstOne_WithTwoBins_MatchesTheFormula()
    {
        // One return of -ln 2 among nine of +ln 2 -> bins hold 1 and 9 values: H = -(0.1 ln 0.1 + 0.9 ln 0.9) / ln 2.
        var values = Run(DoublingPrices(10, downAt: 5), 10, 2);

        double expected = -(0.1 * Math.Log(0.1) + 0.9 * Math.Log(0.9)) / Math.Log(2.0);
        Assert.Equal(expected, (double)values[10]!.Value, Tolerance);
    }

    [Fact]
    public void TheValueIsInsideZeroToOne_AndTheFirstValueIsAtBarPeriod()
    {
        var random = new Random(12345);
        var returns = Enumerable.Range(0, 200).Select(_ => (random.NextDouble() - 0.5) * 0.04).ToList();

        var values = Run(FromReturns(returns), 30, 10);

        Assert.Equal(201, values.Count);
        Assert.All(values.Take(30), v => Assert.Null(v));
        Assert.All(values.Skip(30), v => Assert.InRange((double)v!.Value, 0.0, 1.0 + Tolerance));
    }

    [Fact]
    public void TheValueAtABarDoesNotChangeWhenLaterBarsAreRemoved()
    {
        var random = new Random(777);
        var all = FromReturns(Enumerable.Range(0, 200).Select(_ => (random.NextDouble() - 0.5) * 0.04).ToList());

        var full = Run(all, 30, 10);
        var truncated = Run(all.Take(120).ToList(), 30, 10);

        for (int i = 0; i < truncated.Count; i++) Assert.Equal(full[i], truncated[i]);
    }

    [Fact]
    public void AnUnusablePriceNullsEveryWindowContainingItsReturns()
    {
        var random = new Random(5);
        var candles = FromReturns(Enumerable.Range(0, 100).Select(_ => (random.NextDouble() - 0.5) * 0.04).ToList());
        candles[50] = Bar(50, 0m);   // r_50 and r_51 unusable

        var values = Run(candles, 10, 5);

        Assert.NotNull(values[49]);
        for (int i = 50; i <= 60; i++) Assert.Null(values[i]);
        Assert.NotNull(values[61]);   // window r_52 .. r_61
    }

    [Fact]
    public void EmptyInput_ReturnsASuccessfulEmptyResult()
    {
        var result = new CoreEntropyIndicator().Calculate(new List<CoreCandleData>());

        Assert.True(result.IsSuccessful);
        Assert.Empty(result.MainValues);
    }

    [Fact]
    public void Parameters_DefaultsAndRanges()
    {
        var p = new CoreEntropyParameter();

        Assert.Equal(30, p.Period);
        Assert.Equal(10, p.BinCount);
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreEntropyParameter { Period = 4 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreEntropyParameter { Period = 1001 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreEntropyParameter { BinCount = 1 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreEntropyParameter { BinCount = 101 }.Validate());

        var result = new CoreEntropyIndicator { BinCount = 1 }.Calculate(DoublingPrices(40));
        Assert.False(result.IsSuccessful);
    }

    [Fact]
    public void TheTypeIsCreatedByTheFactory()
    {
        Assert.IsType<CoreEntropyIndicator>(IndicatorFactory.CreateStatic(IndicatorType.Entropy));
    }
}
