using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Statistics;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.Core.Services.Screener;
using Xunit;

namespace StockAnalyzer.Core.Tests.Indicators;

public class JohansenCointegrationIndicatorTests
{
    private static (List<CoreCandleData> Primary, List<CoreCandleData> Comparison) BuildCointegratedPair(
        int count = 160,
        int seed = 101,
        double terminalSpreadBump = 0.0)
    {
        var rng = new Random(seed);
        var primary = new List<CoreCandleData>(count);
        var comparison = new List<CoreCandleData>(count);

        double logP2 = Math.Log(100.0);
        double u = 0.0;

        for (int i = 0; i < count; i++)
        {
            double rwStep = 0.015 * NextGaussian(rng);
            logP2 += rwStep;

            // Stationary AR(1) equilibrium error around 0 (rho = 0.25 -> fast mean reversion)
            u = 0.25 * u + 0.008 * NextGaussian(rng);
            double bump = (i == count - 1) ? terminalSpreadBump : 0.0;
            double logP1 = 1.4 * logP2 - 1.8 + u + bump;

            decimal p1 = (decimal)Math.Round(Math.Exp(logP1), 6);
            decimal p2 = (decimal)Math.Round(Math.Exp(logP2), 6);
            DateTime dt = new DateTime(2023, 1, 1).AddDays(i);

            primary.Add(new CoreCandleData(dt, p1, p1 * 1.002m, p1 * 0.998m, p1, 1000L + i));
            comparison.Add(new CoreCandleData(dt, p2, p2 * 1.002m, p2 * 0.998m, p2, 1000L + i));
        }

        return (primary, comparison);
    }

    private static (List<CoreCandleData> Primary, List<CoreCandleData> Comparison) BuildIndependentRandomWalks(
        int count = 160,
        int seed = 202)
    {
        var rng = new Random(seed);
        var primary = new List<CoreCandleData>(count);
        var comparison = new List<CoreCandleData>(count);
        double lp1 = Math.Log(100.0);
        double lp2 = Math.Log(100.0);

        for (int i = 0; i < count; i++)
        {
            lp1 += 0.015 * NextGaussian(rng);
            lp2 += 0.015 * NextGaussian(rng);
            decimal p1 = (decimal)Math.Round(Math.Exp(lp1), 6);
            decimal p2 = (decimal)Math.Round(Math.Exp(lp2), 6);
            DateTime dt = new DateTime(2023, 1, 1).AddDays(i);
            primary.Add(new CoreCandleData(dt, p1, p1, p1, p1, 1000L + i));
            comparison.Add(new CoreCandleData(dt, p2, p2, p2, p2, 1000L + i));
        }

        return (primary, comparison);
    }

    private static double NextGaussian(Random rng)
    {
        double u1 = 1.0 - rng.NextDouble();
        double u2 = 1.0 - rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    [Fact]
    public void Calculate_CointegratedPair_ExceedsCriticalValueAndIndependentPair()
    {
        var (pCoint, cCoint) = BuildCointegratedPair(count: 160, seed: 77);
        var (pIndep, cIndep) = BuildIndependentRandomWalks(count: 160, seed: 88);

        var indCoint = new CoreJohansenCointegrationIndicator { Period = 100, ComparisonSymbol = "COMP" };
        indCoint.SetSecondaryCandles(cCoint.Cast<CoreCandleData?>().ToList());
        IIndicatorResult resCoint = indCoint.Calculate(pCoint);

        var indIndep = new CoreJohansenCointegrationIndicator { Period = 100, ComparisonSymbol = "COMP" };
        indIndep.SetSecondaryCandles(cIndep.Cast<CoreCandleData?>().ToList());
        IIndicatorResult resIndep = indIndep.Calculate(pIndep);

        Assert.True(resCoint.IsSuccessful);
        Assert.True(resIndep.IsSuccessful);

        for (int i = 0; i < 99; i++)
        {
            Assert.Null(resCoint.MainValues[i]);
        }

        IReadOnlyList<decimal?> cv95 = resCoint.GetSeries(CoreJohansenCointegrationIndicator.CriticalValue95SeriesName)!;
        IReadOnlyList<decimal?> maxEig = resCoint.GetSeries(CoreJohansenCointegrationIndicator.MaxEigenStatSeriesName)!;
        Assert.Equal(15.4947m, cv95[^1]);
        Assert.True(resCoint.MainValues[^1]!.Value >= maxEig[^1]!.Value);
        Assert.True(resCoint.MainValues[^1]!.Value > 15.4947m, $"Expected cointegrated TraceStat ({resCoint.MainValues[^1]}) > 15.4947");
        Assert.True(resCoint.MainValues[^1]!.Value > resIndep.MainValues[^1]!.Value,
            $"Expected cointegrated TraceStat ({resCoint.MainValues[^1]}) > independent ({resIndep.MainValues[^1]})");
    }

    [Fact]
    public void Calculate_TerminalSpreadBump_ProducesPositiveSpreadZScore()
    {
        var (primary, comparison) = BuildCointegratedPair(count: 140, seed: 11, terminalSpreadBump: 0.08);
        var indicator = new CoreJohansenCointegrationIndicator { Period = 100, UseLogPrices = true, ComparisonSymbol = "COMP" };
        indicator.SetSecondaryCandles(comparison.Cast<CoreCandleData?>().ToList());

        IIndicatorResult result = indicator.Calculate(primary);
        Assert.True(result.IsSuccessful);

        IReadOnlyList<decimal?> spreadZ = result.GetSeries(CoreJohansenCointegrationIndicator.CointegrationSpreadZScoreSeriesName)!;
        Assert.NotNull(spreadZ[^1]);
        Assert.True(spreadZ[^1]!.Value > 2m, $"Expected positive spread Z-score on terminal upward bump, got {spreadZ[^1]}");
    }

    [Fact]
    public void Calculate_NonPositivePriceUnderLogMode_InvalidatesWindowAndRecovers()
    {
        var (primary, comparison) = BuildCointegratedPair(count: 140, seed: 33);
        const int badIdx = 60;
        primary[badIdx] = new CoreCandleData(primary[badIdx].Timestamp, 0m, 0m, 0m, 0m, 1000L);

        var indicator = new CoreJohansenCointegrationIndicator { Period = 30, UseLogPrices = true, ComparisonSymbol = "COMP" };
        indicator.SetSecondaryCandles(comparison.Cast<CoreCandleData?>().ToList());
        IIndicatorResult result = indicator.Calculate(primary);

        Assert.True(result.IsSuccessful);
        for (int i = badIdx; i < badIdx + indicator.Period; i++)
        {
            Assert.Null(result.MainValues[i]);
        }
        Assert.NotNull(result.MainValues[badIdx + indicator.Period]);
    }

    [Fact]
    public void Registration_ExplicitInterface_And_Eligibility_AreConsistentWithSsot()
    {
        var param = new CoreJohansenCointegrationParameter { ComparisonSymbol = "GLD" };
        param.Validate();
        Assert.Equal("JohansenCointegration (100, GLD)", param.GetDisplayName("JohansenCointegration"));
        Assert.Equal(100, param.GetRequiredWarmupBars());

        ICrossTickerParameter crossParam = param;
        Assert.False(crossParam.ComparisonSymbolOptional);
        Assert.Null(typeof(CoreJohansenCointegrationParameter).GetProperty(nameof(ICrossTickerParameter.ComparisonSymbolOptional), BindingFlags.Public | BindingFlags.Instance));

        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreJohansenCointegrationParameter { Period = 10 }.Validate());

        ICoreIndicator? created = IndicatorFactory.CreateStatic(IndicatorType.JohansenCointegration);
        Assert.IsType<CoreJohansenCointegrationIndicator>(created);
        Assert.IsAssignableFrom<ICrossTickerIndicator>(created);

        Assert.True(BacktestIndicatorEligibility.IsTypeBlocked(IndicatorType.JohansenCointegration));
        Assert.True(ScreenerIndicatorEligibility.IsTwoTickerType(IndicatorType.JohansenCointegration));
        Assert.False(ScreenerIndicatorEligibility.IsTypeBlocked(IndicatorType.JohansenCointegration));

        var mathGroup = ScreenerIndicatorGroup.GetDefaultGroups().Single(g => g.Name == "Math");
        Assert.Contains(IndicatorType.JohansenCointegration, mathGroup.IndicatorTypes);
    }

    [Fact]
    public void Calculate_IntradayTimestampOffset_DoesNotAlignFutureSameDayBar()
    {
        const int count = 60;
        const int period = 30;
        var primary = new List<CoreCandleData>(count);
        var secondary = new List<CoreCandleData>(count);
        var rng = new Random(123);
        DateTime start = new DateTime(2024, 1, 2, 9, 0, 0);

        decimal pA = 100m;
        decimal pB = 100m;
        for (int i = 0; i < count; i++)
        {
            pA += 0.1m + (decimal)(rng.NextDouble() - 0.5) * 0.5m;
            pB += 0.1m + (decimal)(rng.NextDouble() - 0.5) * 0.4m;

            DateTime tsPrimary = start.AddMinutes(5 * i);
            DateTime tsSecondary = start.AddMinutes(30 + 5 * i);
            primary.Add(new CoreCandleData(tsPrimary, pA, pA + 0.1m, pA - 0.1m, pA, 100L + i));
            secondary.Add(new CoreCandleData(tsSecondary, pB, pB + 0.1m, pB - 0.1m, pB, 100L + i));
        }

        var indicator = new CoreJohansenCointegrationIndicator
        {
            Period = period,
            ComparisonSymbol = "INTRA"
        };
        indicator.SetSecondaryCandles(secondary.Cast<CoreCandleData?>().ToList());

        IIndicatorResult result = indicator.Calculate(primary);
        Assert.True(result.IsSuccessful);

        // Bars 0..5 lack matching intraday secondary timestamps, so rolling Period=30 windows before index 35 cannot compute.
        for (int i = 0; i < 6 + period - 1; i++)
        {
            Assert.Null(result.MainValues[i]);
        }
        Assert.NotNull(result.MainValues[6 + period - 1]);
    }
}
