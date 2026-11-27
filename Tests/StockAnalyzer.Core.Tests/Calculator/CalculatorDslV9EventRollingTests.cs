#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Calculator;
using StockAnalyzer.Core.MathUtils;
using Xunit;
using static StockAnalyzer.Core.Tests.Calculator.CalculatorDslTestHelpers;

namespace StockAnalyzer.Core.Tests.Calculator;

/// <summary>
/// DSL v9 Batch 1 tests: event/history tracking primitives and O(N) monotonic-deque rolling functions.
/// </summary>
public sealed class CalculatorDslV9EventRollingTests
{
    private static ExpressionCalculator.CalculatorBatchResult RunWithArrays(
        string expression,
        IReadOnlyDictionary<string, IReadOnlyList<decimal>> arrays,
        CalculatorLimits? limits = null)
    {
        var items = new[] { new ExpressionCalculator.CalculatorBatchItem("r", expression) };
        return ExpressionCalculator.EvaluateBatch(Empty, arrays, items, limits ?? CalculatorLimits.Default)[0];
    }

    [Fact]
    public void ArrArgwhere_ReturnsSortedZeroBasedIndicesAndHandlesEmpty()
    {
        Assert.Equal(new[] { 1m, 3m, 4m }, Arr("arr_argwhere([0, 1, 0, -5, 0.25, -0.0])"));
        Assert.Empty(Arr("arr_argwhere([])"));
        Assert.Empty(Arr("arr_argwhere([0, -0.0, 0])"));
        Assert.Equal(new[] { 0m }, Arr("arr_argwhere([7])"));
        Assert.Equal(3m, Scalar("arr_len(arr_argwhere([0, 1, 0, 1, 1]))"));

        Fails("arr_argwhere(1)", CalculatorErrorCode.DomainError);
        Fails("arr_argwhere([1 / 0])", CalculatorErrorCode.DivisionByZero);
    }

    [Fact]
    public void ArrBarsSince_TracksCausalElapsedBarsAndNormalizesZero()
    {
        Assert.Equal(new[] { -1m, 0m, 1m, 2m, 0m, 1m }, Arr("arr_bars_since([0, 1, 0, 0, 1, 0])"));
        Assert.Equal(new[] { 99m, 0m, 1m }, Arr("arr_bars_since([0, 1, 0], 99)"));
        Assert.Equal(new[] { 0m }, Arr("arr_bars_since([0], -0.0)"));
        AssertNoNegativeZero(Arr("arr_bars_since([0], -0.0)"), "arr_bars_since");
        Assert.Equal(new[] { 0m }, Arr("arr_bars_since([5])"));
        Assert.Empty(Arr("arr_bars_since([])"));

        Fails("arr_bars_since(5)", CalculatorErrorCode.DomainError);
        Fails("arr_bars_since([1], [0])", CalculatorErrorCode.DomainError);
        Fails("arr_bars_since([1], 1 / 0)", CalculatorErrorCode.DivisionByZero);
    }

    [Fact]
    public void ArrValueWhen_TracksOccurrencesAndPoolBoundaryAtOccurrence64()
    {
        Assert.Equal(new[] { 0m, 10m, 10m, 20m, 20m, 30m }, Arr("arr_value_when([0, 1, 0, 1, 0, 1], [5, 10, 15, 20, 25, 30], 0)"));
        Assert.Equal(new[] { -9m, -9m, -9m, 10m, 10m, 20m }, Arr("arr_value_when([0, 1, 0, 1, 0, 1], [5, 10, 15, 20, 25, 30], 1, -9)"));
        Assert.Equal(new[] { 0m }, Arr("arr_value_when([1], [-0.0], 0, -0.0)"));
        AssertNoNegativeZero(Arr("arr_value_when([1], [-0.0], 0, -0.0)"), "arr_value_when");
        Assert.Empty(Arr("arr_value_when([], [], 0)"));

        // Occurrence = 64 exercises the ArrayPool<decimal> branch (ringSize = 65 > DecimalStackAllocThreshold = 64).
        decimal[] cond = Enumerable.Repeat(1m, 80).ToArray();
        decimal[] src = Enumerable.Range(1, 80).Select(static i => (decimal)i).ToArray();
        var arrays = new Dictionary<string, IReadOnlyList<decimal>>(StringComparer.Ordinal)
        {
            ["c"] = cond,
            ["s"] = src,
        };
        var res64 = RunWithArrays("arr_value_when(c, s, 64, -1)", arrays);
        Assert.True(res64.IsOk, res64.Error?.Message);
        Assert.Equal(-1m, res64.ArrayValues![63]);
        Assert.Equal(1m, res64.ArrayValues[64]);
        Assert.Equal(16m, res64.ArrayValues[79]);

        Fails("arr_value_when([1], [1, 2], 0)", CalculatorErrorCode.DimensionMismatch);
        Fails("arr_value_when([1], [1], -1)", CalculatorErrorCode.DomainError);
        Fails("arr_value_when([1], [1], 65)", CalculatorErrorCode.DomainError);
        Fails("arr_value_when([1], [1], 0.5)", CalculatorErrorCode.DomainError);
        Fails("arr_value_when([], [], 65)", CalculatorErrorCode.DomainError);
        Fails("arr_value_when(1, [1], 0)", CalculatorErrorCode.DomainError);
    }

    [Fact]
    public void ArrFfill_ForwardFillsValidPositionsWithoutZeroMaskLeak()
    {
        Assert.Equal(new[] { -1m, 100m, 100m, 0m, 0m, 200m }, Arr("arr_ffill([99, 100, 99, -0.0, 99, 200], [0, 1, 0, 1, 0, 1], -1)"));
        AssertNoNegativeZero(Arr("arr_ffill([-0.0], [0], -0.0)"), "arr_ffill");
        Assert.Empty(Arr("arr_ffill([], [])"));

        Fails("arr_ffill([1, 2], [1])", CalculatorErrorCode.DimensionMismatch);
        Fails("arr_ffill([1], [1], [0])", CalculatorErrorCode.DomainError);
    }

    [Fact]
    public void CrossOverAndCrossUnder_DetectStrictCrossingsCausally()
    {
        Assert.Equal(new[] { 0m, 0m, 1m, 0m, 0m, 1m }, Arr("cross_over([10, 10, 11, 12, 10, 12], [10, 10, 10, 10, 11, 11])"));
        Assert.Equal(new[] { 0m, 0m, 0m, 1m, 0m, 1m }, Arr("cross_under([10, 10, 12, 9, 10, 9], [10, 10, 10, 10, 10, 10])"));
        Assert.Equal(new[] { 0m }, Arr("cross_over([5], [3])"));
        Assert.Equal(new[] { 0m }, Arr("cross_under([3], [5])"));
        Assert.Empty(Arr("cross_over([], [])"));
        Assert.Empty(Arr("cross_under([], [])"));

        Fails("cross_over([1, 2], [1])", CalculatorErrorCode.DimensionMismatch);
        Fails("cross_under(1, [1])", CalculatorErrorCode.DomainError);
    }

    [Fact]
    public void RollingExtreme_MatchesBruteForceOracleAndMostRecentTieBreak()
    {
        decimal[] data = { 10m, 20m, 20m, 15m, 12m, 25m, 25m, 18m, 18m, 22m };
        var arrays = new Dictionary<string, IReadOnlyList<decimal>>(StringComparer.Ordinal) { ["a"] = data };

        for (int w = 1; w <= data.Length + 2; w++)
        {
            var maxRes = RunWithArrays($"arr_rolling_max(a, {w}, -999)", arrays);
            var minRes = RunWithArrays($"arr_rolling_min(a, {w}, -999)", arrays);
            var bsMaxRes = RunWithArrays($"arr_rolling_bars_since_max(a, {w}, -1)", arrays);
            var bsMinRes = RunWithArrays($"arr_rolling_bars_since_min(a, {w}, -1)", arrays);

            Assert.True(maxRes.IsOk, maxRes.Error?.Message);
            Assert.True(minRes.IsOk, minRes.Error?.Message);
            Assert.True(bsMaxRes.IsOk, bsMaxRes.Error?.Message);
            Assert.True(bsMinRes.IsOk, bsMinRes.Error?.Message);

            for (int t = 0; t < data.Length; t++)
            {
                if (t < w - 1)
                {
                    Assert.Equal(-999m, maxRes.ArrayValues![t]);
                    Assert.Equal(-999m, minRes.ArrayValues![t]);
                    Assert.Equal(-1m, bsMaxRes.ArrayValues![t]);
                    Assert.Equal(-1m, bsMinRes.ArrayValues![t]);
                    continue;
                }

                decimal oracleMax = decimal.MinValue;
                decimal oracleMin = decimal.MaxValue;
                int oracleMaxIdx = t;
                int oracleMinIdx = t;
                for (int j = t; j >= t - w + 1; j--)
                {
                    if (data[j] > oracleMax)
                    {
                        oracleMax = data[j];
                        oracleMaxIdx = j;
                    }

                    if (data[j] < oracleMin)
                    {
                        oracleMin = data[j];
                        oracleMinIdx = j;
                    }
                }

                Assert.Equal(oracleMax, maxRes.ArrayValues![t]);
                Assert.Equal(oracleMin, minRes.ArrayValues![t]);
                Assert.Equal(t - oracleMaxIdx, bsMaxRes.ArrayValues![t]);
                Assert.Equal(t - oracleMinIdx, bsMinRes.ArrayValues![t]);
            }
        }

        // NR7 one-liner verification: narrowest range at bar t gives bars_since_min == 0
        Assert.Equal(new[] { -1m, -1m, 0m, 1m, 0m }, Arr("arr_rolling_bars_since_min([10, 8, 5, 6, 3], 3, -1)"));
        Assert.Empty(Arr("arr_rolling_max([], 5, -1)"));
        Assert.Equal(new[] { -1m, -1m }, Arr("arr_rolling_min([5, 6], 5, -1)"));
        Assert.Equal(new[] { 0m, 0m }, Arr("arr_rolling_max([-0.0, -0.0], 1, -0.0)"));
        AssertNoNegativeZero(Arr("arr_rolling_max([-0.0, -0.0], 1, -0.0)"), "arr_rolling_max");

        // Composition with single-array lowered variadics via ReturnsArray: [0, 20, 30, 40] -> sum 90
        Assert.Equal(90m, Scalar("sum(arr_rolling_max([10, 20, 30, 40], 2, 0))"));

        Fails("arr_rolling_max([1, 2], 0, 0)", CalculatorErrorCode.DomainError);
        Fails("arr_rolling_max([1, 2], 513, 0)", CalculatorErrorCode.DomainError);
        Fails("arr_rolling_min([1, 2], 1.5, 0)", CalculatorErrorCode.DomainError);
        Fails("arr_rolling_bars_since_max(1, 2, -1)", CalculatorErrorCode.DomainError);
        Fails("arr_rolling_bars_since_min([1], 1, [0])", CalculatorErrorCode.DomainError);
    }

    [Fact]
    public void Batch1CausalFunctions_EnforceStrictPrefixInvarianceAndBudget()
    {
        decimal[] full = Enumerable.Range(1, 30).Select(i => (decimal)((i * 7) % 13)).ToArray();
        decimal[] prefix = full.Take(18).ToArray();

        string[] exprs =
        {
            "arr_bars_since(arr_where(x, arr_ones(arr_len(x)), arr_zeros(arr_len(x))), -1)",
            "arr_value_when(arr_where(x, arr_ones(arr_len(x)), arr_zeros(arr_len(x))), x, 1, -1)",
            "arr_ffill(x, arr_where(x, arr_ones(arr_len(x)), arr_zeros(arr_len(x))), -1)",
            "cross_over(x, arr_fill(arr_len(x), 5))",
            "cross_under(x, arr_fill(arr_len(x), 5))",
            "arr_rolling_max(x, 5, -1)",
            "arr_rolling_min(x, 5, -1)",
            "arr_rolling_bars_since_max(x, 5, -1)",
            "arr_rolling_bars_since_min(x, 5, -1)",
        };

        var fullMap = new Dictionary<string, IReadOnlyList<decimal>>(StringComparer.Ordinal) { ["x"] = full };
        var prefixMap = new Dictionary<string, IReadOnlyList<decimal>>(StringComparer.Ordinal) { ["x"] = prefix };

        foreach (string expr in exprs)
        {
            var fullRes = RunWithArrays(expr, fullMap);
            var prefixRes = RunWithArrays(expr, prefixMap);
            Assert.True(fullRes.IsOk, $"{expr}: {fullRes.Error?.Message}");
            Assert.True(prefixRes.IsOk, $"{expr}: {prefixRes.Error?.Message}");
            Assert.Equal(prefixRes.ArrayValues!, fullRes.ArrayValues!.Take(prefix.Length));
        }

        // Budget exhaustion verification
        var tinyBudget = new CalculatorLimits { MaxExecutionOps = 5 };
        var arr512 = new Dictionary<string, IReadOnlyList<decimal>>(StringComparer.Ordinal)
        {
            ["x"] = Enumerable.Repeat(1m, 512).ToArray(),
        };
        var budgetRes = RunWithArrays("arr_rolling_max(x, 20, -1)", arr512, tinyBudget);
        Assert.False(budgetRes.IsOk);
        Assert.Equal(CalculatorErrorCode.BudgetExceeded, budgetRes.Error!.Code);
    }

    [Fact]
    public void ArrRollingCorr_MatchesCorrPearsonOracleAndFlatWindowSentinel()
    {
        decimal[] x = { 1m, 2m, 3m, 5m, 5m, 5m, 5m, 4m, 2m, 1m };
        decimal[] y = { 2m, 4m, 6m, 9m, 8m, 7m, 6m, 8m, 4m, 2m };
        var arrays = new Dictionary<string, IReadOnlyList<decimal>>(StringComparer.Ordinal)
        {
            ["x"] = x,
            ["y"] = y,
        };

        var res = RunWithArrays("arr_rolling_corr(x, y, 4, -2)", arrays);
        Assert.True(res.IsOk, res.Error?.Message);
        Assert.Equal(x.Length, res.ArrayValues!.Count);
        Assert.Equal(-2m, res.ArrayValues[0]);
        Assert.Equal(-2m, res.ArrayValues[1]);
        Assert.Equal(-2m, res.ArrayValues[2]);

        // Window [3..6] has constant x = [5, 5, 5, 5] -> flat window returns sentinel -2 without failing expression
        Assert.Equal(-2m, res.ArrayValues[6]);
        AssertNoNegativeZero(res.ArrayValues, "arr_rolling_corr");

        // Oracle parity against scalar corr_pearson on non-flat windows
        for (int t = 3; t < x.Length; t++)
        {
            if (t == 6)
            {
                continue;
            }

            string xs = string.Join(", ", x.Skip(t - 3).Take(4));
            string ys = string.Join(", ", y.Skip(t - 3).Take(4));
            decimal oracle = Scalar($"corr_pearson([{xs}], [{ys}])");
            Assert.Equal(oracle, res.ArrayValues[t]);
        }

        // High-magnitude flat window (1_000_000.01m constant) triggers FlatWindowRule.Series instead of M2 <= 1e-30 false spread
        Assert.Equal(
            new[] { -2m, -2m, -2m, -2m },
            Arr("arr_rolling_corr([1000000.01, 1000000.01, 1000000.01, 1000000.01], [1, 2, 3, 4], 3, -2)"));

        // MP-1: translation invariance preserved even at 1e16m offset where direct double cast collides
        Assert.Equal(
            new[] { -2m, -2m, 1m, 1m },
            Arr("arr_rolling_corr([1e16, 1e16 + 1, 1e16 + 2, 1e16 + 3], [1, 2, 3, 4], 3, -2)"));

        // MP-2: RollingExtreme ArrayPool path beyond StackAllocThreshold (n = 600 > 512)
        decimal[] rollingLarge = new decimal[600];
        rollingLarge[300] = 77m;
        decimal[] rollingOut = new decimal[600];
        DslEventAndRollingMath.RollingExtreme(rollingLarge, 3, -1m, isMax: true, returnBarsSince: false, rollingOut);
        Assert.Equal(77m, rollingOut[302]);

        // N < w unified warmup rule + N == 0
        Assert.Equal(new[] { -2m, -2m }, Arr("arr_rolling_corr([1, 2], [3, 4], 5, -2)"));
        Assert.Empty(Arr("arr_rolling_corr([], [], 2, -2)"));

        // ArrayPool<double> path (N = 300 > 256 so 2 * N = 600 > StackAllocThreshold = 512)
        decimal[] xLarge = Enumerable.Range(1, 300).Select(static i => (decimal)i).ToArray();
        decimal[] yLarge = Enumerable.Range(1, 300).Select(static i => (decimal)(i * 2)).ToArray();
        var largeMap = new Dictionary<string, IReadOnlyList<decimal>>(StringComparer.Ordinal) { ["x"] = xLarge, ["y"] = yLarge };
        var largeRes = RunWithArrays("arr_rolling_corr(x, y, 20, -2)", largeMap);
        Assert.True(largeRes.IsOk, largeRes.Error?.Message);
        Assert.Equal(1m, largeRes.ArrayValues![^1]);

        Fails("arr_rolling_corr([1, 2], [1], 2, -2)", CalculatorErrorCode.DimensionMismatch);
        Fails("arr_rolling_corr([1, 2], [1, 2], 1, -2)", CalculatorErrorCode.DomainError);
        Fails("arr_rolling_corr([1, 2], [1, 2], 513, -2)", CalculatorErrorCode.DomainError);
        Fails("arr_rolling_corr(1, [1, 2], 2, -2)", CalculatorErrorCode.DomainError);
    }

    [Fact]
    public void ArrZipRollingMap_BindsMultipleZeroCopyWindowsAndEnforcesScopeDepth()
    {
        // Multi-array rolling calculation: sum(h - l) / sum(v)
        decimal[] res = Arr("arr_zip_rolling_map(sum(arr_zip_sub(wh, wl)) / sum(wv), [wh, wl, wv], [[10, 12, 15, 14], [8, 9, 10, 11], [1, 1, 2, 2]], 2)");
        Assert.Equal(new[] { 2.5m, (3m + 5m) / 3m, (5m + 3m) / 4m }, res);

        // Lexical shadowing of outer scalar variable with the same name (a=999 shadowed by window array a)
        Assert.Equal(
            12m,
            Scalar("arr_reduce(arr_zip_rolling_map(sum(a) + sum(b), [a, b], [[1, 2, 3], [4, 5, 6]], 2)[0], a, x, [1], 999)"));
        Assert.Equal(
            16m,
            Scalar("arr_reduce(arr_zip_rolling_map(sum(a) + sum(b), [a, b], [[1, 2, 3], [4, 5, 6]], 2)[1], a, x, [1], 999)"));

        // BoundArrayScope depth = 8 overflow check (nested 4 + 5 = 9 > 8)
        Fails(
            "arr_zip_rolling_map(arr_zip_rolling_map(sum(x1), [x1, x2, x3, x4, x5], [a1, a2, a3, a4, a1], 1)[0], [a1, a2, a3, a4], [[1, 2], [1, 2], [1, 2], [1, 2]], 2)",
            CalculatorErrorCode.BudgetExceeded);

        Fails("arr_zip_rolling_map(sum(a), [a], [[1, 2], [3, 4]], 1)", CalculatorErrorCode.DimensionMismatch);
        Fails("arr_zip_rolling_map(sum(a), [a, a], [[1, 2], [3, 4]], 1)", CalculatorErrorCode.DomainError);
        Fails("arr_zip_rolling_map(sum(a), [a, b], [[1, 2], [3]], 1)", CalculatorErrorCode.DimensionMismatch);
        Fails("arr_zip_rolling_map(sum(a), [a], [[1, 2]], 3)", CalculatorErrorCode.DomainError);
        Fails("arr_zip_rolling_map(sum(a), [a], [[]], 1)", CalculatorErrorCode.DomainError);

        // MP-3: 5th optional fill argument preserves length N for seamless composition with v9 array primitives
        Assert.Equal(new[] { -999m, -999m, 6m, 9m, 12m }, Arr("arr_zip_rolling_map(sum(x), [x], [[1, 2, 3, 4, 5]], 3, -999)"));
        Assert.Equal(new[] { -1m, -1m }, Arr("arr_zip_rolling_map(sum(x), [x], [[1, 2]], 5, -1)"));
        Assert.Empty(Arr("arr_zip_rolling_map(sum(x), [x], [[]], 3, -1)"));
        Assert.Equal(
            new[] { 0m, 0m, 1m, 0m },
            Arr("cross_over([10, 2, 10, 5], arr_zip_rolling_map(sum(x) / 2, [x], [[10, 2, 10, 5]], 2, 0))"));
        Fails("arr_zip_rolling_map(sum(x), [x], [[1, 2]], 1, [0])", CalculatorErrorCode.DomainError);
        Fails("arr_zip_rolling_map(sum(x), [x], [[1, 2]], 0, -1)", CalculatorErrorCode.DomainError);
        Fails("arr_zip_rolling_map(sum(x), [x], [[1, 2]], 513, -1)", CalculatorErrorCode.DomainError);
    }
}
