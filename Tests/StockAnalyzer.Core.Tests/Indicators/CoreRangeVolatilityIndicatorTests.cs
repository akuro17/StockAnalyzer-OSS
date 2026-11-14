using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Volatility;
using StockAnalyzer.Core.Models.Parameters;
using Xunit;

namespace StockAnalyzer.Core.Tests.Indicators;

/// <summary>
/// WebAiExpansion plan 04 T1/T2: Parkinson (v = ln(H/L)^2 / (4 ln 2)) and Rogers-Satchell (v = ln(H/C) ln(H/O) + ln(L/C) ln(L/O)) volatility,
/// rolled by RangeVolatilityMath: sqrt(max(0, mean v)) x 100 (x sqrt(PeriodsPerYear) when Annualize is on).
/// </summary>
public class CoreRangeVolatilityIndicatorTests
{
    private const double Tolerance = 1e-9;

    private static CoreCandleData Bar(int day, decimal open, decimal high, decimal low, decimal close) =>
        new(new DateTime(2024, 1, 1).AddDays(day), open, high, low, close, 1000);

    private static List<CoreCandleData> Same(int count, decimal open, decimal high, decimal low, decimal close) =>
        Enumerable.Range(0, count).Select(i => Bar(i, open, high, low, close)).ToList();

    private static IReadOnlyList<decimal?> Parkinson(IReadOnlyList<CoreCandleData> candles, int period, bool annualize = false, int periodsPerYear = 252) =>
        new CoreParkinsonVolatilityIndicator { Period = period, Annualize = annualize, PeriodsPerYear = periodsPerYear }.Calculate(candles).MainValues;

    private static IReadOnlyList<decimal?> RogersSatchell(IReadOnlyList<CoreCandleData> candles, int period, bool annualize = false, int periodsPerYear = 252) =>
        new CoreRogersSatchellVolatilityIndicator { Period = period, Annualize = annualize, PeriodsPerYear = periodsPerYear }.Calculate(candles).MainValues;

    private static double ParkinsonExpected(double high, double low)
    {
        double ln = Math.Log(high / low);
        return Math.Sqrt(ln * ln / (4.0 * Math.Log(2.0))) * 100.0;
    }

    // ----- Parkinson (T1) -----

    [Fact]
    public void Parkinson_FlatBars_GiveZero()
    {
        var values = Parkinson(Same(5, 100m, 100m, 100m, 100m), 3);

        Assert.Equal(new decimal?[] { null, null, 0m, 0m, 0m }, values);
    }

    [Fact]
    public void Parkinson_TwoIdenticalBars_MatchTheFormulaAndTheAnnualisedValueIsSqrt252Times()
    {
        var candles = Same(2, 100m, 110m, 100m, 105m);
        double expected = ParkinsonExpected(110.0, 100.0);

        var perBar = Parkinson(candles, 2);
        var annual = Parkinson(candles, 2, annualize: true);

        Assert.Null(perBar[0]);
        Assert.Equal(expected, (double)perBar[1]!.Value, Tolerance);
        Assert.Equal(expected * Math.Sqrt(252.0), (double)annual[1]!.Value, 1e-7);
    }

    [Fact]
    public void Parkinson_PeriodsPerYearIsIgnoredWhileAnnualizeIsOff()
    {
        var candles = Same(4, 100m, 110m, 100m, 105m);

        Assert.Equal(Parkinson(candles, 2, false, 252), Parkinson(candles, 2, false, 52));
        Assert.NotEqual(Parkinson(candles, 2, true, 252), Parkinson(candles, 2, true, 52));
    }

    [Fact]
    public void Parkinson_AnInvalidBarNullsEveryWindowContainingIt()
    {
        var candles = new List<CoreCandleData>
        {
            Bar(0, 100m, 110m, 100m, 105m),
            Bar(1, 100m, 110m, 100m, 105m),
            Bar(2, 100m, 110m, 0m, 105m),   // Low = 0: invalid
            Bar(3, 100m, 110m, 100m, 105m),
            Bar(4, 100m, 110m, 100m, 105m),
        };

        var values = Parkinson(candles, 2);

        Assert.NotNull(values[1]);
        Assert.Null(values[2]);
        Assert.Null(values[3]);
        Assert.NotNull(values[4]);
    }

    [Fact]
    public void Parkinson_EmptyInput_ReturnsAnEmptySuccessfulResult()
    {
        var result = new CoreParkinsonVolatilityIndicator().Calculate(new List<CoreCandleData>());

        Assert.True(result.IsSuccessful);
        Assert.Empty(result.MainValues);
    }

    [Fact]
    public void Parkinson_OutputLengthEqualsCandleCount_AndFewerBarsThanPeriodAreAllNull()
    {
        var values = Parkinson(Same(5, 100m, 110m, 100m, 105m), 20);

        Assert.Equal(5, values.Count);
        Assert.All(values, v => Assert.Null(v));
    }

    [Fact]
    public void Parkinson_ValueAtABarDoesNotDependOnLaterBars()
    {
        var all = Enumerable.Range(0, 30).Select(i => Bar(i, 100m, 100m + 1 + i % 5, 100m - 1 - i % 3, 100m)).ToList();

        var full = Parkinson(all, 5);
        var truncated = Parkinson(all.Take(20).ToList(), 5);

        for (int i = 0; i < truncated.Count; i++) Assert.Equal(full[i], truncated[i]);
    }

    // ----- Rogers-Satchell (T2) -----

    [Fact]
    public void RogersSatchell_FlatBars_GiveZero()
    {
        var values = RogersSatchell(Same(4, 100m, 100m, 100m, 100m), 2);

        Assert.Equal(new decimal?[] { null, 0m, 0m, 0m }, values);
    }

    [Fact]
    public void RogersSatchell_SymmetricBar_MatchesTwoLnSquared()
    {
        // O = C = 100, H = O x 1.1, L = O / 1.1: v = 2 ln(1.1)^2 per bar, so the two-bar mean is the same.
        decimal open = 100m;
        decimal high = open * 1.1m;
        decimal low = open / 1.1m;
        var candles = Same(2, open, high, low, open);
        double ln = Math.Log(1.1);
        double expected = Math.Sqrt(2.0 * ln * ln) * 100.0;

        var perBar = RogersSatchell(candles, 2);
        var annual = RogersSatchell(candles, 2, annualize: true, periodsPerYear: 52);

        Assert.Null(perBar[0]);
        Assert.Equal(expected, (double)perBar[1]!.Value, 1e-7);
        Assert.Equal(expected * Math.Sqrt(52.0), (double)annual[1]!.Value, 1e-6);
    }

    [Fact]
    public void RogersSatchell_AnyNonPositivePriceInTheWindowGivesNull()
    {
        var candles = new List<CoreCandleData>
        {
            Bar(0, 100m, 110m, 95m, 105m),
            Bar(1, 100m, 110m, 95m, 105m),
            Bar(2, 0m, 110m, 95m, 105m),   // Open = 0: invalid
            Bar(3, 100m, 110m, 95m, 105m),
            Bar(4, 100m, 110m, 95m, 105m),
        };

        var values = RogersSatchell(candles, 2);

        Assert.NotNull(values[1]);
        Assert.Null(values[2]);
        Assert.Null(values[3]);
        Assert.NotNull(values[4]);
    }

    [Fact]
    public void RogersSatchell_EmptyInput_ReturnsAnEmptySuccessfulResult()
    {
        var result = new CoreRogersSatchellVolatilityIndicator().Calculate(new List<CoreCandleData>());

        Assert.True(result.IsSuccessful);
        Assert.Empty(result.MainValues);
    }

    // ----- Shared aggregation and parameters -----

    [Fact]
    public void RangeVolatilityMath_RebuildsTheWindowSum_ForNegativeContributionsTheMeanIsClampedToZero()
    {
        var values = RangeVolatilityMath.Rolling(new[] { -1.0, -1.0, 4.0 }, new[] { true, true, true }, 2, false, 252);

        // Window [-1, -1]: mean -1, clamped to 0 -> 0. Window [-1, 4]: mean 1.5 -> sqrt(1.5) x 100.
        Assert.Null(values[0]);
        Assert.Equal(0m, values[1]);
        Assert.Equal(Math.Sqrt(1.5) * 100.0, (double)values[2]!.Value, Tolerance);
    }

    [Fact]
    public void Parameters_DefaultsAndRanges()
    {
        var p = new CoreParkinsonVolatilityParameter();

        Assert.Equal(20, p.Period);
        Assert.False(p.Annualize);
        Assert.Equal(252, p.PeriodsPerYear);
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreParkinsonVolatilityParameter { Period = 1 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreRogersSatchellVolatilityParameter { Period = 1001 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreRogersSatchellVolatilityParameter { PeriodsPerYear = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreRogersSatchellVolatilityParameter { PeriodsPerYear = 367 }.Validate());
    }

    [Fact]
    public void Registration_BothTypesAreCreatedWithTheirConventionNamedParameters_AsDisabledSubPanelVolatility()
    {
        foreach (var type in new[] { IndicatorType.ParkinsonVolatility, IndicatorType.RogersSatchellVolatility })
        {
            var indicator = IndicatorFactory.CreateStatic(type);
            var settings = DefaultCoreIndicatorSettings.GetDefault().Single(s => s.TypeEnum == type);

            Assert.NotNull(indicator);
            Assert.False(settings.IsEnabled);
            Assert.False(settings.IsOverlay);
            Assert.Equal(CoreIndicatorCategory.Volatility, settings.Category);
            Assert.IsAssignableFrom<CoreRangeVolatilityParameterBase>(settings.ParameterObject);
            Assert.Equal(IndicatorDefaultConstants.RsiColor, settings.Color);
        }
    }

    [Fact]
    public void EnumMembersAreAppendedAfterVolumeProfileLevels_WithoutMovingAnyOlderMember()
    {
        Assert.Equal((int)IndicatorType.VolumeProfileLevels + 1, (int)IndicatorType.ParkinsonVolatility);
        Assert.Equal((int)IndicatorType.ParkinsonVolatility + 1, (int)IndicatorType.RogersSatchellVolatility);
    }
}
