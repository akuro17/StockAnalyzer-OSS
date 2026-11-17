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

public class GrangerCausalityIndicatorTests
{
    private static (List<CoreCandleData> Primary, List<CoreCandleData> Comparison) BuildUnidirectionalCausalPair(
        int count = 120,
        int seed = 17,
        bool reverseSign = false)
    {
        var rng = new Random(seed);
        var primary = new List<CoreCandleData>(count);
        var comparison = new List<CoreCandleData>(count);

        double pA = 100.0;
        double pB = 100.0;
        double prevRB = 0.0;

        for (int i = 0; i < count; i++)
        {
            double u1 = 1.0 - rng.NextDouble();
            double u2 = 1.0 - rng.NextDouble();
            double nB = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);

            double u3 = 1.0 - rng.NextDouble();
            double u4 = 1.0 - rng.NextDouble();
            double nA = Math.Sqrt(-2.0 * Math.Log(u3)) * Math.Cos(2.0 * Math.PI * u4);

            double rB = 0.015 * nB;
            double rA = 0.85 * prevRB + 0.002 * nA;
            double rBOut = reverseSign ? -rB : rB;

            if (i > 0)
            {
                pA *= Math.Exp(rA);
                pB *= Math.Exp(rBOut);
            }

            DateTime dt = new DateTime(2023, 1, 1).AddDays(i);
            decimal cA = (decimal)Math.Round(pA, 6);
            decimal cB = (decimal)Math.Round(pB, 6);
            primary.Add(new CoreCandleData(dt, cA, cA * 1.002m, cA * 0.998m, cA, 1000L + i));
            comparison.Add(new CoreCandleData(dt, cB, cB * 1.002m, cB * 0.998m, cB, 1000L + i));

            prevRB = rB;
        }

        return (primary, comparison);
    }

    [Fact]
    public void Calculate_UnidirectionalCausality_ProducesLargeForwardFAndPositiveDiff()
    {
        var (primary, comparison) = BuildUnidirectionalCausalPair(count: 130, seed: 42);
        var indicator = new CoreGrangerCausalityIndicator
        {
            Period = 60,
            LagOrder = 2,
            ComparisonSymbol = "COMP"
        };
        indicator.SetSecondaryCandles(comparison.Cast<CoreCandleData?>().ToList());

        IIndicatorResult result = indicator.Calculate(primary);
        Assert.True(result.IsSuccessful);

        IReadOnlyList<decimal?> forward = result.MainValues;
        IReadOnlyList<decimal?> reverse = result.GetSeries(CoreGrangerCausalityIndicator.ReverseFStatSeriesName)!;
        IReadOnlyList<decimal?> diff = result.GetSeries(CoreGrangerCausalityIndicator.DirectionalDiffSeriesName)!;
        IReadOnlyList<decimal?> dcc = result.GetSeries(CoreGrangerCausalityIndicator.DccCorrelationSeriesName)!;

        // First usable bar is index Period (= 60) since return 0 is NaN.
        for (int i = 0; i < indicator.Period; i++)
        {
            Assert.Null(forward[i]);
            Assert.Null(dcc[i]);
        }

        for (int i = indicator.Period; i < primary.Count; i++)
        {
            Assert.NotNull(forward[i]);
            Assert.NotNull(reverse[i]);
            Assert.NotNull(diff[i]);
            Assert.NotNull(dcc[i]);
            Assert.InRange(dcc[i]!.Value, -1m, 1m);
            Assert.True(forward[i]!.Value >= 0m);
            Assert.True(reverse[i]!.Value >= 0m);
        }

        decimal lastForward = forward[^1]!.Value;
        decimal lastReverse = reverse[^1]!.Value;
        Assert.True(lastForward > 10m, $"Expected strong Granger F(B->A), got {lastForward}");
        Assert.True(lastForward > lastReverse * 2m, $"Expected F(B->A) ({lastForward}) > 2 * F(A->B) ({lastReverse})");
        Assert.True(diff[^1]!.Value > 5m);
    }

    [Fact]
    public void Calculate_WithoutSecondaryCandles_ReturnsNullSeries()
    {
        var (primary, _) = BuildUnidirectionalCausalPair(count: 80);
        var indicator = new CoreGrangerCausalityIndicator { Period = 40, LagOrder = 1 };

        IIndicatorResult result = indicator.Calculate(primary);
        Assert.True(result.IsSuccessful);
        Assert.Equal(80, result.MainValues.Count);
        Assert.All(result.MainValues, v => Assert.Null(v));
    }

    [Fact]
    public void Calculate_IsStrictlyCausal()
    {
        var (primary, comparison) = BuildUnidirectionalCausalPair(count: 90, seed: 5);
        var indicator = new CoreGrangerCausalityIndicator { Period = 40, LagOrder = 2, ComparisonSymbol = "COMP" };
        indicator.SetSecondaryCandles(comparison.Cast<CoreCandleData?>().ToList());
        IIndicatorResult baseline = indicator.Calculate(primary);

        var pExt = new List<CoreCandleData>(primary);
        var cExt = new List<CoreCandleData>(comparison);
        for (int i = 0; i < 5; i++)
        {
            DateTime dt = primary[^1].Timestamp.AddDays(i + 1);
            decimal pa = primary[^1].Close * (i % 2 == 0 ? 3m : 0.4m);
            decimal pb = comparison[^1].Close * (i % 2 == 0 ? 0.5m : 2.5m);
            pExt.Add(new CoreCandleData(dt, pa, pa, pa, pa, 1000L));
            cExt.Add(new CoreCandleData(dt, pb, pb, pb, pb, 1000L));
        }

        indicator.SetSecondaryCandles(cExt.Cast<CoreCandleData?>().ToList());
        IIndicatorResult extended = indicator.Calculate(pExt);

        for (int i = 0; i < primary.Count; i++)
        {
            Assert.Equal(baseline.MainValues[i], extended.MainValues[i]);
            Assert.Equal(
                baseline.GetSeries(CoreGrangerCausalityIndicator.DccCorrelationSeriesName)![i],
                extended.GetSeries(CoreGrangerCausalityIndicator.DccCorrelationSeriesName)![i]);
        }
    }

    [Fact]
    public void Registration_ExplicitInterface_And_Eligibility_AreConsistentWithSsot()
    {
        var param = new CoreGrangerCausalityParameter { ComparisonSymbol = "QQQ" };
        param.Validate();
        Assert.Equal("GrangerCausality (60, L2, QQQ)", param.GetDisplayName("GrangerCausality"));
        Assert.Equal(61, param.GetRequiredWarmupBars());

        ICrossTickerParameter crossParam = param;
        Assert.False(crossParam.ComparisonSymbolOptional);
        Assert.Null(typeof(CoreGrangerCausalityParameter).GetProperty(nameof(ICrossTickerParameter.ComparisonSymbolOptional), BindingFlags.Public | BindingFlags.Instance));

        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreGrangerCausalityParameter { Period = 10 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreGrangerCausalityParameter { LagOrder = 6 }.Validate());

        ICoreIndicator? created = IndicatorFactory.CreateStatic(IndicatorType.GrangerCausality);
        Assert.IsType<CoreGrangerCausalityIndicator>(created);
        Assert.IsAssignableFrom<ICrossTickerIndicator>(created);

        Assert.True(BacktestIndicatorEligibility.IsTypeBlocked(IndicatorType.GrangerCausality));
        Assert.True(ScreenerIndicatorEligibility.IsTwoTickerType(IndicatorType.GrangerCausality));
        Assert.False(ScreenerIndicatorEligibility.IsTypeBlocked(IndicatorType.GrangerCausality));

        var mathGroup = ScreenerIndicatorGroup.GetDefaultGroups().Single(g => g.Name == "Math");
        Assert.Contains(IndicatorType.GrangerCausality, mathGroup.IndicatorTypes);
    }

    [Fact]
    public void Calculate_ReturnScaleInvariance_ProducesIdenticalFStatistics()
    {
        const int count = 110;
        const int period = 50;
        const int lagOrder = 2;

        static (List<CoreCandleData> Primary, List<CoreCandleData> Comparison) BuildScaledPair(
            double scaleA,
            double scaleB)
        {
            var rng = new Random(31415);
            var primary = new List<CoreCandleData>(count);
            var comparison = new List<CoreCandleData>(count);
            double pA = 100.0;
            double pB = 100.0;
            double prevRB = 0.0;

            for (int i = 0; i < count; i++)
            {
                double u1 = 1.0 - rng.NextDouble();
                double u2 = rng.NextDouble();
                double nB = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);

                double u3 = 1.0 - rng.NextDouble();
                double u4 = rng.NextDouble();
                double nA = Math.Sqrt(-2.0 * Math.Log(u3)) * Math.Cos(2.0 * Math.PI * u4);

                double rB = 0.0004 * nB;
                double rA = 0.85 * prevRB + 0.0001 * nA;

                if (i > 0)
                {
                    pA *= Math.Exp(scaleA * rA);
                    pB *= Math.Exp(scaleB * rB);
                }

                DateTime dt = new DateTime(2023, 1, 1).AddDays(i);
                decimal cA = (decimal)pA;
                decimal cB = (decimal)pB;
                primary.Add(new CoreCandleData(dt, cA, cA * 1.001m, cA * 0.999m, cA, 1000L + i));
                comparison.Add(new CoreCandleData(dt, cB, cB * 1.001m, cB * 0.999m, cB, 1000L + i));

                prevRB = rB;
            }

            return (primary, comparison);
        }

        var (baseA, baseB) = BuildScaledPair(1.0, 1.0);
        var (scaledA, scaledB) = BuildScaledPair(1e-3, 1e2);

        var indBase = new CoreGrangerCausalityIndicator { Period = period, LagOrder = lagOrder, ComparisonSymbol = "COMP" };
        indBase.SetSecondaryCandles(baseB.Cast<CoreCandleData?>().ToList());
        IIndicatorResult resBase = indBase.Calculate(baseA);

        var indScaled = new CoreGrangerCausalityIndicator { Period = period, LagOrder = lagOrder, ComparisonSymbol = "COMP" };
        indScaled.SetSecondaryCandles(scaledB.Cast<CoreCandleData?>().ToList());
        IIndicatorResult resScaled = indScaled.Calculate(scaledA);

        Assert.True(resBase.IsSuccessful && resScaled.IsSuccessful);
        var revBase = resBase.GetSeries(CoreGrangerCausalityIndicator.ReverseFStatSeriesName)!;
        var revScaled = resScaled.GetSeries(CoreGrangerCausalityIndicator.ReverseFStatSeriesName)!;

        for (int i = period; i < count; i++)
        {
            double fBase = (double)resBase.MainValues[i]!.Value;
            double fScaled = (double)resScaled.MainValues[i]!.Value;
            Assert.True(
                Math.Abs(fBase - fScaled) <= 1e-6 * Math.Max(1.0, Math.Abs(fBase)),
                $"ForwardF mismatch at bar {i}: base={fBase}, scaled={fScaled}");

            double rBase = (double)revBase[i]!.Value;
            double rScaled = (double)revScaled[i]!.Value;
            Assert.True(
                Math.Abs(rBase - rScaled) <= 1e-6 * Math.Max(1.0, Math.Abs(rBase)),
                $"ReverseF mismatch at bar {i}: base={rBase}, scaled={rScaled}");
        }
    }

    [Fact]
    public void Calculate_ConstantSeries_ReturnsZeroWithoutException()
    {
        const int count = 90;
        const int period = 40;
        var primary = new List<CoreCandleData>(count);
        var comparison = new List<CoreCandleData>(count);
        for (int i = 0; i < count; i++)
        {
            DateTime dt = new DateTime(2023, 1, 1).AddDays(i);
            primary.Add(new CoreCandleData(dt, 100m, 100m, 100m, 100m, 1000L));
            comparison.Add(new CoreCandleData(dt, 200m, 200m, 200m, 200m, 1000L));
        }

        var indicator = new CoreGrangerCausalityIndicator
        {
            Period = period,
            LagOrder = 2,
            ComparisonSymbol = "CONST"
        };
        indicator.SetSecondaryCandles(comparison.Cast<CoreCandleData?>().ToList());

        IIndicatorResult result = indicator.Calculate(primary);
        Assert.True(result.IsSuccessful);
        var reverse = result.GetSeries(CoreGrangerCausalityIndicator.ReverseFStatSeriesName)!;
        var diff = result.GetSeries(CoreGrangerCausalityIndicator.DirectionalDiffSeriesName)!;

        for (int i = period; i < count; i++)
        {
            Assert.Equal(0m, result.MainValues[i]);
            Assert.Equal(0m, reverse[i]);
            Assert.Equal(0m, diff[i]);
        }
    }

    [Fact]
    public void Calculate_IntradayTimestampOffset_DoesNotAlignFutureSameDayBar()
    {
        const int count = 55;
        const int period = 30;
        var primary = new List<CoreCandleData>(count);
        var secondary = new List<CoreCandleData>(count);
        var rng = new Random(99);
        DateTime start = new DateTime(2024, 1, 2, 9, 0, 0);

        decimal pA = 100m;
        decimal pB = 50m;
        for (int i = 0; i < count; i++)
        {
            pA += (decimal)(rng.NextDouble() - 0.5) * 0.4m;
            pB += (decimal)(rng.NextDouble() - 0.5) * 0.3m;

            DateTime tsPrimary = start.AddMinutes(5 * i);
            DateTime tsSecondary = start.AddMinutes(30 + 5 * i);
            primary.Add(new CoreCandleData(tsPrimary, pA, pA + 0.1m, pA - 0.1m, pA, 100L + i));
            secondary.Add(new CoreCandleData(tsSecondary, pB, pB + 0.1m, pB - 0.1m, pB, 100L + i));
        }

        List<decimal?> aligned = PriceDataHelper.ExtractDateAlignedPriceSeries(
            primary,
            secondary.Cast<CoreCandleData?>().ToList(),
            PriceType.Close);

        // Bars 0..5 (09:00..09:25) must NOT read ahead to 09:30 on the same date.
        for (int i = 0; i < 6; i++)
        {
            Assert.Null(aligned[i]);
        }
        for (int i = 6; i < count; i++)
        {
            Assert.Equal(secondary[i - 6].Close, aligned[i]);
        }

        var indicator = new CoreGrangerCausalityIndicator
        {
            Period = period,
            LagOrder = 1,
            ComparisonSymbol = "INTRA"
        };
        indicator.SetSecondaryCandles(secondary.Cast<CoreCandleData?>().ToList());

        IIndicatorResult result = indicator.Calculate(primary);
        Assert.True(result.IsSuccessful);

        // First aligned return is index 7 (index 6 lacks previous aligned price), so first 30-bar return window ends at index 36.
        for (int i = 0; i < 6 + period; i++)
        {
            Assert.Null(result.MainValues[i]);
        }
        Assert.NotNull(result.MainValues[6 + period]);
    }
}
