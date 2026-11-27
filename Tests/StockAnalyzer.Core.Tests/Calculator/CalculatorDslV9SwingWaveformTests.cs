#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Calculator;
using Xunit;
using static StockAnalyzer.Core.Tests.Calculator.CalculatorDslTestHelpers;

namespace StockAnalyzer.Core.Tests.Calculator;

/// <summary>
/// DSL v9 Batch 3 tests: causal pivot/zigzag detection and geometric waveform simplification (RDP, PIP).
/// </summary>
public sealed class CalculatorDslV9SwingWaveformTests
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
    public void PivotHighAndLowCausal_ConfirmRightmostPlateauBarAndPreventRepaint()
    {
        // Tweezer top [20, 20] at indices 1,2 with left=1, right=1 -> confirms rightmost bar 2 at bar t=3.
        decimal[] highs = Arr("pivot_high_causal([10, 20, 20, 15, 10], 1, 1, -1)");
        Assert.Equal(new[] { -1m, -1m, -1m, 20m, -1m }, highs);

        // Tweezer bottom [5, 5] at indices 1,2 -> confirms rightmost bar 2 at bar t=3.
        decimal[] lows = Arr("pivot_low_causal([10, 5, 5, 8, 12], 1, 1, -1)");
        Assert.Equal(new[] { -1m, -1m, -1m, 5m, -1m }, lows);

        // Multi-bar plateau [20, 20, 20] & within-window double top [20, 15, 20] confirm rightmost bar 3 at t=5
        Assert.Equal(new[] { -1m, -1m, -1m, -1m, -1m, 20m }, Arr("pivot_high_causal([10, 20, 20, 20, 10, 10], 1, 2, -1)"));
        Assert.Equal(new[] { -1m, -1m, -1m, -1m, -1m, 20m }, Arr("pivot_high_causal([10, 20, 15, 20, 15, 10], 1, 2, -1)"));
        Assert.Equal(new[] { -1m, -1m, -1m, -1m, -1m, 10m }, Arr("pivot_low_causal([20, 10, 10, 10, 20, 20], 1, 2, -1)"));

        // Short series (N < left + right + 1) returns fill-filled length-N array
        Assert.Equal(new[] { -1m, -1m }, Arr("pivot_high_causal([10, 20], 1, 1, -1)"));
        Assert.Empty(Arr("pivot_high_causal([], 1, 1, -1)"));
        AssertNoNegativeZero(Arr("pivot_high_causal([1, -0.0, -5], 1, 1, -0.0)"), "pivot_high_causal");

        // Strict causal prefix invariance across every prefix k
        decimal[] series = { 10m, 14m, 12m, 18m, 15m, 11m, 16m, 19m, 17m, 12m };
        var fullMap = new Dictionary<string, IReadOnlyList<decimal>>(StringComparer.Ordinal) { ["x"] = series };
        decimal[] fullOut = RunWithArrays("pivot_high_causal(x, 2, 2, -1)", fullMap).ArrayValues!.ToArray();
        for (int k = 1; k <= series.Length; k++)
        {
            var prefMap = new Dictionary<string, IReadOnlyList<decimal>>(StringComparer.Ordinal) { ["x"] = series.Take(k).ToArray() };
            decimal[] prefOut = RunWithArrays("pivot_high_causal(x, 2, 2, -1)", prefMap).ArrayValues!.ToArray();
            Assert.Equal(fullOut.Take(k), prefOut);
        }

        // MP-2: kernel ArrayPool path beyond StackAllocThreshold (n = 600 > 512)
        decimal[] longSeries = new decimal[600];
        longSeries[200] = 50m;
        decimal[] longOut = new decimal[600];
        StockAnalyzer.Core.MathUtils.DslSwingAndWaveformMath.PivotCausal(longSeries, 2, 2, -1m, isHigh: true, longOut);
        Assert.Equal(50m, longOut[202]);

        Fails("pivot_high_causal([1, 2, 3], 0, 1)", CalculatorErrorCode.DomainError);
        Fails("pivot_low_causal([1, 2, 3], 1, 256)", CalculatorErrorCode.DomainError);
    }

    [Fact]
    public void ZigzagCausal_PercentAndAtrModesEnforceCausalityAndSameBarProhibition()
    {
        // Percent mode (mode=0), threshold=10%, outputKind=0 (price), 1 (sign), 2 (origIdx)
        // Bar 0: H=100, L=100
        // Bar 1: H=112 (+12% >= 10% from initLow=100 at idx 0 < 1) -> confirms low 100 at t=1!
        // Bar 2: H=120 (extends peak to 120 at idx 2)
        // Bar 3: L=105 ((120-105)/120 = 12.5% >= 10%) -> confirms high 120 at t=3!
        decimal[] prices = Arr("zigzag_causal([100, 112, 120, 105], [100, 100, 115, 105], 10, 0, 0, [], -1)");
        decimal[] signs = Arr("zigzag_causal([100, 112, 120, 105], [100, 100, 115, 105], 10, 0, 1)");
        decimal[] origIdx = Arr("zigzag_causal([100, 112, 120, 105], [100, 100, 115, 105], 10, 0, 2)");

        Assert.Equal(new[] { -1m, 100m, -1m, 120m }, prices);
        Assert.Equal(new[] { 0m, -1m, 0m, 1m }, signs);
        Assert.Equal(new[] { -1m, 0m, -1m, 2m }, origIdx);

        // D-08 check: single wide outside bar at t=1 (H=130, L=80) extends BOTH initHigh and initLow on bar 1,
        // so initLowIdx == 1 and initHighIdx == 1 -> NO same-bar reversal at t=1.
        decimal[] outside = Arr("zigzag_causal([100, 130, 125], [100, 80, 105], 10, 0, 0)");
        Assert.Equal(new[] { 0m, 0m, 80m }, outside);

        // ATR mode (mode=1): reversal blocked when atr == 0, triggered when move >= threshold * atr
        decimal[] atrOut = Arr("zigzag_causal([100, 110, 115, 108], [100, 100, 110, 100], 2, 1, 0, [5, 0, 5, 5], -1)");
        Assert.Equal(new[] { -1m, -1m, 100m, 115m }, atrOut);

        // Prefix invariance on zigzag_causal
        decimal[] hSeq = { 100m, 106m, 112m, 108m, 99m, 95m, 104m, 111m };
        decimal[] lSeq = { 98m, 102m, 107m, 98m, 94m, 90m, 96m, 105m };
        var fullMap = new Dictionary<string, IReadOnlyList<decimal>>(StringComparer.Ordinal) { ["h"] = hSeq, ["l"] = lSeq };
        decimal[] zzFull = RunWithArrays("zigzag_causal(h, l, 8, 0, 0)", fullMap).ArrayValues!.ToArray();
        for (int k = 1; k <= hSeq.Length; k++)
        {
            var prefMap = new Dictionary<string, IReadOnlyList<decimal>>(StringComparer.Ordinal)
            {
                ["h"] = hSeq.Take(k).ToArray(),
                ["l"] = lSeq.Take(k).ToArray(),
            };
            Assert.Equal(zzFull.Take(k), RunWithArrays("zigzag_causal(h, l, 8, 0, 0)", prefMap).ArrayValues!);
        }

        // Error checks
        Fails("zigzag_causal([100, 110], [100, 100], 5, 1)", CalculatorErrorCode.DomainError); // missing atrSeries in mode=1
        Fails("zigzag_causal([100, 110], [100, 100], 5, 1, 0, [1])", CalculatorErrorCode.DimensionMismatch);
        Fails("zigzag_causal([100, 110], [100, 100], 5, 0, 0, [1])", CalculatorErrorCode.DimensionMismatch);
        Fails("zigzag_causal([100, 90], [100, 95], 5, 0)", CalculatorErrorCode.DomainError); // high < low
        Fails("zigzag_causal([100, 110], [0, 100], 5, 0)", CalculatorErrorCode.DomainError); // low <= 0 in percent mode
        Fails("zigzag_causal([100, 110], [90, 100], 2, 1, 0, [1, -1])", CalculatorErrorCode.DomainError); // negative ATR
        Fails("zigzag_causal([100], [90], 0, 0)", CalculatorErrorCode.DomainError);
        Fails("zigzag_causal([100, 250], [100, 100], 100, 0)", CalculatorErrorCode.DomainError); // >= 100% deadlock guard
        Fails("zigzag_causal([100, 250], [100, 100], 150, 0)", CalculatorErrorCode.DomainError);

        // MP-1: threshold = 99.99% remains valid and completes downward reversal;
        // D1 tie-break distinguishes 1e16 vs 1e16 + 1 when double comparison would collide.
        Assert.Equal(new[] { -1m, 100m, 200m }, Arr("zigzag_causal([100, 200, 200], [100, 100, 0.01], 99.99, 0, 0, [], -1)"));
        decimal baseHi = 10_000_000_000_000_001m; // 1e16 + 1 -> initHigh - low = 1e16 + 1 vs high - initLow = 1e16
        decimal[] tieH = { 10_000_000_000_000_001m, 10_000_000_000_000_000m };
        decimal[] tieL = { 0m, 0m };
        // At t=0: initHigh=1e16+1, initLow=0, atr=1e20 (no reversal). At t=1: atr=1, h<=initHigh, l>=initLow -> moveUp=1e16 < moveDown=1e16+1 => down (+1)
        decimal[] tieRes = RunWithArrays(
            "zigzag_causal(h, l, 1, 1, 1, [1e20, 1], -9)",
            new Dictionary<string, IReadOnlyList<decimal>>(StringComparer.Ordinal) { ["h"] = tieH, ["l"] = tieL }).ArrayValues!.ToArray();
        Assert.Equal(new[] { -9m, 1m }, tieRes);
        Assert.Equal(baseHi, tieH[0]);
    }

    [Fact]
    public void RdpSimplify_HandlesCollinearDecimalExactGuardAndBoundaries()
    {
        // D-10: decimal-exact arithmetic sequence [1.0, 1.1, 1.2, 1.3] at epsilon=0 keeps only endpoints [0, 3]
        Assert.Equal(new[] { 0m, 3m }, Arr("rdp_simplify([1.0, 1.1, 1.2, 1.3], 0)"));

        // Large-offset decimal difference avoids double subtraction collapse ([1e17, 1e17+1, 1e17+3] is non-collinear)
        Assert.Equal(new[] { 0m, 1m, 2m }, Arr("rdp_simplify([1e17, 1e17 + 1, 1e17 + 3], 0.1)"));

        // Peak at index 2 is kept when epsilon < distance, dropped when epsilon is large
        Assert.Equal(new[] { 0m, 2m, 4m }, Arr("rdp_simplify([0, 1, 10, 1, 0], 1)"));
        Assert.Equal(new[] { 0m, 4m }, Arr("rdp_simplify([0, 1, 10, 1, 0], 10)"));

        Assert.Empty(Arr("rdp_simplify([], 0)"));
        Assert.Equal(new[] { 0m }, Arr("rdp_simplify([42], 0)"));
        Assert.Equal(new[] { 0m, 1m }, Arr("rdp_simplify([42, 43], 0)"));

        // > 1e24 magnitude exercises double fallback cleanly without throwing OverflowException
        Assert.Equal(new[] { 0m, 1m, 2m }, Arr("rdp_simplify([0, 2e25, 0], 1)"));

        // MP-3: metric = 1 (Vertical Price Distance in D1) achieves slope & price-level invariance
        Assert.Equal(new[] { 0m, 4m }, Arr("rdp_simplify([10000000, 10100000, 10500000, 10750000, 11000000], 50000, 0)"));
        Assert.Equal(new[] { 0m, 1m, 2m, 4m }, Arr("rdp_simplify([10000000, 10100000, 10500000, 10750000, 11000000], 50000, 1)"));
        Assert.Equal(new[] { 0m, 1m, 2m }, Arr("rdp_simplify([38000, 38700, 39000], 199.9, 1)"));
        Assert.Equal(new[] { 0m, 2m }, Arr("rdp_simplify([38000, 38700, 39000], 200.1, 1)"));

        Fails("rdp_simplify([1, 2, 3], -0.1)", CalculatorErrorCode.DomainError);
        Fails("rdp_simplify([1, 2, 3], 1, 2)", CalculatorErrorCode.DomainError);
        Fails("rdp_simplify(1, 0)", CalculatorErrorCode.DomainError);
    }

    [Fact]
    public void PipExtract_ExtractsKeyPointsWithVerticalDistanceAndTieBreak()
    {
        // Double top shape [0, 10, 2, 10, 0] -> k=5 returns all 5; k=2 returns endpoints; k=3 picks lowest-index peak (1)
        Assert.Equal(new[] { 0m, 1m, 2m, 3m, 4m }, Arr("pip_extract([0, 10, 2, 10, 0], 5)"));
        Assert.Equal(new[] { 0m, 4m }, Arr("pip_extract([0, 10, 2, 10, 0], 2)"));
        Assert.Equal(new[] { 0m, 1m, 4m }, Arr("pip_extract([0, 10, 2, 10, 0], 3)"));
        Assert.Equal(new[] { 0m, 1m, 3m, 4m }, Arr("pip_extract([0, 10, 2, 10, 0], 4, 0)"));

        // MP-1: D1 decimal VD fixes binary64 rounding tie-break [1.0, 1.1, 1.2, 1.3, 1.4] -> [0, 1, 4] and large offset [1e17..]
        Assert.Equal(new[] { 0m, 1m, 4m }, Arr("pip_extract([1.0, 1.1, 1.2, 1.3, 1.4], 3)"));
        Assert.Equal(new[] { 0m, 1m, 2m, 4m }, Arr("pip_extract([1.0, 1.1, 1.2, 1.3, 1.4], 4)"));
        Assert.Equal(new[] { 0m, 1m, 4m }, Arr("pip_extract([0, 1.3, 0, 1.3, 0], 3)"));
        Assert.Equal(new[] { 0m, 2m, 3m }, Arr("pip_extract([1e17, 1e17 + 2, 1e17 + 10, 1e17 + 6], 3)"));

        // MP-2: dynamic budget avoids overcharging k == n early path on 512-element arrays
        decimal[] s512 = Enumerable.Range(0, 512).Select(static i => (decimal)(i % 13)).ToArray();
        var map512 = new Dictionary<string, IReadOnlyList<decimal>>(StringComparer.Ordinal) { ["s"] = s512 };
        var repeatedFull = RunWithArrays("arr_len(pip_extract(s, 512)) + arr_len(pip_extract(s, 512))", map512);
        Assert.True(repeatedFull.IsOk, repeatedFull.Error?.Message);
        Assert.Equal(1024m, repeatedFull.ScalarValue);

        Fails("pip_extract([1], 2)", CalculatorErrorCode.DomainError);
        Fails("pip_extract([1, 2, 3], 1)", CalculatorErrorCode.DomainError);
        Fails("pip_extract([1, 2, 3], 4)", CalculatorErrorCode.DomainError);
        Fails("pip_extract([1, 2, 3], 2, 1)", CalculatorErrorCode.DomainError); // reserved metric != 0
    }

    [Fact]
    public void DtwDistance_MatchesDtwMathCalculateAndEnforcesCellBudget()
    {
        double[] p = { 0.0, 1.0, 0.2, 1.0, 0.0 };
        double[] q = { 0.0, 0.9, 0.9, 0.1, 1.0, 0.0 };
        decimal expectedUnconstrained = StockAnalyzer.Core.Models.Indicators.IndicatorCalculationHelper.ToDecimalOrNull(
            StockAnalyzer.Core.MathUtils.DtwMath.Calculate(p, q, -1))!.Value;
        decimal expectedBand1 = StockAnalyzer.Core.Models.Indicators.IndicatorCalculationHelper.ToDecimalOrNull(
            StockAnalyzer.Core.MathUtils.DtwMath.Calculate(p, q, 1))!.Value;

        Assert.Equal(expectedUnconstrained, Scalar("dtw_distance([0, 1, 0.2, 1, 0], [0, 0.9, 0.9, 0.1, 1, 0])"));
        Assert.Equal(expectedBand1, Scalar("dtw_distance([0, 1, 0.2, 1, 0], [0, 0.9, 0.9, 0.1, 1, 0], 1)"));
        Assert.Equal(0m, Scalar("dtw_distance([5], [5])"));
        Assert.Equal(3m, Scalar("dtw_distance([5], [2])"));

        // 512 x 512 = 262,144 cells -> 8,192 budget units: succeeds under 10,000 ops once, fails when repeated twice
        decimal[] s512 = Enumerable.Range(0, 512).Select(static i => (decimal)(i % 7)).ToArray();
        var map512 = new Dictionary<string, IReadOnlyList<decimal>>(StringComparer.Ordinal) { ["s"] = s512 };
        var ok512 = RunWithArrays("dtw_distance(s, s)", map512);
        Assert.True(ok512.IsOk, ok512.Error?.Message);
        Assert.Equal(0m, ok512.ScalarValue);

        var budgetFail = RunWithArrays("dtw_distance(s, s) + dtw_distance(s, s)", map512);
        Assert.False(budgetFail.IsOk);
        Assert.Equal(CalculatorErrorCode.BudgetExceeded, budgetFail.Error!.Code);

        Fails("dtw_distance([], [1])", CalculatorErrorCode.DomainError);
        Fails("dtw_distance([1, 2], [1], -2)", CalculatorErrorCode.DomainError);
        Fails("dtw_distance([1, 2], [1], 3)", CalculatorErrorCode.DomainError);
    }

    [Fact]
    public void CusumFilter_UsesSymmetricDualResetToPreventPostReversalChattering()
    {
        // Sequence: [100, 92 (-8), 99 (+7), 97 (-2)] with threshold = 5:
        // t=1: delta=-8 -> sNeg=-8 <= -5 -> emits -1 and resets BOTH sPos=0, sNeg=0
        // t=2: delta=+7 -> sPos=+7 >= 5 -> emits +1 and resets BOTH sPos=0, sNeg=0
        // t=3: delta=-2 -> sNeg=-2 > -5 -> emits 0 (no chattering from stale pre-reversal sNeg!)
        Assert.Equal(new[] { 0m, -1m, 1m, 0m }, Arr("cusum_filter([100, 92, 99, 97], 5)"));
        Assert.Equal(new[] { 0m }, Arr("cusum_filter([42], 1)"));
        Assert.Empty(Arr("cusum_filter([], 1)"));

        Fails("cusum_filter([1, 2], 0)", CalculatorErrorCode.DomainError);
        Fails("cusum_filter([1, 2], -1)", CalculatorErrorCode.DomainError);
        Fails("cusum_filter([-7e28, 7e28], 1)", CalculatorErrorCode.ArithmeticOverflow);
    }

    [Fact]
    public void ArrInterpAt_ReconstructsLinearWaveformsAndResidualsInExactDecimal()
    {
        Assert.Equal(new[] { 10m, 20m, 30m, 40m }, Arr("arr_interp_at([0, 3], [10, 99, 99, 40])"));
        Assert.Equal(new[] { -1m, 10m, 15m, 20m, -1m }, Arr("arr_interp_at([1, 3], [99, 10, 99, 20, 99], -1)"));
        Assert.Equal(new[] { -1m, 20m, -1m }, Arr("arr_interp_at([1], [10, 20, 30], -1)"));
        Assert.Equal(new[] { -9m, -9m, -9m }, Arr("arr_interp_at([], [1, 2, 3], -9)"));
        Assert.Empty(Arr("arr_interp_at([], [], -9)"));
        AssertNoNegativeZero(Arr("arr_interp_at([], [1], -0.0)"), "arr_interp_at");

        // Extreme cross-sign overflow-safe interpolation: midpoint of [-5e28, 5e28] -> 0
        Assert.Equal(new[] { -5e28m, 0m, 5e28m }, Arr("arr_interp_at([0, 2], [-5e28, 999, 5e28])"));

        // Direct composition with rdp_simplify (metric = 1): residual trendline deviation at center index 1 is +5
        Assert.Equal(
            new[] { 0m, 5m, 0m },
            Arr("arr_zip_sub([10, 15, 10], arr_interp_at(rdp_simplify([10, 15, 10], 10, 1), [10, 15, 10]))"));

        Fails("arr_interp_at([1, 1], [10, 20, 30])", CalculatorErrorCode.DomainError);
        Fails("arr_interp_at([2, 1], [10, 20, 30])", CalculatorErrorCode.DomainError);
        Fails("arr_interp_at([-1], [10, 20])", CalculatorErrorCode.DomainError);
        Fails("arr_interp_at([2], [10, 20])", CalculatorErrorCode.DomainError);
        Fails("arr_interp_at([0.5], [10, 20])", CalculatorErrorCode.DomainError);
        Fails("arr_interp_at([0], [])", CalculatorErrorCode.DomainError);
    }
}
