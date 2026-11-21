#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using StockAnalyzer.Core.Calculator;
using StockAnalyzer.Core.MathUtils;
using Xunit;

namespace StockAnalyzer.Core.Tests.Calculator;

/// <summary>
/// DSL v5 review-adoption batch B, T2: the statistics (<c>corr_pearson</c>, <c>corr_spearman</c>, <c>ccf</c>, <c>ttest_1samp</c>, <c>ttest_2samp</c>,
/// <c>jb_test</c>, <c>ljung_box</c>) share one flat-series rule, <see cref="FlatWindowRule.Series"/> (population deviation below
/// <c>max(1e-10, 1e-12 * |mean|)</c>); <c>ljung_box</c> additionally keeps the delegated kernel's population-variance floor
/// (<see cref="ReturnStatistics.VarianceEpsilon"/>). Owner decisions Q-1a = B, Q-1b = ii, Q-1c (2026-11-21).
/// </summary>
public sealed class CalculatorDslV5BatchBTests
{
    private static readonly IReadOnlyDictionary<string, decimal> Empty =
        new Dictionary<string, decimal>(StringComparer.Ordinal);

    private static ExpressionCalculator.CalculatorBatchResult Run(string expression)
        => ExpressionCalculator.EvaluateBatch(
            Empty,
            null,
            new[] { new ExpressionCalculator.CalculatorBatchItem("r", expression) },
            CalculatorLimits.Default)[0];

    private static decimal Scalar(string expression)
    {
        ExpressionCalculator.CalculatorBatchResult result = Run(expression);
        Assert.True(result.IsOk, $"{expression} => {result.Error?.Code}: {result.Error?.Message}");
        Assert.False(result.IsArray);
        return result.ScalarValue;
    }

    private static decimal[] Arr(string expression)
    {
        ExpressionCalculator.CalculatorBatchResult result = Run(expression);
        Assert.True(result.IsOk, $"{expression} => {result.Error?.Code}: {result.Error?.Message}");
        Assert.True(result.IsArray);
        return result.ArrayValues!.ToArray();
    }

    private static void Fails(string expression, CalculatorErrorCode expected)
    {
        ExpressionCalculator.CalculatorBatchResult result = Run(expression);
        Assert.False(result.IsOk, $"{expression} unexpectedly succeeded.");
        Assert.Equal(expected, result.Error!.Code);
    }

    private static string Vector(IEnumerable<decimal> values)
        => "[" + string.Join(", ", values.Select(v => v.ToString(CultureInfo.InvariantCulture))) + "]";

    // ---------------------------------------------------------------- one rule for every statistic

    [Fact]
    public void SmallButRealSpreadIsNotFlatForAnyStatistic()
    {
        // Population deviations of 1e-8..1e-7 are above FlatWindowRule.Series (1e-10) although the old variance floor (1e-12, i.e. deviation 1e-6) rejected them.
        const string Tiny = "[0, 0.00000001, 0.00000002, 0.00000003]";
        Assert.Equal(1.0, (double)Scalar($"corr_pearson({Tiny}, [1, 2, 3, 4])"), 12);
        Assert.Equal(1.0, (double)Scalar($"ccf({Tiny}, [1, 2, 3, 4], 0)"), 12);
        Assert.Equal(1.0, (double)Scalar($"corr_pearson([1, 2, 3, 4], {Tiny})"), 12);

        decimal[] t = Arr($"ttest_1samp({Tiny}, 0)");
        Assert.Equal(3, t.Length);
        Assert.Equal(3.0, (double)t[2], 12);
    }

    [Fact]
    public void RelativePartOfTheRuleTreatsRoundingNoiseOfALargeConstantAsFlat()
    {
        // Mean 1e9, deviation 2.5e-4 < 1e-12 * 1e9 = 1e-3: noise around a constant, not a spread.
        const string Large = "[1000000000, 1000000000.0005, 1000000000, 1000000000.0005]";
        Fails($"corr_pearson({Large}, [1, 2, 3, 4])", CalculatorErrorCode.DivisionByZero);
        Fails($"corr_pearson([1, 2, 3, 4], {Large})", CalculatorErrorCode.DivisionByZero);
        Fails($"ccf({Large}, [1, 2, 3, 4], 1)", CalculatorErrorCode.DivisionByZero);
        Fails($"ttest_1samp({Large}, 0)", CalculatorErrorCode.DivisionByZero);
    }

    [Fact]
    public void DeviationJustBelowTheAbsoluteLimitIsFlatAndJustAboveIsNot()
    {
        // [0, 2e-11, 4e-11]: population deviation 1.63e-11 < 1e-10.
        Fails("ttest_1samp([0, 0.00000000002, 0.00000000004], 0)", CalculatorErrorCode.DivisionByZero);

        // [0, 2e-10, 4e-10]: population deviation 1.63e-10 >= 1e-10.
        decimal[] t = Arr("ttest_1samp([0, 0.0000000002, 0.0000000004], 0)");
        Assert.Equal(2.0, (double)t[2], 12);
    }

    [Fact]
    public void SpearmanAppliesTheRuleToRanksSoOnlyAnAllEqualSeriesIsFlat()
    {
        // A strictly increasing series has rank deviations of at least ~0.5 whatever its magnitude.
        Assert.Equal(1.0, (double)Scalar("corr_spearman([0, 0.000000000000001, 0.000000000000002], [1, 2, 3])"), 12);
        Fails("corr_spearman([2, 2, 2], [1, 2, 3])", CalculatorErrorCode.DivisionByZero);
        Fails("corr_spearman([1, 2, 3], [7, 7, 7])", CalculatorErrorCode.DivisionByZero);
    }

    [Fact]
    public void EitherSeriesBeingFlatIsADivisionByZeroForTheTwoSeriesFunctions()
    {
        Fails("corr_pearson([1, 2, 3], [4, 4, 4])", CalculatorErrorCode.DivisionByZero);
        Fails("corr_pearson([4, 4, 4], [1, 2, 3])", CalculatorErrorCode.DivisionByZero);
        Fails("ccf([1, 2, 3], [4, 4, 4], 0)", CalculatorErrorCode.DivisionByZero);
        Fails("ccf([4, 4, 4], [1, 2, 3], 1)", CalculatorErrorCode.DivisionByZero);
    }

    // ---------------------------------------------------------------- Welch: flat sides

    [Fact]
    public void WelchOnlyFailsWhenBothSamplesAreFlat()
    {
        Fails("ttest_2samp([5, 5, 5], [7, 7, 7])", CalculatorErrorCode.DivisionByZero);
        Fails("ttest_2samp([0, 0.00000000002, 0.00000000004], [1, 1.00000000002, 1.00000000004])", CalculatorErrorCode.DivisionByZero);
    }

    [Fact]
    public void WelchWithOneFlatSampleUsesAnExactZeroVarianceTermForThatSide()
    {
        // x flat: se = sqrt(vy), df = ny - 1. y = [1, 2, 3, 4]: sample variance 5/3, vy = 5/3/4.
        double vy = 5.0 / 3.0 / 4.0;
        decimal[] xFlat = Arr("ttest_2samp([5, 5, 5], [1, 2, 3, 4])");
        Assert.Equal((5.0 - 2.5) / Math.Sqrt(vy), (double)xFlat[0], 10);
        Assert.Equal(3.0, (double)xFlat[2], 10);

        // y flat: symmetric. x = [1, 2, 3, 4], y = [5, 5, 5, 5, 5]: df = nx - 1.
        decimal[] yFlat = Arr("ttest_2samp([1, 2, 3, 4], [5, 5, 5, 5, 5])");
        Assert.Equal((2.5 - 5.0) / Math.Sqrt(vy), (double)yFlat[0], 10);
        Assert.Equal(3.0, (double)yFlat[2], 10);

        // A flat sample whose deviation is rounding noise must not leak into the standard error.
        decimal[] noisy = Arr("ttest_2samp([1000000000, 1000000000.0005, 1000000000, 1000000000.0005], [1, 2, 3, 4])");
        decimal[] exact = Arr("ttest_2samp([1000000000, 1000000000, 1000000000, 1000000000], [1, 2, 3, 4])");
        Assert.Equal((double)exact[2], (double)noisy[2], 12);
        Assert.Equal(3.0, (double)noisy[2], 12);
    }

    // ---------------------------------------------------------------- jb_test and ljung_box

    [Fact]
    public void JarqueBeraHasNoExtraVarianceFloorBeyondTheSharedRule()
    {
        decimal[] unit = [1m, 2m, 3m, 4m, 5m, 7m, 9m, 12m];
        decimal scale = 0.00000001m;
        decimal[] scaled = unit.Select(v => v * scale).ToArray();

        // Population variance of the scaled series is about 1e-15 (< the old 1e-12 floor) but its deviation 3e-8 is far above 1e-10.
        decimal[] reference = Arr($"jb_test({Vector(unit)})");
        decimal[] result = Arr($"jb_test({Vector(scaled)})");
        Assert.Equal((double)reference[0], (double)result[0], 8);
        Assert.Equal((double)reference[1], (double)result[1], 8);

        Fails("jb_test([5, 5, 5, 5, 5, 5, 5, 5])", CalculatorErrorCode.DivisionByZero);
    }

    [Fact]
    public void LjungBoxKeepsTheDelegatedKernelFloorOnThePopulationVariance()
    {
        decimal[] unit = [1m, 3m, 2m, 5m, 4m, 6m, 5m, 8m];

        // Population variance 3.6e-13: above ReturnStatistics.VarianceEpsilon (1e-14) and below the old 1e-12 floor -> now computed, scale-invariant.
        decimal[] above = unit.Select(v => v * 0.0000003m).ToArray();
        decimal[] reference = Arr($"ljung_box({Vector(unit)}, 2)");
        decimal[] result = Arr($"ljung_box({Vector(above)}, 2)");
        Assert.Equal((double)reference[0], (double)result[0], 8);
        Assert.Equal((double)reference[1], (double)result[1], 8);

        // Population variance 4e-16 (deviation 2e-8, not flat under the shared rule) is at or below the kernel floor 1e-14 -> DivisionByZero, not NonFiniteResult.
        decimal[] below = unit.Select(v => v * 0.00000001m).ToArray();
        Fails($"ljung_box({Vector(below)}, 2)", CalculatorErrorCode.DivisionByZero);

        Fails("ljung_box([2, 2, 2, 2], 1)", CalculatorErrorCode.DivisionByZero);
        Fails("ljung_box([1], 1)", CalculatorErrorCode.DomainError);
    }

    [Fact]
    public void RemovedVarianceFloorConstantsAreNoLongerPartOfTheKernel()
    {
        System.Reflection.FieldInfo[] fields = typeof(DslBivariateAndTestMath).GetFields();
        Assert.DoesNotContain(fields, f => f.Name is "VarianceFloor" or "WelchVarianceFloor");
    }
}
