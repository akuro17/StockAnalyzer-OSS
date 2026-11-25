using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Services.Analysis.PatternEngines;
using Xunit;

namespace StockAnalyzer.Core.Tests.PatternEngines;

/// <summary>Plan ChartPatterns56 V2, run V2-1, plan R-14/R-15: the averages of the closes come from the shared helpers and one inconsistent bar never contaminates later values.</summary>
public class PatternSeriesContextTests
{
    private static decimal[] Closes(IEnumerable<CandleData> bars) => bars.Select(b => b.Close).ToArray();

    [Theory]
    [InlineData(10)]
    [InlineData(20)]
    public void Ema_WithNoInvalidBar_EqualsTheSharedEma(int period)
    {
        CandleData[] bars = PatternEngineTestBars.Walk(1, 300);
        PatternSeriesContext ctx = PatternEngineTestBars.Context(bars);
        List<decimal?> expected = IndicatorCalculationHelper.CalculateEma(Closes(bars), period);

        for (int i = 0; i < bars.Length; i++)
        {
            Assert.Equal(expected[i], ctx.Ema(period, i));
        }
    }

    [Theory]
    [InlineData(20)]
    [InlineData(50)]
    public void Sma_WithNoInvalidBar_EqualsTheSharedSma(int period)
    {
        CandleData[] bars = PatternEngineTestBars.Walk(2, 300);
        PatternSeriesContext ctx = PatternEngineTestBars.Context(bars);
        List<decimal?> expected = IndicatorCalculationHelper.CalculateSma(Closes(bars), period);

        for (int i = 0; i < bars.Length; i++)
        {
            Assert.Equal(expected[i], ctx.Sma(period, i));
        }
    }

    [Fact]
    public void Ema_InvalidBar_IsNullThere_AndLaterValuesEqualTheEmaOfTheSeriesWithoutThatBar()
    {
        const int bad = 100;
        CandleData[] bars = PatternEngineTestBars.Walk(3, 300);
        PatternSeriesContext ctx = PatternEngineTestBars.Context(PatternEngineTestBars.Corrupt(bars, bad));

        List<decimal?> withoutBar = IndicatorCalculationHelper.CalculateEma(Closes(bars.Where((_, i) => i != bad)), 10);

        Assert.Null(ctx.Ema(10, bad));
        for (int i = bad + 1; i < bars.Length; i++)
        {
            Assert.Equal(withoutBar[i - 1], ctx.Ema(10, i)); // the bad bar was skipped, the state carried over
        }

        for (int i = 20; i < bad; i++)
        {
            Assert.Equal(IndicatorCalculationHelper.CalculateEma(Closes(bars), 10)[i], ctx.Ema(10, i)); // before it: untouched
        }
    }

    [Fact]
    public void Sma_InvalidBar_IsUndefinedExactlyWhileItIsInTheWindow()
    {
        const int bad = 100;
        const int period = 20;
        CandleData[] bars = PatternEngineTestBars.Walk(4, 300);
        PatternSeriesContext ctx = PatternEngineTestBars.Context(PatternEngineTestBars.Corrupt(bars, bad));
        List<decimal?> clean = IndicatorCalculationHelper.CalculateSma(Closes(bars), period);

        for (int i = period; i < bars.Length; i++)
        {
            bool inWindow = i - period + 1 <= bad && bad <= i;
            if (inWindow)
            {
                Assert.Null(ctx.Sma(period, i));
            }
            else
            {
                Assert.Equal(clean[i], ctx.Sma(period, i)); // the bad bar changed only its High and Open, never a close
            }
        }
    }

    [Fact]
    public void AllValid_FollowsTheInconsistentBars()
    {
        PatternSeriesContext ctx = PatternEngineTestBars.Context(PatternEngineTestBars.Corrupt(PatternEngineTestBars.Walk(5, 50), 25));

        Assert.True(ctx.AllValid(0, 24));
        Assert.False(ctx.AllValid(0, 25));
        Assert.False(ctx.AllValid(25, 25));
        Assert.True(ctx.AllValid(26, 49));
        Assert.False(ctx.IsValid(25));
        Assert.True(ctx.IsValid(24));
    }

    [Theory]
    [InlineData(-1, 5)]
    [InlineData(0, 50)]   // beyond the series
    [InlineData(10, 9)]   // empty window
    [InlineData(10, 100)]
    public void AllValid_OutOfRangeOrEmptyWindow_IsFalse(int from, int to)
        => Assert.False(PatternEngineTestBars.Context(PatternEngineTestBars.Walk(6, 50)).AllValid(from, to));

    [Fact]
    public void Constructor_ReadsOnlyTheFirstCountBars_OfALongerList()
    {
        CandleData[] bars = PatternEngineTestBars.Walk(7, 80);
        var ctx = new PatternSeriesContext(bars, 60);

        Assert.Equal(60, ctx.Count);
        Assert.False(ctx.AllValid(0, 60));
        Assert.Null(ctx.Ema(10, 60));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PatternSeriesContext(bars, 81));
    }
}
