using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Volatility;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Indicators;

/// <summary>Plan 02 T6: Garman-Klass optional Period aggregation (mean of variances) and annualisation.</summary>
public class CoreGarmanKlassAggregationTests
{
    private static readonly double Factor = 2.0 * Math.Log(2.0) - 1.0;

    private static CoreCandleData Bar(int day, decimal o, decimal h, decimal l, decimal c) => new(new DateTime(2026, 3, 2).AddDays(day), o, h, l, c, 1000);

    private static List<CoreCandleData> Varied(int count)
    {
        var list = new List<CoreCandleData>();
        for (int i = 0; i < count; i++)
        {
            decimal open = 100m + i % 7;
            list.Add(Bar(i, open, open + 1m + i % 3, open - 1m - i % 2, open + (i % 5 - 2) * 0.4m));
        }

        return list;
    }

    private static double Variance(CoreCandleData c)
    {
        double hl = Math.Log((double)(c.High / c.Low));
        double co = Math.Log((double)(c.Close / c.Open));
        return 0.5 * hl * hl - Factor * co * co;
    }

    private static IReadOnlyList<decimal?> Run(IReadOnlyList<CoreCandleData> candles, int period = 1, bool annualize = false, int perYear = 252)
    {
        var indicator = new CoreGarmanKlassVolatilityIndicator();
        indicator.Configure(new CoreGarmanKlassVolatilityParameter { Period = period, Annualize = annualize, PeriodsPerYear = perYear });
        return indicator.Calculate(candles).GetSeries("Main");
    }

    [Fact]
    public void Defaults_AreThePerBarValue_AndMatchAnIndependentCalculation()
    {
        var parameter = new CoreGarmanKlassVolatilityParameter();
        Assert.Equal(1, parameter.Period);
        Assert.False(parameter.Annualize);
        Assert.Equal(252, parameter.PeriodsPerYear);

        List<CoreCandleData> candles = Varied(40);
        candles.Add(Bar(40, 100m, 100m, 100m, 130m));   // negative variance -> null in the default
        candles.Add(Bar(41, 0m, 101m, 99m, 100m));      // non-positive price -> null
        IReadOnlyList<decimal?> byDefault = new CoreGarmanKlassVolatilityIndicator().Calculate(candles).GetSeries("Main");
        IReadOnlyList<decimal?> configured = Run(candles);

        for (int i = 0; i < candles.Count; i++)
        {
            decimal? expected = null;
            if (candles[i].Open > 0)
            {
                double v = Variance(candles[i]);
                if (double.IsFinite(v) && v >= 0) expected = (decimal)Math.Sqrt(v);
            }

            Assert.Equal(expected, byDefault[i]);
            Assert.Equal(expected, configured[i]);
        }

        Assert.Null(byDefault[40]);
        Assert.Null(byDefault[41]);
    }

    [Fact]
    public void OnePerBarValue_IsARatio_NotPercent()
    {
        // O=C and H/L = e: variance 0.5 -> 0.7071..., not 70.71.
        var candles = new List<CoreCandleData> { Bar(0, 100m, 271.8281828m, 100m, 100m) };
        AssertNear(Math.Sqrt(0.5), Run(candles)[0]);
    }

    [Fact]
    public void Period3_HandCalculation_UsesTheSquareRootOfTheMeanVariance()
    {
        // variances 0.5, 0.02 (H/L = e^0.2), 0.0 (flat bar).
        var candles = new List<CoreCandleData>
        {
            Bar(0, 100m, 271.8281828m, 100m, 100m),
            Bar(1, 100m, 122.1402758m, 100m, 100m),
            Bar(2, 100m, 100m, 100m, 100m),
        };
        IReadOnlyList<decimal?> result = Run(candles, period: 3);

        Assert.Null(result[0]);
        Assert.Null(result[1]);
        AssertNear(Math.Sqrt((0.5 + 0.02 + 0.0) / 3.0), result[2]);
    }

    [Fact]
    public void Period2_DiffersFromTheMeanOfTheStandardDeviations()
    {
        var candles = new List<CoreCandleData>
        {
            Bar(0, 100m, 271.8281828m, 100m, 100m),    // variance 0.5
            Bar(1, 100m, 122.1402758m, 100m, 100m),    // variance 0.02
        };
        decimal? aggregated = Run(candles, period: 2)[1];

        double expected = Math.Sqrt((0.5 + 0.02) / 2.0);                     // 0.5099...
        double meanOfStd = (Math.Sqrt(0.5) + Math.Sqrt(0.02)) / 2.0;         // 0.4242...
        AssertNear(expected, aggregated);
        Assert.True(Math.Abs((double)aggregated!.Value - meanOfStd) > 0.05);
    }

    [Fact]
    public void ANegativeVarianceInsideAValidWindow_IsUsed_AndAMeanBelowZeroGivesZero()
    {
        var candles = new List<CoreCandleData>
        {
            Bar(0, 100m, 100m, 100m, 271.8281828m),   // variance = -(2 ln2 - 1) * 1 = -0.386...
            Bar(1, 100m, 100m, 100m, 100m),           // 0
        };
        IReadOnlyList<decimal?> result = Run(candles, period: 2);
        Assert.Equal(0m, result[1]);   // mean -0.193 -> sqrt(max(0, .)) = 0
        Assert.Null(Run(candles)[0]);  // the default still refuses a negative variance
    }

    [Fact]
    public void AWindowWithANonPositivePrice_IsNull_AndLaterWindowsRecover()
    {
        var candles = new List<CoreCandleData>
        {
            Bar(0, 100m, 101m, 99m, 100m),
            Bar(1, 0m, 101m, 99m, 100m),     // O = 0 -> invalid
            Bar(2, 100m, 101m, 99m, 100m),
            Bar(3, 100m, 101m, 99m, 100m),
        };
        IReadOnlyList<decimal?> result = Run(candles, period: 2);

        Assert.Null(result[0]);
        Assert.Null(result[1]);   // window 0..1 contains the invalid bar
        Assert.Null(result[2]);   // window 1..2 contains it too
        Assert.NotNull(result[3]);
    }

    [Fact]
    public void PeriodsPerYear_IsIgnoredWhileAnnualizeIsOff()
    {
        List<CoreCandleData> candles = Varied(30);
        Assert.Equal(Run(candles, period: 3, annualize: false, perYear: 252), Run(candles, period: 3, annualize: false, perYear: 52));
    }

    [Fact]
    public void Annualize_MultipliesBySqrtOfPeriodsPerYear()
    {
        List<CoreCandleData> candles = Varied(30);
        IReadOnlyList<decimal?> plain = Run(candles, period: 3);
        IReadOnlyList<decimal?> annual = Run(candles, period: 3, annualize: true, perYear: 52);

        for (int i = 0; i < candles.Count; i++)
        {
            if (plain[i] is null)
            {
                Assert.Null(annual[i]);
                continue;
            }

            AssertNear((double)plain[i]!.Value * Math.Sqrt(52), annual[i]);
        }
    }

    [Fact]
    public void Annualize_WithPeriodOne_UsesTheAggregatedRule_AndClampsANegativeVarianceToZero()
    {
        var candles = new List<CoreCandleData> { Bar(0, 100m, 100m, 100m, 271.8281828m) };
        Assert.Equal(0m, Run(candles, period: 1, annualize: true)[0]);
    }

    [Fact]
    public void Parameter_DisplayNameAndValidation()
    {
        Assert.Equal("GK", new CoreGarmanKlassVolatilityParameter().GetDisplayName("GK"));
        Assert.Equal("GK (5)", new CoreGarmanKlassVolatilityParameter { Period = 5 }.GetDisplayName("GK"));
        Assert.Equal("GK (1)", new CoreGarmanKlassVolatilityParameter { Annualize = true }.GetDisplayName("GK"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreGarmanKlassVolatilityParameter { Period = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreGarmanKlassVolatilityParameter { Period = 1001 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreGarmanKlassVolatilityParameter { PeriodsPerYear = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreGarmanKlassVolatilityParameter { PeriodsPerYear = 367 }.Validate());
    }

    [Fact]
    public void OutputNames_AreUnchanged()
    {
        Assert.Equal(new[] { "GarmanKlassVolatility" }, IndicatorOutputSeriesResolver.GetDisplayNames(IndicatorType.GarmanKlassVolatility));
    }

    private static void AssertNear(double expected, decimal? actual)
    {
        Assert.NotNull(actual);
        Assert.True(Math.Abs(expected - (double)actual!.Value) < 1e-6, $"expected {expected} but was {actual}");
    }
}
