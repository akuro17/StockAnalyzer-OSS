#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Calculator;
using StockAnalyzer.Core.MathUtils;
using Xunit;
using Xunit.Abstractions;

namespace StockAnalyzer.Core.Tests.Calculator;

public sealed class CalculatorDslV8ExpressivenessTests
{
    private static readonly IReadOnlyDictionary<string, decimal> EmptyScalars = new Dictionary<string, decimal>(StringComparer.Ordinal);
    private readonly ITestOutputHelper _output;

    public CalculatorDslV8ExpressivenessTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static ExpressionCalculator.CalculatorBatchResult EvalBatchOne(
        string expr,
        IReadOnlyDictionary<string, IReadOnlyList<decimal>>? arrays = null,
        CalculatorLimits? limits = null)
    {
        IReadOnlyList<ExpressionCalculator.CalculatorBatchResult> results = ExpressionCalculator.EvaluateBatch(
            EmptyScalars,
            arrays,
            new[] { new ExpressionCalculator.CalculatorBatchItem("r", expr) },
            limits ?? CalculatorLimits.Default);
        return results[0];
    }

    // ---- Batch 1: Core Limits, Arena Capacity, Combinators, arr_where, mat_vstack ----

    [Fact]
    public void CalculatorLimits_MaxArenaElements_ValidatesBoundsAndPropagatesThroughScopedView()
    {
        Assert.Equal(4_096, CalculatorLimits.Default.MaxArenaElements);

        Assert.Throws<ArgumentOutOfRangeException>(() => new CalculatorLimits { MaxArenaElements = 4_095 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CalculatorLimits { MaxArenaElements = 32_769 }.Validate());

        var customLimits = new CalculatorLimits { MaxArenaElements = 16_384, MaxExecutionOps = 50_000 };
        customLimits.Validate();

        // Populate > 4_096 arena elements via caller arrays so scoped higher-order allocations happen above offset 4_096.
        decimal[] seedBlock = Enumerable.Range(1, 512).Select(i => (decimal)i).ToArray();
        var arrays = new Dictionary<string, IReadOnlyList<decimal>>(StringComparer.Ordinal);
        for (int k = 0; k < 9; k++)
        {
            arrays[$"a{k}"] = seedBlock;
        }

        // Default 4_096 arena throws ArgumentOutOfRangeException when loading 9 * 512 = 4_608 elements.
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            EvalBatchOne("arr_get(a8, 0)", arrays, CalculatorLimits.Default));

        // 16_384 arena loads 4_608 elements AND runs a higher-order scoped view above offset 4_096.
        var res = EvalBatchOne("arr_rolling_map(sum(arr_map(v * 2, v, w)), w, arr_slice(a8, 0, 4), 2)", arrays, customLimits);
        Assert.True(res.IsOk, res.Error?.Message);
        Assert.Equal(new[] { 6m, 10m, 14m }, res.ArrayValues);
    }

    [Fact]
    public void ArrScanStateAndReduceState_ExecuteMultiStateRecurrenceAndEnforceBoundaries()
    {
        var scan1 = EvalBatchOne("arr_scan_state([s0 + x, s1 + s0 + x], [s0, s1], x, [1, 2, 3], [0, 0], 1)");
        Assert.True(scan1.IsOk, scan1.Error?.Message);
        Assert.Equal(new[] { 1m, 4m, 10m }, scan1.ArrayValues);

        var scan0 = EvalBatchOne("arr_scan_state([s0 + x, s1 + s0 + x], [s0, s1], x, [1, 2, 3], [0, 0], 0)");
        Assert.True(scan0.IsOk, scan0.Error?.Message);
        Assert.Equal(new[] { 1m, 3m, 6m }, scan0.ArrayValues);

        var reduce = EvalBatchOne("arr_reduce_state([s0 + x, s1 + s0 * x], [s0, s1], x, [2, 3, 4], [1, 0])");
        Assert.True(reduce.IsOk, reduce.Error?.Message);
        Assert.Equal(new[] { 10m, 35m }, reduce.ArrayValues);

        // Empty array handling & negative zero normalization
        var emptyScan = EvalBatchOne("arr_scan_state([s0 + x], [s0], x, [], [5], 0)");
        Assert.True(emptyScan.IsOk, emptyScan.Error?.Message);
        Assert.Empty(emptyScan.ArrayValues!);

        var emptyReduce = EvalBatchOne("arr_reduce_state([s0 + x, s1], [s0, s1], x, [], [-0.0, 7])");
        Assert.True(emptyReduce.IsOk, emptyReduce.Error?.Message);
        Assert.Equal(new[] { 0m, 7m }, emptyReduce.ArrayValues);

        // Hybrid stackalloc / ArrayPool path (n = 40 > 32)
        var longScan = EvalBatchOne("arr_scan_state([s0 + x], [s0], x, arange(1, 41, 1), [0], 0)");
        Assert.True(longScan.IsOk, longScan.Error?.Message);
        Assert.Equal(40, longScan.ArrayValues!.Count);
        Assert.Equal(820m, longScan.ArrayValues[^1]);

        // CF-04: wrong arity does not misreport itemVar as UNDEFINED_NAME, whereas unresolved ordinary arg still reports UNDEFINED_NAME.
        Assert.Equal(CalculatorErrorCode.WrongArgumentCount, EvalBatchOne("arr_scan_state([s0], [s0], itemVar, [1, 2], [0])").Error!.Code);
        Assert.Equal(CalculatorErrorCode.UndefinedName, EvalBatchOne("arr_scan_state([s0], [s0], itemVar, unknownArr, [0])").Error!.Code);

        // Domain / DimensionMismatch error checks
        Assert.Equal(CalculatorErrorCode.DomainError, EvalBatchOne("arr_scan_state([], [s0], x, [1], [0], 0)").Error!.Code);
        Assert.Equal(CalculatorErrorCode.DimensionMismatch, EvalBatchOne("arr_scan_state([s0], [s0, s1], x, [1], [0], 0)").Error!.Code);
        Assert.Equal(CalculatorErrorCode.DimensionMismatch, EvalBatchOne("arr_reduce_state([s0, s1], [s0, s1], x, [1], [0])").Error!.Code);
        Assert.Equal(CalculatorErrorCode.DomainError, EvalBatchOne("arr_reduce_state([s0, s1], [s0, s0], x, [1], [0, 0])").Error!.Code);
        Assert.Equal(CalculatorErrorCode.DomainError, EvalBatchOne("arr_reduce_state([s0], [x], x, [1], [0])").Error!.Code);
        Assert.Equal(CalculatorErrorCode.DomainError, EvalBatchOne("arr_scan_state([s0], [s0], x, [1], [0], 1)").Error!.Code);
        Assert.Equal(CalculatorErrorCode.DomainError, EvalBatchOne("arr_scan_state([s0], [s0], x, [1], [0], -1)").Error!.Code);
    }

    [Fact]
    public void ArrRollingMap_SupportsArrayLoweringHybridLengthAndLexicalShadowing()
    {
        var avgRes = EvalBatchOne("arr_rolling_map(avg(w), w, [1, 2, 3, 4, 5], 3)");
        Assert.True(avgRes.IsOk, avgRes.Error?.Message);
        Assert.Equal(new[] { 2m, 3m, 4m }, avgRes.ArrayValues);

        // CF-02: BoundArrayScope lookahead enables single-array-lowered calls like stdev_s(w)
        var stdevRes = EvalBatchOne("arr_rolling_map(stdev_s(w), w, [2, 4, 4, 4, 5, 5, 7, 9], 4)");
        Assert.True(stdevRes.IsOk, stdevRes.Error?.Message);
        Assert.Equal(5, stdevRes.ArrayValues!.Count);
        var expectedFirst = EvalBatchOne("stdev_s([2, 4, 4, 4])").ScalarValue;
        Assert.Equal(expectedFirst, stdevRes.ArrayValues[0]);

        // Lexical shadowing: inner arr_rolling_map window variable `w` shadows outer scalar accumulator `w`
        var shadowRes = EvalBatchOne("arr_scan(arr_rolling_map(sum(w), w, [1, 2, 3], 2)[0] + item, w, item, [10, 20], 0)");
        Assert.True(shadowRes.IsOk, shadowRes.Error?.Message);
        Assert.Equal(new[] { 13m, 23m }, shadowRes.ArrayValues);

        // Hybrid pool buffer (m = 40 - 3 + 1 = 38 > 32) with nested array ops in body
        var medianRes = EvalBatchOne("arr_rolling_map(arr_get(arr_sort(w), 1), w, arange(1, 41, 1), 3)");
        Assert.True(medianRes.IsOk, medianRes.Error?.Message);
        Assert.Equal(38, medianRes.ArrayValues!.Count);
        Assert.Equal(2m, medianRes.ArrayValues[0]);
        Assert.Equal(39m, medianRes.ArrayValues[^1]);

        // Validation
        Assert.Equal(CalculatorErrorCode.DomainError, EvalBatchOne("arr_rolling_map(sum(w), w, [], 1)").Error!.Code);
        Assert.Equal(CalculatorErrorCode.DomainError, EvalBatchOne("arr_rolling_map(sum(w), w, [1, 2, 3], 0)").Error!.Code);
        Assert.Equal(CalculatorErrorCode.DomainError, EvalBatchOne("arr_rolling_map(sum(w), w, [1, 2, 3], 4)").Error!.Code);
        Assert.Equal(CalculatorErrorCode.WrongArgumentCount, EvalBatchOne("arr_rolling_map(sum(w), w, [1, 2, 3])").Error!.Code);
    }

    [Fact]
    public void ArrWhere_EnforcesStageOrderZeroNormalizationAndEmptyArrays()
    {
        var ok = EvalBatchOne("arr_where([1, 0, -2.5], [10, 20, 30], [-1, -0.0, -3])");
        Assert.True(ok.IsOk, ok.Error?.Message);
        Assert.Equal(new[] { 10m, 0m, 30m }, ok.ArrayValues);

        // Stage 1: kinds (DomainError) checked before lengths
        Assert.Equal(CalculatorErrorCode.DomainError, EvalBatchOne("arr_where(1, [1, 2], [3])").Error!.Code);
        // Stage 2: lengths (DimensionMismatch)
        Assert.Equal(CalculatorErrorCode.DimensionMismatch, EvalBatchOne("arr_where([1, 0], [1], [2, 3])").Error!.Code);
        // Stage 3: empty arrays -> []
        var empty = EvalBatchOne("arr_where([], [], [])");
        Assert.True(empty.IsOk, empty.Error?.Message);
        Assert.Empty(empty.ArrayValues!);
    }

    [Fact]
    public void MatVstack_EnforcesMatHstackSymmetryAndValidationOrder()
    {
        var ok = EvalBatchOne("mat_vstack([1, 2, 3, -0.0], [5, 6], 2)");
        Assert.True(ok.IsOk, ok.Error?.Message);
        Assert.Equal(new[] { 1m, 2m, 3m, 0m, 5m, 6m }, ok.ArrayValues);

        // Stage 1: kinds -> DomainError
        Assert.Equal(CalculatorErrorCode.DomainError, EvalBatchOne("mat_vstack(1, [2], 1)").Error!.Code);
        Assert.Equal(CalculatorErrorCode.DomainError, EvalBatchOne("mat_vstack([1], [2], [1])").Error!.Code);
        // Stage 2: cols range -> DomainError
        Assert.Equal(CalculatorErrorCode.DomainError, EvalBatchOne("mat_vstack([1, 2], [3, 4], 0)").Error!.Code);
        Assert.Equal(CalculatorErrorCode.DomainError, EvalBatchOne("mat_vstack([1, 2], [3, 4], 1.5)").Error!.Code);
        // Stage 2.5: empty matrices -> DomainError (symmetric with mat_hstack)
        Assert.Equal(CalculatorErrorCode.DomainError, EvalBatchOne("mat_vstack([], [1, 2], 2)").Error!.Code);
        Assert.Equal(CalculatorErrorCode.DomainError, EvalBatchOne("mat_vstack([1, 2], [], 2)").Error!.Code);
        // Stage 3: dimensions -> DimensionMismatch
        Assert.Equal(CalculatorErrorCode.DimensionMismatch, EvalBatchOne("mat_vstack([1, 2, 3], [4, 5], 2)").Error!.Code);
        Assert.Equal(CalculatorErrorCode.DimensionMismatch, EvalBatchOne("mat_vstack(arr_zeros(300), arr_zeros(300), 10)").Error!.Code);
    }

    // ---- Batch 2: Constrained Optimization (opt_lp, opt_qp_eq) ----

    [Fact]
    public void OptLp_SolvesStandardPhaseIPhaseIIBoxFastPathAndRejectsInvalidInputs()
    {
        // Box-only fast path (m = 0)
        var boxOnly = EvalBatchOne("opt_lp([1, -2, 0], [], [], [-1, 0, 2], [3, 5, 4])");
        Assert.True(boxOnly.IsOk, boxOnly.Error?.Message);
        Assert.Equal(new[] { -1m, 5m, 2m }, boxOnly.ArrayValues);

        // Phase I + Phase II with shifted lower bounds (lb = [1, 1]) and redundant constraint forcing artificial cleanup
        // min x0 + 2*x1 s.t. x0 + x1 >= 4 (-x0 - x1 <= -4), -2*x0 - 2*x1 <= -8, 1 <= x <= 5 -> x* = [3, 1]
        var phase1Res = EvalBatchOne("opt_lp([1, 2], [-1, -1, -2, -2], [-4, -8], [1, 1], [5, 5], 1e-10)");
        Assert.True(phase1Res.IsOk, phase1Res.Error?.Message);
        Assert.Equal(2, phase1Res.ArrayValues!.Count);
        Assert.InRange(phase1Res.ArrayValues[0], 2.999999m, 3.000001m);
        Assert.InRange(phase1Res.ArrayValues[1], 0.999999m, 1.000001m);

        // Classic 2D LP: min -5*x0 - 4*x1 s.t. x0 + x1 <= 4, 0 <= x0 <= 2, 0 <= x1 <= 3 -> [2, 2]
        var stdRes = EvalBatchOne("opt_lp([-5, -4], [1, 1], [4], [0, 0], [2, 3])");
        Assert.True(stdRes.IsOk, stdRes.Error?.Message);
        Assert.Equal(new[] { 2m, 2m }, stdRes.ArrayValues);

        // Infeasible -> ConvergenceFailure
        var infeasible = EvalBatchOne("opt_lp([1, 1], [1, 1, -1, -1], [1, -3], [0, 0], [10, 10])");
        Assert.Equal(CalculatorErrorCode.ConvergenceFailure, infeasible.Error!.Code);

        // Validation contracts
        Assert.Equal(CalculatorErrorCode.DomainError, EvalBatchOne("opt_lp([], [], [], [], [])").Error!.Code);
        Assert.Equal(CalculatorErrorCode.DomainError, EvalBatchOne("opt_lp([1], [1], [1], [2], [1])").Error!.Code);
        Assert.Equal(CalculatorErrorCode.DomainError, EvalBatchOne("opt_lp([1], [1], [1], [0], [2], 0.1)").Error!.Code);
        Assert.Equal(CalculatorErrorCode.DimensionMismatch, EvalBatchOne("opt_lp(arr_ones(17), arr_ones(17), [1], arr_zeros(17), arr_ones(17))").Error!.Code);
        Assert.Equal(CalculatorErrorCode.DimensionMismatch, EvalBatchOne("opt_lp([1, 2], [1], [1], [0, 0], [1, 1])").Error!.Code);
    }

    [Fact]
    public void OptQpEq_SolvesMarkowitzConstraintsAndMaxScaleDim16WithinBudget()
    {
        // Markowitz min 0.5*(0.04*x0^2 + 0.09*x1^2 + 0.16*x2^2) s.t. x0 + x1 + x2 = 1, 0 <= x <= 1
        // Exact analytical inverse-variance weights: [36/61, 16/61, 9/61]
        var mkRes = EvalBatchOne("opt_qp_eq([0.04, 0, 0, 0, 0.09, 0, 0, 0, 0.16], [0, 0, 0], [1, 1, 1], [1], [0, 0, 0], [1, 1, 1], 1e-10)");
        Assert.True(mkRes.IsOk, mkRes.Error?.Message);
        Assert.Equal(3, mkRes.ArrayValues!.Count);
        Assert.InRange(mkRes.ArrayValues[0], 36m / 61m - 1e-7m, 36m / 61m + 1e-7m);
        Assert.InRange(mkRes.ArrayValues[1], 16m / 61m - 1e-7m, 16m / 61m + 1e-7m);
        Assert.InRange(mkRes.ArrayValues[2], 9m / 61m - 1e-7m, 9m / 61m + 1e-7m);

        // Error contracts: m_eq == 0 -> DomainError, m_eq > n -> DimensionMismatch, dependent Aeq -> SingularMatrix, asymmetric/indefinite H -> DomainError
        Assert.Equal(CalculatorErrorCode.DomainError, EvalBatchOne("opt_qp_eq([1, 0, 0, 1], [0, 0], [], [], [0, 0], [1, 1])").Error!.Code);
        Assert.Equal(CalculatorErrorCode.DimensionMismatch, EvalBatchOne("opt_qp_eq([1], [0], [1, 1], [1, 2], [0], [1])").Error!.Code);
        Assert.Equal(CalculatorErrorCode.SingularMatrix, EvalBatchOne("opt_qp_eq([1, 0, 0, 1], [0, 0], [1, 1, 2, 2], [1, 2], [0, 0], [1, 1])").Error!.Code);
        Assert.Equal(CalculatorErrorCode.DomainError, EvalBatchOne("opt_qp_eq([1, 2, 0, 1], [0, 0], [1, 1], [1], [0, 0], [1, 1])").Error!.Code);
        Assert.Equal(CalculatorErrorCode.DomainError, EvalBatchOne("opt_qp_eq([1, 0, 0, -1], [0, 0], [1, 1], [1], [0, 0], [1, 1])").Error!.Code);

        // Box infeasible equality -> ConvergenceFailure
        Assert.Equal(CalculatorErrorCode.ConvergenceFailure, EvalBatchOne("opt_qp_eq([1, 0, 0, 1], [0, 0], [1, 1], [5], [0, 0], [1, 1])").Error!.Code);

        // Max-scale verification (n = 16, m_eq = 16, KKT dim = 32) under CalculatorLimits.Default (MaxExecutionOps = 10_000)
        const int maxN = 16;
        const int maxEq = 16;
        var hMax = new decimal[maxN * maxN];
        var aEqMax = new decimal[maxEq * maxN];
        var bEqMax = new decimal[maxEq];
        var cMax = new decimal[maxN];
        var lbMax = new decimal[maxN];
        var ubMax = new decimal[maxN];
        for (int i = 0; i < maxN; i++)
        {
            hMax[i * maxN + i] = 2m + i * 0.1m;
            if (i + 1 < maxN)
            {
                hMax[i * maxN + (i + 1)] = 0.2m;
                hMax[(i + 1) * maxN + i] = 0.2m;
            }
            cMax[i] = -0.1m * (i + 1);
            lbMax[i] = -2m;
            ubMax[i] = 2m;
            aEqMax[i * maxN + i] = 1m;
            if (i > 0) aEqMax[i * maxN + (i - 1)] = 0.25m;
            bEqMax[i] = 0.25m;
        }

        // Also test multi-iteration active-box portfolio QP at n = 16, m_eq = 4 (linearly independent rows) under CalculatorLimits.Default
        const int portEq = 4;
        var aEqPort = new decimal[portEq * maxN];
        for (int j = 0; j < maxN; j++)
        {
            aEqPort[j] = 1m;                                        // sum = 1.0
            aEqPort[maxN + j] = j < 8 ? 1m : 0m;                   // sleeve 1 sum = 0.6 (sleeve 2 is implicitly 0.4)
            aEqPort[2 * maxN + j] = (j % 2 == 0) ? 1m : -1m;       // net parity = 0.1
            aEqPort[3 * maxN + j] = j < 4 ? 1m : (j >= 12 ? -1m : 0m); // quartile tilt = 0.0
            lbMax[j] = 0m;
            ubMax[j] = 0.25m;
        }

        var arrays = new Dictionary<string, IReadOnlyList<decimal>>(StringComparer.Ordinal)
        {
            ["H"] = hMax,
            ["c"] = cMax,
            ["AeqMax"] = aEqMax,
            ["beqMax"] = bEqMax,
            ["lbBox"] = Enumerable.Repeat(-2m, maxN).ToArray(),
            ["ubBox"] = Enumerable.Repeat(2m, maxN).ToArray(),
            ["AeqPort"] = aEqPort,
            ["beqPort"] = new[] { 1m, 0.6m, 0.1m, 0m },
            ["lbPort"] = lbMax,
            ["ubPort"] = ubMax,
        };

        var resMax = EvalBatchOne("opt_qp_eq(H, c, AeqMax, beqMax, lbBox, ubBox)", arrays, CalculatorLimits.Default);
        Assert.True(resMax.IsOk, resMax.Error?.Message);
        Assert.Equal(maxN, resMax.ArrayValues!.Count);
        for (int i = 0; i < maxEq; i++)
        {
            decimal lhs = 0m;
            for (int j = 0; j < maxN; j++) lhs += aEqMax[i * maxN + j] * resMax.ArrayValues[j];
            Assert.InRange(Math.Abs(lhs - bEqMax[i]), 0m, 1e-6m);
        }

        var resPort = EvalBatchOne("opt_qp_eq(H, c, AeqPort, beqPort, lbPort, ubPort)", arrays, CalculatorLimits.Default);
        Assert.True(resPort.IsOk, resPort.Error?.Message);
        Assert.Equal(maxN, resPort.ArrayValues!.Count);
        Assert.InRange(Math.Abs(resPort.ArrayValues.Sum() - 1m), 0m, 1e-6m);
        Assert.InRange(Math.Abs(resPort.ArrayValues.Take(8).Sum() - 0.6m), 0m, 1e-6m);
        Assert.All(resPort.ArrayValues, w => Assert.InRange(w, 0m, 0.25m));
    }

    [Fact]
    public void ConstrainedOptimizationRemediation_VerifyScaleFixedVarsAndRedundantRows()
    {
        // 1. opt_qp_eq: micro-scale (1e-6) daily/intraday return covariance preserves scale-invariant exact solution
        var tinyCov = EvalBatchOne("opt_qp_eq([4e-8, 0, 0, 0, 9e-8, 0, 0, 0, 1.6e-7], [0, 0, 0], [1, 1, 1], [1], [0, 0, 0], [1, 1, 1], 1e-10)");
        Assert.True(tinyCov.IsOk, tinyCov.Error?.Message);
        Assert.InRange(tinyCov.ArrayValues![0], 36m / 61m - 1e-7m, 36m / 61m + 1e-7m);
        Assert.InRange(tinyCov.ArrayValues[1], 16m / 61m - 1e-7m, 16m / 61m + 1e-7m);
        Assert.InRange(tinyCov.ArrayValues[2], 9m / 61m - 1e-7m, 9m / 61m + 1e-7m);

        // 2. opt_qp_eq: strict 4-step precedence with fixed variables (F vs U)
        // Step 1: zero row in original aEq -> SingularMatrix
        Assert.Equal(CalculatorErrorCode.SingularMatrix, EvalBatchOne("opt_qp_eq([1, 0, 0, 1], [0, 0], [0, 0], [0], [0, 0], [0, 0])").Error!.Code);
        // Step 2: box-vs-equality impossible -> ConvergenceFailure
        Assert.Equal(CalculatorErrorCode.ConvergenceFailure, EvalBatchOne("opt_qp_eq([1, 0, 0, 1], [0, 0], [1, 1], [5], [0, 0], [1, 1])").Error!.Code);
        // Step 3 (nFree == 0): all fixed feasible vs infeasible
        var allFixedOk = EvalBatchOne("opt_qp_eq([1, 0, 0, 1], [0, 0], [1, 1], [3], [1, 2], [1, 2])");
        Assert.True(allFixedOk.IsOk, allFixedOk.Error?.Message);
        Assert.Equal(new[] { 1m, 2m }, allFixedOk.ArrayValues);
        Assert.Equal(CalculatorErrorCode.ConvergenceFailure, EvalBatchOne("opt_qp_eq([1, 0, 0, 1], [0, 0], [1, 1], [4], [1, 2], [1, 2])").Error!.Code);
        // Step 3 (fixed-only satisfied row dropped -> reduced problem over free U succeeds)
        var fixedDropOk = EvalBatchOne("opt_qp_eq([1,0,0, 0,1,0, 0,0,1], [0,0,0], [0,0,1, 1,1,1], [1, 2], [0,0,1], [10,10,1])");
        Assert.True(fixedDropOk.IsOk, fixedDropOk.Error?.Message);
        Assert.InRange(Math.Abs(fixedDropOk.ArrayValues![0] - 0.5m), 0m, 1e-6m);
        Assert.InRange(Math.Abs(fixedDropOk.ArrayValues[1] - 0.5m), 0m, 1e-6m);
        Assert.Equal(1m, fixedDropOk.ArrayValues[2]);
        // Step 4: dependent rows on free subspace U (full Aeq was rank-2, Aeq_U is rank-1) -> SingularMatrix
        Assert.Equal(CalculatorErrorCode.SingularMatrix, EvalBatchOne("opt_qp_eq([1,0,0, 0,1,0, 0,0,1], [0,0,0], [1,1,1, 1,1,0], [2, 1], [0,0,1], [10,10,1])").Error!.Code);

        // 3. opt_lp: extreme multi-scale rows + Phase I zero-level redundant near-dependent row
        var multiScaleLp = EvalBatchOne("opt_lp([1e-6, 2e-6], [-1e6, -1e6, -2e-6, -2e-6], [-4e6, -8e-6], [1, 1], [5, 5], 1e-10)");
        Assert.True(multiScaleLp.IsOk, multiScaleLp.Error?.Message);
        Assert.InRange(multiScaleLp.ArrayValues![0], 2.999999m, 3.000001m);
        Assert.InRange(multiScaleLp.ArrayValues[1], 0.999999m, 1.000001m);

        var degPerturbedLp = EvalBatchOne("opt_lp([1, 2, 1], [-1, -1, 0, -1, -1, -1e-11, 1, 1, 0], [-2, -2, 2], [0, 0, 0], [2, 2, 2], 1e-8)");
        Assert.True(degPerturbedLp.IsOk, degPerturbedLp.Error?.Message);
        Assert.InRange(Math.Abs(degPerturbedLp.ArrayValues![0] + degPerturbedLp.ArrayValues[1] - 2m), 0m, 1e-6m);
        Assert.InRange(degPerturbedLp.ArrayValues[2], -1e-7m, 1e-7m);
    }

    // ---- Batch 3: 2D Quadrature/Interpolation, Log-Space, Time-Series & Multivariate Distributions ----

    [Fact]
    public void Quad2dGk_IntegratesSmoothPeakedAndReversedDomainsAndEnforcesOrder()
    {
        // Exact integral of x^2 + y^2 over [0, 1] x [0, 1] = 2/3
        var poly = EvalBatchOne("quad2d_gk(x^2 + y^2, x, y, 0, 1, 0, 1)");
        Assert.True(poly.IsOk, poly.Error?.Message);
        Assert.InRange(Math.Abs(poly.ScalarValue - (2m / 3m)), 0m, 1e-12m);

        // Transcendental integral under CalculatorLimits.Default: int_0^1 int_0^1 exp(x + y) dy dx = (e - 1)^2
        var expRes = EvalBatchOne("quad2d_gk(exp(x + y), x, y, 0, 1, 0, 1)");
        Assert.True(expRes.IsOk, expRes.Error?.Message);
        decimal expExpected = (decimal)Math.Pow(Math.E - 1.0, 2.0);
        Assert.InRange(Math.Abs(expRes.ScalarValue - expExpected), 0m, 1e-12m);

        // Adaptive non-polynomial peaked integrand: int_0^1 int_0^1 1/(x + y + 0.05)^2 dy dx = ln(441 / 41)
        // Multiple adaptive 15x15 splits exceed Default 10_000 AST ops and succeed with expanded MaxExecutionOps.
        Assert.Equal(CalculatorErrorCode.BudgetExceeded, EvalBatchOne("quad2d_gk(1 / ((x + y + 0.05)^2), x, y, 0, 1, 0, 1)").Error!.Code);
        var peaked = EvalBatchOne(
            "quad2d_gk(1 / ((x + y + 0.05)^2), x, y, 0, 1, 0, 1)",
            limits: new CalculatorLimits { MaxExecutionOps = 50_000 });
        Assert.True(peaked.IsOk, peaked.Error?.Message);
        decimal expectedPeaked = (decimal)Math.Log(441.0 / 41.0);
        Assert.InRange(Math.Abs(peaked.ScalarValue - expectedPeaked), 0m, 1e-6m);

        // Reversed bounds flip sign: int_2^0 int_0^1 (x * y) dy dx = -1
        var reversed = EvalBatchOne("quad2d_gk(x * y, x, y, 2, 0, 0, 1)");
        Assert.True(reversed.IsOk, reversed.Error?.Message);
        Assert.InRange(Math.Abs(reversed.ScalarValue + 1m), 0m, 1e-12m);

        // Degenerate interval returns 0m WITHOUT evaluating body, AFTER validating xVar/yVar & 4 scalar bounds
        var deg = EvalBatchOne("quad2d_gk(1 / 0, x, y, 1, 1, 0, 2)");
        Assert.True(deg.IsOk, deg.Error?.Message);
        Assert.Equal(0m, deg.ScalarValue);

        Assert.Equal(CalculatorErrorCode.DomainError, EvalBatchOne("quad2d_gk(x * y, x, x, 1, 1, 0, 2)").Error!.Code);
        Assert.Equal(CalculatorErrorCode.DomainError, EvalBatchOne("quad2d_gk(x * y, x, y, [1], 1, 0, 2)").Error!.Code);
        Assert.Equal(CalculatorErrorCode.WrongArgumentCount, EvalBatchOne("quad2d_gk(xVar * yVar, xVar, yVar, 0, 1, 0)").Error!.Code);
    }

    [Fact]
    public void Interp2DBicubic_IsSymmetricUnderAxisSwapAndSuppressesOvershoot()
    {
        decimal[] xs = { 0m, 0.7m, 2.1m, 4.0m };
        decimal[] ys = { -1.0m, 0.2m, 1.5m };
        // Asymmetric grid Z[j * nx + i] (ny = 3 rows, nx = 4 cols)
        decimal[] z =
        {
            1.0m, 2.5m, 1.8m, 4.2m,
            0.4m, 3.1m, 2.9m, 5.0m,
            2.2m, 1.7m, 4.4m, 3.6m,
        };

        var zT = new decimal[z.Length];
        for (int j = 0; j < ys.Length; j++)
        {
            for (int i = 0; i < xs.Length; i++)
            {
                zT[i * ys.Length + j] = z[j * xs.Length + i];
            }
        }

        var arrays = new Dictionary<string, IReadOnlyList<decimal>>(StringComparer.Ordinal)
        {
            ["xs"] = xs,
            ["ys"] = ys,
            ["Z"] = z,
            ["ZT"] = zT,
        };

        // Exact at grid nodes
        var nodeVal = EvalBatchOne("interp2d_bicubic(xs, ys, Z, 2.1, 0.2)", arrays);
        Assert.True(nodeVal.IsOk, nodeVal.Error?.Message);
        Assert.Equal(2.9m, nodeVal.ScalarValue);

        // Exact x <-> y transpose symmetry on non-uniform 3x4 grid
        var q1 = EvalBatchOne("interp2d_bicubic(xs, ys, Z, 1.35, 0.65)", arrays);
        var q2 = EvalBatchOne("interp2d_bicubic(ys, xs, ZT, 0.65, 1.35)", arrays);
        Assert.True(q1.IsOk && q2.IsOk);
        Assert.InRange(Math.Abs(q1.ScalarValue - q2.ScalarValue), 0m, 1e-13m);

        // PCHIP overshoot suppression on monotone/step surface
        var stepArrays = new Dictionary<string, IReadOnlyList<decimal>>(StringComparer.Ordinal)
        {
            ["g"] = new[] { 0m, 1m, 2m, 3m },
            ["Zstep"] = new decimal[]
            {
                0m, 0m, 1m, 1m,
                0m, 0m, 1m, 1m,
                0m, 0m, 1m, 1m,
                0m, 0m, 1m, 1m,
            },
        };
        var midStep = EvalBatchOne("interp2d_bicubic(g, g, Zstep, 1.5, 1.5)", stepArrays);
        Assert.True(midStep.IsOk, midStep.Error?.Message);
        Assert.InRange(midStep.ScalarValue, 0m, 1m);

        // Validation
        Assert.Equal(CalculatorErrorCode.DomainError, EvalBatchOne("interp2d_bicubic([0, 1], [0, 1, 2], [1, 2, 3, 4, 5, 6], 0.5, 1)").Error!.Code);
        Assert.Equal(CalculatorErrorCode.DimensionMismatch, EvalBatchOne("interp2d_bicubic([0, 1, 2], [0, 1, 2], [1, 2, 3], 1, 1)").Error!.Code);
        Assert.Equal(CalculatorErrorCode.DomainError, EvalBatchOne("interp2d_bicubic(xs, ys, Z, -0.1, 0.2)", arrays).Error!.Code);
    }

    [Fact]
    public void LogNBallVolAndLogSumExpWeighted_HandleExtremesZeroWeightsAndValidation()
    {
        var vol10 = EvalBatchOne("nball_vol(10, 2)");
        var logVol10 = EvalBatchOne("log_nball_vol(10, 2)");
        Assert.True(vol10.IsOk && logVol10.IsOk);
        Assert.InRange(Math.Abs((decimal)Math.Log((double)vol10.ScalarValue) - logVol10.ScalarValue), 0m, 1e-11m);

        var largeDim = EvalBatchOne("log_nball_vol(500, 1)");
        Assert.True(largeDim.IsOk, largeDim.Error?.Message);
        Assert.True(largeDim.ScalarValue < 0m);
        Assert.Equal(CalculatorErrorCode.DomainError, EvalBatchOne("log_nball_vol(0, 1)").Error!.Code);
        Assert.Equal(CalculatorErrorCode.DomainError, EvalBatchOne("log_nball_vol(2, 0)").Error!.Code);

        // Zero-weighted extreme terms (10000 and -10000) are safely ignored
        var lseW = EvalBatchOne("log_sum_exp_weighted([1000, 1000, 10000, -10000], [1, 3, 0, 0])");
        Assert.True(lseW.IsOk, lseW.Error?.Message);
        decimal expectedLseW = 1000m + (decimal)Math.Log(4.0);
        Assert.InRange(Math.Abs(lseW.ScalarValue - expectedLseW), 0m, 1e-10m);

        Assert.Equal(CalculatorErrorCode.DomainError, EvalBatchOne("log_sum_exp_weighted([1, 2], [0, 0])").Error!.Code);
        Assert.Equal(CalculatorErrorCode.DomainError, EvalBatchOne("log_sum_exp_weighted([1, 2], [1, -0.1])").Error!.Code);
        Assert.Equal(CalculatorErrorCode.DimensionMismatch, EvalBatchOne("log_sum_exp_weighted([1, 2], [1])").Error!.Code);
    }

    [Fact]
    public void TimeSeriesV8Functions_ValidateSignalsParametersAndEconometricResults()
    {
        // kalman_1d_filter: default init vs explicit init
        var kfDef = EvalBatchOne("kalman_1d_filter([1, 2, 3, 4], 0.1, 0.5)");
        Assert.True(kfDef.IsOk, kfDef.Error?.Message);
        Assert.Equal(4, kfDef.ArrayValues!.Count);
        Assert.Equal(1m, kfDef.ArrayValues[0]);
        Assert.True(kfDef.ArrayValues[3] > kfDef.ArrayValues[0]);

        var kfExp = EvalBatchOne("kalman_1d_filter([1, 2, 3, 4], 0.1, 0.5, 0, 2)");
        Assert.True(kfExp.IsOk, kfExp.Error?.Message);
        Assert.True(kfExp.ArrayValues![0] < 1m && kfExp.ArrayValues[0] > 0m);
        Assert.Equal(CalculatorErrorCode.DomainError, EvalBatchOne("kalman_1d_filter([], 0.1, 0.5)").Error!.Code);
        Assert.Equal(CalculatorErrorCode.DomainError, EvalBatchOne("kalman_1d_filter([1, 2], -0.1, 0.5)").Error!.Code);
        Assert.Equal(CalculatorErrorCode.DomainError, EvalBatchOne("kalman_1d_filter([1, 2], 0.1, 0)").Error!.Code);

        // granger_test: x Granger-causes y (y_t = 0.85 * x_{t-1} + small noise), SSoT lagOrder in [1, 5] default 2
        const int tsLen = 48;
        var causeX = new decimal[tsLen];
        var effectY = new decimal[tsLen];
        for (int t = 0; t < tsLen; t++)
        {
            causeX[t] = (decimal)(Math.Sin(0.7 * t) + 0.35 * Math.Cos(1.3 * t));
            effectY[t] = t == 0
                ? 0m
                : 0.85m * causeX[t - 1] + 0.05m * (decimal)Math.Sin(2.1 * t);
        }

        // johansen_test: cointegrated pair y1 = random walk proxy, y2 = 2 * y1 + bounded oscillation
        var j1 = new decimal[tsLen];
        var j2 = new decimal[tsLen];
        decimal acc = 100m;
        for (int t = 0; t < tsLen; t++)
        {
            acc += (decimal)(0.5 * Math.Sin(0.4 * t) + 0.25 * Math.Cos(0.9 * t));
            j1[t] = acc;
            j2[t] = (2m * acc) + 0.2m * (decimal)Math.Sin(1.1 * t);
        }

        // arima_fit: AR(1) stationary process z_t = 0.6 * z_{t-1} + deterministic innovation
        var arimaSeries = new decimal[tsLen];
        arimaSeries[0] = 1m;
        for (int t = 1; t < tsLen; t++)
        {
            arimaSeries[t] = 0.6m * arimaSeries[t - 1] + 0.4m * (decimal)Math.Sin(0.8 * t);
        }

        var arrays = new Dictionary<string, IReadOnlyList<decimal>>(StringComparer.Ordinal)
        {
            ["x"] = causeX,
            ["y"] = effectY,
            ["j1"] = j1,
            ["j2"] = j2,
            ["ar"] = arimaSeries,
            ["lin"] = Enumerable.Range(1, 30).Select(i => (decimal)i).ToArray(),
        };

        var gRes = EvalBatchOne("granger_test(x, y)", arrays); // default lagOrder = 2
        Assert.True(gRes.IsOk, gRes.Error?.Message);
        Assert.Equal(4, gRes.ArrayValues!.Count);
        Assert.True(gRes.ArrayValues[0] > 10m);
        Assert.InRange(gRes.ArrayValues[1], 0m, 0.01m);
        Assert.Equal(2m, gRes.ArrayValues[2]);
        Assert.Equal(tsLen - 7m, gRes.ArrayValues[3]);

        // SSoT Granger lag range [1, 5] & min sample n >= 3p + 6
        Assert.Equal(CalculatorErrorCode.DomainError, EvalBatchOne("granger_test(x, y, 0)", arrays).Error!.Code);
        Assert.Equal(CalculatorErrorCode.DomainError, EvalBatchOne("granger_test(x, y, 6)", arrays).Error!.Code);
        Assert.Equal(CalculatorErrorCode.DomainError, EvalBatchOne("granger_test(arr_slice(x, 0, 11), arr_slice(y, 0, 11), 2)", arrays).Error!.Code);
        Assert.Equal(CalculatorErrorCode.DivisionByZero, EvalBatchOne("granger_test(arr_ones(20), arr_slice(y, 0, 20), 1)", arrays).Error!.Code);

        var jRes = EvalBatchOne("johansen_test(j1, j2)", arrays);
        Assert.True(jRes.IsOk, jRes.Error?.Message);
        Assert.Equal(3, jRes.ArrayValues!.Count);
        Assert.True(jRes.ArrayValues[0] > 15.4947m);
        // Constant first difference -> DivisionByZero flatness guard
        Assert.Equal(CalculatorErrorCode.DivisionByZero, EvalBatchOne("johansen_test(arr_slice(lin, 0, 15), arr_slice(j1, 0, 15))", arrays).Error!.Code);

        var arRes = EvalBatchOne("arima_fit(ar, 1, 0, 1)", arrays);
        Assert.True(arRes.IsOk, arRes.Error?.Message);
        Assert.Equal(4, arRes.ArrayValues!.Count);
        Assert.InRange(Math.Abs(arRes.ArrayValues[0]), 0m, 0.98m);
        Assert.InRange(Math.Abs(arRes.ArrayValues[1]), 0m, 0.98m);
        Assert.True(arRes.ArrayValues[2] > 0m);
        Assert.Equal(CalculatorErrorCode.DomainError, EvalBatchOne("arima_fit(ar, 0, 0, 0)", arrays).Error!.Code);
        Assert.Equal(CalculatorErrorCode.DivisionByZero, EvalBatchOne("arima_fit(lin, 1, 1, 0)", arrays).Error!.Code);
    }

    [Fact]
    public void TimeSeriesV8Remediation_KalmanNoDoubleCountJohansenScaleAndArimaSchurCohn()
    {
        // 1. kalman_1d_filter: omitted initX/initP initializes posterior at t=0 so q=0 yields exact running sample mean
        var kfMean = EvalBatchOne("kalman_1d_filter([10, 20, 30, 40], 0, 1)");
        Assert.True(kfMean.IsOk, kfMean.Error?.Message);
        decimal[] expectedMeans = { 10m, 15m, 20m, 25m };
        for (int i = 0; i < expectedMeans.Length; i++)
        {
            Assert.InRange(Math.Abs(kfMean.ArrayValues![i] - expectedMeans[i]), 0m, 1e-12m);
        }

        // Single-element array with default posterior init returns [series[0]] unchanged
        var kfSingleClean = EvalBatchOne("kalman_1d_filter(arr_slice([7, 9], 0, 1), 0, 1)");
        Assert.True(kfSingleClean.IsOk, kfSingleClean.Error?.Message);
        Assert.Equal(new[] { 7m }, kfSingleClean.ArrayValues);

        // 4-arg explicit prior initX = 0 still updates at t = 0
        var kfPrior4 = EvalBatchOne("kalman_1d_filter([10, 20], 0, 1, 0)");
        Assert.True(kfPrior4.IsOk, kfPrior4.Error?.Message);
        Assert.InRange(Math.Abs(kfPrior4.ArrayValues![0] - 5m), 0m, 1e-12m);
        Assert.InRange(Math.Abs(kfPrior4.ArrayValues![1] - 10m), 0m, 1e-12m);

        // 2. johansen_test: strict scale invariance down to 1e-5 level
        const int jLen = 48;
        var j1 = new decimal[jLen];
        var j2 = new decimal[jLen];
        decimal acc = 100m;
        for (int t = 0; t < jLen; t++)
        {
            acc += (decimal)(0.5 * Math.Sin(0.4 * t) + 0.25 * Math.Cos(0.9 * t));
            j1[t] = acc;
            j2[t] = (2m * acc) + 0.2m * (decimal)Math.Sin(1.1 * t);
        }

        // 3. arima_fit: ARMA(2,1) with true phi1 = 1.5 > 0.98 is not clipped at 0.98,
        // and high-variance pure AR(1) (sigma2 ~ 100) has smaller relative AIC at (1,0,0) than (1,0,1)
        const int armaLen = 180;
        var arma21 = new decimal[armaLen];
        var pureAr1HighVar = new decimal[armaLen];
        var ma2Valid = new decimal[armaLen];
        double a1Prev = 0.0, a1Prev2 = 0.0, ePrev = 0.0, ePrev2 = 0.0;
        double hPrev = 0.0;
        uint rng = 2463534242u;
        for (int t = 0; t < armaLen; t++)
        {
            // Box-Muller / deterministic pseudo-white-noise via 32-bit xorshift (uncorrelated broadband innovations)
            rng ^= rng << 13;
            rng ^= rng >> 17;
            rng ^= rng << 5;
            double u1 = ((rng & 0xFFFFFFu) + 0.5) / 16777216.0;
            rng ^= rng << 13;
            rng ^= rng >> 17;
            rng ^= rng << 5;
            double u2 = ((rng & 0xFFFFFFu) + 0.5) / 16777216.0;
            double eps = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);

            double yt = (1.5 * a1Prev) - (0.7 * a1Prev2) + eps + (0.25 * ePrev);
            arma21[t] = Math.Round((decimal)yt, 8);
            ma2Valid[t] = Math.Round((decimal)(eps + (1.5 * ePrev) + (0.56 * ePrev2)), 8);
            a1Prev2 = a1Prev;
            a1Prev = yt;
            ePrev2 = ePrev;
            ePrev = eps;

            double epsHigh = 10.0 * eps;
            double ht = (0.65 * hPrev) + epsHigh;
            pureAr1HighVar[t] = Math.Round((decimal)ht, 6);
            hPrev = ht;
        }

        var arrays = new Dictionary<string, IReadOnlyList<decimal>>(StringComparer.Ordinal)
        {
            ["j1"] = j1,
            ["j2"] = j2,
            ["arma21"] = arma21,
            ["ar1hv"] = pureAr1HighVar,
            ["ma2"] = ma2Valid,
        };

        var jBase = EvalBatchOne("johansen_test(j1, j2)", arrays);
        var jTiny = EvalBatchOne("johansen_test(arr_scale(j1, 1e-5), arr_scale(j2, 1e-5))", arrays);
        Assert.True(jBase.IsOk && jTiny.IsOk);
        for (int i = 0; i < 3; i++)
        {
            decimal scale = Math.Max(1m, Math.Abs(jBase.ArrayValues![i]));
            Assert.InRange(Math.Abs(jBase.ArrayValues[i] - jTiny.ArrayValues![i]), 0m, 1e-7m * scale);
        }

        var arima21Fit = EvalBatchOne("arima_fit(arma21, 2, 0, 1)", arrays);
        Assert.True(arima21Fit.IsOk, arima21Fit.Error?.Message);
        Assert.Equal(5, arima21Fit.ArrayValues!.Count);
        Assert.True(arima21Fit.ArrayValues[0] > 1.0m, $"Expected AR1 > 1.0 without 0.98 clamp, got {arima21Fit.ArrayValues[0]}");

        // Q2: Both q == 0 and q > 0 strictly apply common differenced-series multiplier n = w - d in AIC
        var aicAr1 = EvalBatchOne("arima_fit(ar1hv, 1, 0, 0)", arrays);
        var aicArma11 = EvalBatchOne("arima_fit(ar1hv, 1, 0, 1)", arrays);
        var aicArma22D1 = EvalBatchOne("arima_fit(ar1hv, 2, 1, 2)", arrays);
        Assert.True(aicAr1.IsOk && aicArma11.IsOk && aicArma22D1.IsOk);

        double expectedAicAr1 = (armaLen * Math.Log(Math.Max((double)aicAr1.ArrayValues![1], 1e-16))) + (2.0 * (1 + 0 + 2));
        double expectedAicArma11 = (armaLen * Math.Log(Math.Max((double)aicArma11.ArrayValues![2], 1e-16))) + (2.0 * (1 + 1 + 2));
        double expectedAicArma22D1 = ((armaLen - 1) * Math.Log(Math.Max((double)aicArma22D1.ArrayValues![4], 1e-16))) + (2.0 * (2 + 2 + 2));
        Assert.InRange(Math.Abs((double)aicAr1.ArrayValues[2] - expectedAicAr1), 0.0, 1e-9);
        Assert.InRange(Math.Abs((double)aicArma11.ArrayValues[3] - expectedAicArma11), 0.0, 1e-9);
        Assert.InRange(Math.Abs((double)aicArma22D1.ArrayValues[5] - expectedAicArma22D1), 0.0, 1e-9);

        var ma2Fit = EvalBatchOne("arima_fit(ma2, 0, 0, 2)", arrays);
        Assert.True(ma2Fit.IsOk, ma2Fit.Error?.Message);
        Assert.Equal(4, ma2Fit.ArrayValues!.Count);
        Assert.True(ma2Fit.ArrayValues[0] > 1.0m, $"Expected MA1 > 1.0 without 0.98 clamp, got {ma2Fit.ArrayValues[0]}");
    }

    [Fact]
    public void ArimaForecast_MatchesArimaFitParityAnalyticalPsiAndQuadraticDrift()
    {
        const int nLen = 60;
        var arimaSeries = new decimal[nLen];
        var quadSeries = new decimal[nLen];
        arimaSeries[0] = 100m;
        for (int t = 1; t < nLen; t++)
        {
            arimaSeries[t] = arimaSeries[t - 1] + 0.5m + 0.8m * (decimal)Math.Sin(0.7 * t) + 0.3m * (decimal)Math.Cos(1.1 * t);
        }

        // Quadratic trend + small oscillation so 2nd difference has non-zero drift mu > 0 and non-zero variance.
        for (int t = 0; t < nLen; t++)
        {
            quadSeries[t] = 10m + (0.5m * t) + (0.15m * t * t) + 0.1m * (decimal)Math.Sin(0.8 * t);
        }

        var arrays = new Dictionary<string, IReadOnlyList<decimal>>(StringComparer.Ordinal)
        {
            ["s"] = arimaSeries,
            ["quad"] = quadSeries,
            ["lin"] = Enumerable.Range(1, 30).Select(i => (decimal)i).ToArray(),
        };

        // 1. q = 0 parity with arima_fit for d = 0, 1, 2 (1-step forecast and SE(1))
        for (int d = 0; d <= 2; d++)
        {
            const int p = 2;
            var fitRes = EvalBatchOne($"arima_fit(s, {p}, {d}, 0)", arrays);
            var fcRes = EvalBatchOne($"arima_forecast(s, {p}, {d}, 0, 3)", arrays);
            Assert.True(fitRes.IsOk && fcRes.IsOk, fitRes.Error?.Message ?? fcRes.Error?.Message);
            Assert.Equal(6, fcRes.ArrayValues!.Count);

            double phi1 = (double)fitRes.ArrayValues![0];
            double phi2 = (double)fitRes.ArrayValues[1];
            double sigma2 = (double)fitRes.ArrayValues[2];

            int nDiff = nLen - d;
            var diff = new double[nDiff];
            var raw = arimaSeries.Select(v => (double)v).ToArray();
            ArimaMath.Difference(raw, diff, d);
            double mu = diff.Average();
            double yTildeLast = diff[nDiff - 1] - mu;
            double yTildePrev = diff[nDiff - 2] - mu;
            double expectedDiff1 = mu + (phi1 * yTildeLast) + (phi2 * yTildePrev);
            double expectedY1 = d switch
            {
                0 => expectedDiff1,
                1 => raw[^1] + expectedDiff1,
                2 => (2.0 * raw[^1]) - raw[^2] + expectedDiff1,
                _ => expectedDiff1,
            };

            Assert.InRange(Math.Abs((double)fcRes.ArrayValues[0] - expectedY1), 0.0, 1e-10);
            Assert.InRange(Math.Abs((double)fcRes.ArrayValues[3] - Math.Sqrt(sigma2)), 0.0, 1e-11);
        }

        // 2. Analytical psi_1, psi_2 and SE(1), SE(2), SE(3) from Phi*(B) = (1 - B)^d * Phi(B) for d = 0, 1, 2
        // 2a. d = 0, ARMA(1, 1): phi*(1) = phi1, psi_0 = 1, psi_1 = phi1 + theta1, psi_2 = phi1 * psi_1
        {
            var fit = EvalBatchOne("arima_fit(s, 1, 0, 1)", arrays);
            var fc = EvalBatchOne("arima_forecast(s, 1, 0, 1, 3)", arrays);
            Assert.True(fit.IsOk && fc.IsOk, fit.Error?.Message ?? fc.Error?.Message);
            double phi1 = (double)fit.ArrayValues![0];
            double theta1 = (double)fit.ArrayValues[1];
            double sigma2 = (double)fit.ArrayValues[2];
            double psi1 = phi1 + theta1;
            double psi2 = phi1 * psi1;
            Assert.InRange(Math.Abs((double)fc.ArrayValues![3] - Math.Sqrt(sigma2)), 0.0, 1e-11);
            Assert.InRange(Math.Abs((double)fc.ArrayValues[4] - Math.Sqrt(sigma2 * (1.0 + psi1 * psi1))), 0.0, 1e-10);
            Assert.InRange(Math.Abs((double)fc.ArrayValues[5] - Math.Sqrt(sigma2 * (1.0 + psi1 * psi1 + psi2 * psi2))), 0.0, 1e-10);
        }

        // 2b. d = 1, ARIMA(1, 1, 1): Phi*(B) = (1-B)(1-phi1*B) => phiStar = [1+phi1, -phi1]
        {
            var fit = EvalBatchOne("arima_fit(s, 1, 1, 1)", arrays);
            var fc = EvalBatchOne("arima_forecast(s, 1, 1, 1, 3)", arrays);
            Assert.True(fit.IsOk && fc.IsOk, fit.Error?.Message ?? fc.Error?.Message);
            double phi1 = (double)fit.ArrayValues![0];
            double theta1 = (double)fit.ArrayValues[1];
            double sigma2 = (double)fit.ArrayValues[2];
            double phiStar1 = 1.0 + phi1;
            double phiStar2 = -phi1;
            double psi1 = theta1 + phiStar1;
            double psi2 = (phiStar1 * psi1) + phiStar2;
            Assert.InRange(Math.Abs((double)fc.ArrayValues![3] - Math.Sqrt(sigma2)), 0.0, 1e-11);
            Assert.InRange(Math.Abs((double)fc.ArrayValues[4] - Math.Sqrt(sigma2 * (1.0 + psi1 * psi1))), 0.0, 1e-10);
            Assert.InRange(Math.Abs((double)fc.ArrayValues[5] - Math.Sqrt(sigma2 * (1.0 + psi1 * psi1 + psi2 * psi2))), 0.0, 1e-10);
        }

        // 2c. d = 2, ARIMA(1, 2, 0): Phi*(B) = (1-B)^2(1-phi1*B) => phiStar = [2+phi1, -1-2*phi1, phi1]
        {
            var fit = EvalBatchOne("arima_fit(s, 1, 2, 0)", arrays);
            var fc = EvalBatchOne("arima_forecast(s, 1, 2, 0, 3)", arrays);
            Assert.True(fit.IsOk && fc.IsOk, fit.Error?.Message ?? fc.Error?.Message);
            double phi1 = (double)fit.ArrayValues![0];
            double sigma2 = (double)fit.ArrayValues[1];
            double phiStar1 = 2.0 + phi1;
            double phiStar2 = -1.0 - (2.0 * phi1);
            double psi1 = phiStar1;
            double psi2 = (phiStar1 * psi1) + phiStar2;
            Assert.InRange(Math.Abs((double)fc.ArrayValues![3] - Math.Sqrt(sigma2)), 0.0, 1e-11);
            Assert.InRange(Math.Abs((double)fc.ArrayValues[4] - Math.Sqrt(sigma2 * (1.0 + psi1 * psi1))), 0.0, 1e-10);
            Assert.InRange(Math.Abs((double)fc.ArrayValues[5] - Math.Sqrt(sigma2 * (1.0 + psi1 * psi1 + psi2 * psi2))), 0.0, 1e-10);
        }

        // 3. d = 2 quadratic trend restoration with mu != 0
        {
            var fcQuad = EvalBatchOne("arima_forecast(quad, 1, 2, 0, 4)", arrays);
            Assert.True(fcQuad.IsOk, fcQuad.Error?.Message);
            Assert.Equal(8, fcQuad.ArrayValues!.Count);
            decimal yPrev = quadSeries[^1];
            decimal prevStep = fcQuad.ArrayValues[0] - yPrev;
            Assert.True(prevStep > 0m);
            for (int h = 1; h < 4; h++)
            {
                decimal step = fcQuad.ArrayValues[h] - fcQuad.ArrayValues[h - 1];
                Assert.True(step > prevStep, $"Expected accelerating quadratic step at h={h + 1}: {step} > {prevStep}");
                prevStep = step;
            }
            // Monotonically increasing SE(h)
            for (int h = 1; h < 4; h++)
            {
                Assert.True(fcQuad.ArrayValues[4 + h] > fcQuad.ArrayValues[4 + h - 1]);
            }
        }

        // 4. Rolling window leak-zero forecast & Z-score signal via arr_rolling_map
        var rollPred = EvalBatchOne(
            "arr_rolling_map(arima_forecast(w, 1, 1, 1, 1)[0], w, s, 30)",
            arrays,
            new CalculatorLimits { MaxExecutionOps = 50_000 });
        Assert.True(rollPred.IsOk, rollPred.Error?.Message);
        Assert.Equal(nLen - 30 + 1, rollPred.ArrayValues!.Count);

        var rollZ = EvalBatchOne(
            "arr_rolling_map((arima_forecast(w, 1, 0, 0, 1)[0] - w[29]) / arima_forecast(w, 1, 0, 0, 1)[1], w, s, 30)",
            arrays,
            new CalculatorLimits { MaxExecutionOps = 50_000 });
        Assert.True(rollZ.IsOk, rollZ.Error?.Message);
        Assert.Equal(nLen - 30 + 1, rollZ.ArrayValues!.Count);

        // 5. Boundary & error contracts
        Assert.Equal(CalculatorErrorCode.DomainError, EvalBatchOne("arima_forecast(s, 1, 1, 1, 0)", arrays).Error!.Code);
        Assert.Equal(CalculatorErrorCode.DomainError, EvalBatchOne("arima_forecast(s, 1, 1, 1, -1)", arrays).Error!.Code);
        Assert.Equal(CalculatorErrorCode.DomainError, EvalBatchOne("arima_forecast(s, 1, 1, 1, 1.5)", arrays).Error!.Code);
        Assert.Equal(CalculatorErrorCode.DomainError, EvalBatchOne("arima_forecast(s, 1, 1, 1, 65)", arrays).Error!.Code);
        Assert.Equal(CalculatorErrorCode.DomainError, EvalBatchOne("arima_forecast(s, 0, 1, 0, 1)", arrays).Error!.Code);
        Assert.Equal(CalculatorErrorCode.DomainError, EvalBatchOne("arima_forecast(arr_slice(s, 0, 15), 1, 0, 0, 1)", arrays).Error!.Code);
        Assert.Equal(CalculatorErrorCode.DivisionByZero, EvalBatchOne("arima_forecast(lin, 1, 1, 0, 1)", arrays).Error!.Code);
        Assert.Equal(CalculatorErrorCode.ArithmeticOverflow, EvalBatchOne("arima_forecast(arr_scale(s, 7e25), 1, 2, 0, 10)", arrays).Error!.Code);

        // Max horizon 64 succeeds
        var maxH = EvalBatchOne("arima_forecast(s, 1, 1, 0, 64)", arrays);
        Assert.True(maxH.IsOk, maxH.Error?.Message);
        Assert.Equal(128, maxH.ArrayValues!.Count);

        // Direct ArimaMath.TryForecastArima checks: NaN -> NonFiniteResult, Infinity -> ArithmeticOverflow
        var fcBuf = new double[16];
        var seBuf = new double[16];
        var nanInput = arimaSeries.Select(v => (double)v).ToArray();
        nanInput[5] = double.NaN;
        Assert.Equal(MathErrorCode.NonFiniteResult, ArimaMath.TryForecastArima(nanInput, 1, 0, 0, 2, fcBuf, seBuf));

        var infInput = arimaSeries.Select(v => (double)v).ToArray();
        infInput[5] = double.PositiveInfinity;
        Assert.Equal(MathErrorCode.ArithmeticOverflow, ArimaMath.TryForecastArima(infInput, 1, 0, 0, 2, fcBuf, seBuf));

        // Direct API horizon cap (MaxPublicForecastHorizon = 1024, allowing Chart Projection's 100 while guarding OOM/DoS)
        Assert.Equal(MathErrorCode.DomainError, ArimaMath.TryForecastArima(arimaSeries.Select(v => (double)v).ToArray(), 1, 0, 0, 1025, new double[1025], new double[1025]));

        // With maxAbs normalization, 1e155 avoids premature sigma2 squaring overflow in TryForecastArima SE(h),
        // while TryFitArimaParameters (returning variance sigma2 = maxAbs^2 * sigma2U) and 1e306 in TryForecastArima cleanly return ArithmeticOverflow.
        var midHugeInput = arimaSeries.Select(v => (double)v * 1e155).ToArray();
        Assert.Equal(MathErrorCode.None, ArimaMath.TryForecastArima(midHugeInput, 1, 2, 0, 16, fcBuf, seBuf));
        Assert.Equal(MathErrorCode.ArithmeticOverflow, ArimaMath.TryFitArimaParameters(midHugeInput, 1, 2, 0, new double[1], Span<double>.Empty, out _, out _));

        var maxHugeInput = arimaSeries.Select(v => (double)v * 1e306).ToArray();
        Assert.Equal(MathErrorCode.ArithmeticOverflow, ArimaMath.TryForecastArima(maxHugeInput, 1, 2, 0, 16, fcBuf, seBuf));
    }

    [Fact]
    public void ArimaHardening_ScaleInvarianceLogAicQrStabilityGoldenAndRoundingContracts()
    {
        const int nLen = 60;
        var baseSeries = new decimal[nLen];
        baseSeries[0] = 0m;
        for (int t = 1; t < nLen; t++)
        {
            baseSeries[t] = (0.85m * baseSeries[t - 1]) + (decimal)(1.2 * Math.Sin(0.6 * t) + 0.4 * Math.Cos(1.3 * t));
        }

        // 1. Scale invariance across 1e-7 (inside old 1e-10..1e-6 dead zone), 1, and 1e4 for both q = 0 and q >= 1
        double[] scales = [1e-7, 1.0, 1e4];
        foreach ((int p, int d, int q) in new[] { (2, 1, 0), (2, 1, 1) })
        {
            var fit1 = EvalBatchOne($"arima_fit(b, {p}, {d}, {q})", new Dictionary<string, IReadOnlyList<decimal>>(StringComparer.Ordinal) { ["b"] = baseSeries });
            var fc1 = EvalBatchOne($"arima_forecast(b, {p}, {d}, {q}, 4)", new Dictionary<string, IReadOnlyList<decimal>>(StringComparer.Ordinal) { ["b"] = baseSeries });
            Assert.True(fit1.IsOk && fc1.IsOk, fit1.Error?.Message ?? fc1.Error?.Message);

            int nDiff = nLen - d;
            foreach (double c in scales)
            {
                decimal cDec = (decimal)c;
                var scaledSeries = baseSeries.Select(v => v * cDec).ToArray();
                var arr = new Dictionary<string, IReadOnlyList<decimal>>(StringComparer.Ordinal) { ["b"] = scaledSeries };

                var fitC = EvalBatchOne($"arima_fit(b, {p}, {d}, {q})", arr);
                var fcC = EvalBatchOne($"arima_forecast(b, {p}, {d}, {q}, 4)", arr);
                Assert.True(fitC.IsOk && fcC.IsOk, $"Scale {c} failed: {fitC.Error?.Message ?? fcC.Error?.Message}");

                // Point forecasts and SE scale linearly with c
                for (int h = 0; h < 4; h++)
                {
                    double f1 = (double)fc1.ArrayValues![h];
                    double fcScaledBack = (double)fcC.ArrayValues![h] / c;
                    Assert.InRange(Math.Abs(fcScaledBack - f1), 0.0, 1e-9 * Math.Max(1.0, Math.Abs(f1)));

                    double s1 = (double)fc1.ArrayValues[4 + h];
                    double seScaledBack = (double)fcC.ArrayValues[4 + h] / c;
                    Assert.InRange(Math.Abs(seScaledBack - s1), 0.0, 1e-9 * Math.Max(1e-12, Math.Abs(s1)));
                }

                // Parameters (phi, theta) remain scale-invariant; sigma2 scales as c^2; log-space AIC satisfies AIC(c*x) - AIC(x) == 2*n*ln(c)
                int paramCount = p + q;
                for (int k = 0; k < paramCount; k++)
                {
                    Assert.InRange(Math.Abs((double)fitC.ArrayValues![k] - (double)fit1.ArrayValues![k]), 0.0, 1e-9);
                }

                double sig1 = (double)fit1.ArrayValues![paramCount];
                double sigC = (double)fitC.ArrayValues![paramCount];
                Assert.InRange(Math.Abs((sigC / (c * c)) - sig1), 0.0, 1e-8 * Math.Max(1e-16, sig1));

                double aic1 = (double)fit1.ArrayValues[paramCount + 1];
                double aicC = (double)fitC.ArrayValues[paramCount + 1];
                double expectedAicDiff = 2.0 * nDiff * Math.Log(c);
                Assert.InRange(Math.Abs((aicC - aic1) - expectedAicDiff), 0.0, 1e-8);
            }
        }

        // 2. High-collinearity ARMA(3, 2) solved via SmallMatrixMath.TryHouseholderQr + non-decreasing SE(h)
        var persistent = new decimal[100];
        persistent[0] = 10m;
        for (int t = 1; t < persistent.Length; t++)
        {
            persistent[t] = (0.94m * persistent[t - 1]) + (decimal)(0.5 * Math.Sin(0.45 * t) + 0.15 * Math.Cos(1.1 * t));
        }

        var qrFc = EvalBatchOne("arima_forecast(p, 3, 0, 2, 6)", new Dictionary<string, IReadOnlyList<decimal>>(StringComparer.Ordinal) { ["p"] = persistent });
        Assert.True(qrFc.IsOk, qrFc.Error?.Message);
        for (int h = 1; h < 6; h++)
        {
            Assert.True(qrFc.ArrayValues![6 + h] >= qrFc.ArrayValues[6 + h - 1]);
        }

        // 3. Analytical Yule-Walker (R ar.yw biased 1/n parity) golden reference on alternating series w_t = (-1)^t, N = 24
        var alt = Enumerable.Range(0, 24).Select(t => t % 2 == 0 ? 1m : -1m).ToArray();
        var altArr = new Dictionary<string, IReadOnlyList<decimal>>(StringComparer.Ordinal) { ["alt"] = alt };
        var altFit = EvalBatchOne("arima_fit(alt, 1, 0, 0)", altArr);
        var altFc = EvalBatchOne("arima_forecast(alt, 1, 0, 0, 3)", altArr);
        Assert.True(altFit.IsOk && altFc.IsOk);

        const double expectedPhi1 = -23.0 / 24.0;
        const double expectedSigma2 = 1.0 - (expectedPhi1 * expectedPhi1);
        Assert.InRange(Math.Abs((double)altFit.ArrayValues![0] - expectedPhi1), 0.0, 1e-12);
        Assert.InRange(Math.Abs((double)altFit.ArrayValues[1] - expectedSigma2), 0.0, 1e-12);
        // alt[23] = -1 => h=1: expectedPhi1 * (-1) = 23/24; h=2: phi1^2 * (-1); h=3: phi1^3 * (-1)
        Assert.InRange(Math.Abs((double)altFc.ArrayValues![0] - (-expectedPhi1)), 0.0, 1e-12);
        Assert.InRange(Math.Abs((double)altFc.ArrayValues[1] - (-(expectedPhi1 * expectedPhi1))), 0.0, 1e-12);
        Assert.InRange(Math.Abs((double)altFc.ArrayValues[2] - (-(expectedPhi1 * expectedPhi1 * expectedPhi1))), 0.0, 1e-12);
        Assert.InRange(Math.Abs((double)altFc.ArrayValues[3] - Math.Sqrt(expectedSigma2)), 0.0, 1e-12);
        Assert.InRange(Math.Abs((double)altFc.ArrayValues[4] - Math.Sqrt(expectedSigma2 * (1.0 + expectedPhi1 * expectedPhi1))), 0.0, 1e-12);

        // 4. Period-4 oscillating series with near-zero phi_1 verifying weak monotonicity SE(h+1) >= SE(h) and double->decimal rounding accuracy
        var p4 = Enumerable.Range(0, 40).Select(t => (decimal)(new[] { 1.0, 0.2, -1.0, -0.2 }[t % 4])).ToArray();
        double[] rawP4 = p4.Select(v => (double)v).ToArray();
        double[] apiFc = new double[4];
        double[] apiSe = new double[4];
        Assert.Equal(MathErrorCode.None, ArimaMath.TryForecastArima(rawP4, 2, 0, 0, 4, apiFc, apiSe));
        var dslP4 = EvalBatchOne("arima_forecast(p4, 2, 0, 0, 4)", new Dictionary<string, IReadOnlyList<decimal>>(StringComparer.Ordinal) { ["p4"] = p4 });
        Assert.True(dslP4.IsOk);
        for (int h = 0; h < 4; h++)
        {
            Assert.InRange(Math.Abs(apiFc[h] - (double)dslP4.ArrayValues![h]), 0.0, 1e-12);
            Assert.InRange(Math.Abs(apiSe[h] - (double)dslP4.ArrayValues[4 + h]), 0.0, 1e-12);
            if (h > 0)
            {
                Assert.True(dslP4.ArrayValues[4 + h] >= dslP4.ArrayValues[4 + h - 1]);
            }
        }
    }

    [Fact]
    public void NamespaceSeparation_AllowsSameNameFunctionsVariablesLoweredArraysAndBoundDummies()
    {
        var scalars = new Dictionary<string, decimal>(StringComparer.Ordinal)
        {
            ["step"] = 41m,
            ["x"] = 5m,
        };
        var arrays = new Dictionary<string, IReadOnlyList<decimal>>(StringComparer.Ordinal)
        {
            ["sum"] = new[] { 2m, 4m, 6m, 8m },
            ["stdev_s"] = new[] { 2m, 4m, 4m, 4m, 5m, 5m, 7m, 9m },
        };
        var items = new[]
        {
            new ExpressionCalculator.CalculatorBatchItem("arrAndSubscript", "sum(sum) + sum[0]"),
            new ExpressionCalculator.CalculatorBatchItem("scalarAndCall", "step + step(x)"),
            new ExpressionCalculator.CalculatorBatchItem("loweredSameName", "stdev_s(stdev_s)"),
            new ExpressionCalculator.CalculatorBatchItem("rollingShadow", "arr_rolling_map(sum(sum) / arr_len(sum), sum, sum, 3)"),
            new ExpressionCalculator.CalculatorBatchItem("stateScanShadow", "arr_scan_state([sum + min, max + sum], [sum, max], min, [1, 2, 3], [0, 1], 0)"),
        };

        var results = ExpressionCalculator.EvaluateBatch(scalars, arrays, items, CalculatorLimits.Default);
        Assert.All(results, r => Assert.True(r.IsOk, r.Error?.Message));

        Assert.Equal(22m, results[0].ScalarValue); // 20 + 2
        Assert.Equal(42m, results[1].ScalarValue); // 41 + 1
        Assert.Equal(EvalBatchOne("stdev_s([2, 4, 4, 4, 5, 5, 7, 9])").ScalarValue, results[2].ScalarValue);
        Assert.Equal(new[] { 4m, 6m }, results[3].ArrayValues); // avg([2,4,6])=4, avg([4,6,8])=6
        Assert.Equal(new[] { 1m, 3m, 6m }, results[4].ArrayValues);
    }

    [Fact]
    public void MultivariateDistributions_MvNormLogPdfAndCopulaGaussCdf_MatchAnalyticalTruth()
    {
        // mvnorm_logpdf([1, 2], [1, 2], diag(2, 3)) = -0.5 * (2*ln(2*pi) + ln(6))
        var mvnDiag = EvalBatchOne("mvnorm_logpdf([1, 2], [1, 2], [2, 0, 0, 3])");
        Assert.True(mvnDiag.IsOk, mvnDiag.Error?.Message);
        decimal expDiag = (decimal)(-0.5 * ((2.0 * Math.Log(2.0 * Math.PI)) + Math.Log(6.0)));
        Assert.InRange(Math.Abs(mvnDiag.ScalarValue - expDiag), 0m, 1e-11m);

        Assert.Equal(CalculatorErrorCode.DomainError, EvalBatchOne("mvnorm_logpdf([1, 2], [1, 2], [1, 2, 2, 1])").Error!.Code);
        Assert.Equal(CalculatorErrorCode.DomainError, EvalBatchOne("mvnorm_logpdf([1, 2], [1, 2], [1, 0.5, 0, 1])").Error!.Code);
        Assert.Equal(CalculatorErrorCode.DomainError, EvalBatchOne("mvnorm_logpdf([], [], [])").Error!.Code);
        Assert.Equal(CalculatorErrorCode.DimensionMismatch, EvalBatchOne("mvnorm_logpdf([1, 2], [1], [1, 0, 0, 1])").Error!.Code);

        // copula_gauss_cdf(0.5, 0.5, 0.5) = 1/4 + asin(0.5)/(2*pi) = 1/3
        var copHalf = EvalBatchOne("copula_gauss_cdf(0.5, 0.5, 0.5)");
        Assert.True(copHalf.IsOk, copHalf.Error?.Message);
        Assert.InRange(Math.Abs(copHalf.ScalarValue - (1m / 3m)), 0m, 1e-12m);

        Assert.Equal(0m, EvalBatchOne("copula_gauss_cdf(0, 0.4, 0.5)").ScalarValue);
        Assert.Equal(0.4m, EvalBatchOne("copula_gauss_cdf(1, 0.4, 0.5)").ScalarValue);
        Assert.Equal(0.25m, EvalBatchOne("copula_gauss_cdf(0.5, 0.5, 0)").ScalarValue);
        Assert.Equal(CalculatorErrorCode.DomainError, EvalBatchOne("copula_gauss_cdf(0, 0.4, 1)").Error!.Code);
        Assert.Equal(CalculatorErrorCode.DomainError, EvalBatchOne("copula_gauss_cdf(-0.1, 0.4, 0.2)").Error!.Code);
    }

    // ---- Batch 4: Dirty Bond Price, Dividend Binomial Option, YearFrac Basis 4, & Parallel Determinism ----

    [Fact]
    public void FinanceExtendedAndYearFracBasis4_VerifyDirtyBondOptionDivAnd30E360()
    {
        // bond_price_dirty matches bond_price bit-for-bit when daysSinceCoupon == 0
        var clean = EvalBatchOne("bond_price(1000, 0.06, 0.05, 5, 2)");
        var dirty0 = EvalBatchOne("bond_price_dirty(1000, 0.06, 0.05, 0, 180, 10, 2)");
        Assert.True(clean.IsOk && dirty0.IsOk);
        Assert.Equal(clean.ScalarValue, dirty0.ScalarValue);

        // Accepts fractional/real daysSinceCoupon and daysInPeriod (matching bond_accrued_interest)
        var dirtyFrac = EvalBatchOne("bond_price_dirty(1000, 0.06, 0.05, 45.5, 182.5, 10, 2)");
        Assert.True(dirtyFrac.IsOk, dirtyFrac.Error?.Message);
        double expectedDirty = (double)clean.ScalarValue * Math.Pow(1.0 + 0.025, 45.5 / 182.5);
        Assert.InRange(Math.Abs(dirtyFrac.ScalarValue - (decimal)expectedDirty), 0m, 1e-10m);

        Assert.Equal(CalculatorErrorCode.DomainError, EvalBatchOne("bond_price_dirty(1000, 0.06, 0.05, 180, 180, 10, 2)").Error!.Code);
        Assert.Equal(CalculatorErrorCode.DomainError, EvalBatchOne("bond_price_dirty(1000, 0.06, 0.05, -0.5, 180, 10, 2)").Error!.Code);
        Assert.Equal(CalculatorErrorCode.DomainError, EvalBatchOne("bond_price_dirty(1000, 0.06, 25, 10, 180, 10, 2)").Error!.Code);

        // binomial_option_price_div matches binomial_option_price when q == 0, supports T == 0 intrinsic, and dividend q > 0
        var c0 = EvalBatchOne("binomial_option_price(100, 100, 1, 0.05, 0.2, 1, 0, 50)");
        var cDiv0 = EvalBatchOne("binomial_option_price_div(100, 100, 1, 0.05, 0, 0.2, 50, 0)");
        Assert.True(c0.IsOk && cDiv0.IsOk);
        Assert.Equal(c0.ScalarValue, cDiv0.ScalarValue);

        var amPut0 = EvalBatchOne("binomial_option_price(95, 100, 0.5, 0.04, 0.25, 0, 1, 40)");
        var amPutDiv0 = EvalBatchOne("binomial_option_price_div(95, 100, 0.5, 0.04, 0, 0.25, 40, 3)");
        Assert.Equal(amPut0.ScalarValue, amPutDiv0.ScalarValue);

        var divCall = EvalBatchOne("binomial_option_price_div(100, 100, 1, 0.05, 0.03, 0.2, 50, 0)");
        Assert.True(divCall.IsOk && divCall.ScalarValue < cDiv0.ScalarValue);

        Assert.Equal(12.5m, EvalBatchOne("binomial_option_price_div(112.5, 100, 0, 0.05, 0.03, 0.2, 50, 0)").ScalarValue);
        Assert.Equal(7.5m, EvalBatchOne("binomial_option_price_div(92.5, 100, 0, 0.05, 0.03, 0.2, 50, 3)").ScalarValue);
        Assert.Equal(CalculatorErrorCode.DomainError, EvalBatchOne("binomial_option_price_div(100, 100, 1, 0.05, 0.03, 0.2, 50, 4)").Error!.Code);

        // yearfrac basis = 4 (European 30E/360)
        var yfEom = EvalBatchOne("yearfrac(date_serial(2020, 1, 31), date_serial(2020, 3, 31), 4)");
        Assert.True(yfEom.IsOk, yfEom.Error?.Message);
        Assert.InRange(Math.Abs(yfEom.ScalarValue - (60m / 360m)), 0m, 1e-12m);
    }

    [Fact]
    public void BatchR3Remediation_Quad2dAnisotropicCopulaHighRhoDirtyBondAndCellContinuity()
    {
        // 1. quad2d_gk anisotropic integrand along x only:
        //    (a) steep centered peak under 50k ops (4-way split would exhaust leaf/op limits without bisecting x only)
        var aniso = EvalBatchOne(
            "quad2d_gk(exp(-80 * (x - 0.5)^2), x, y, 0, 1, 0, 1)",
            limits: new CalculatorLimits { MaxExecutionOps = 50_000 });
        Assert.True(aniso.IsOk, aniso.Error?.Message);
        // erf(sqrt(20)) is within 1e-9 of 1.0
        decimal expectedAniso = (decimal)(Math.Sqrt(Math.PI / 80.0) * 1.0);
        Assert.InRange(Math.Abs(aniso.ScalarValue - expectedAniso), 0m, 1e-7m);

        //    (b) anisotropic rational peak along x under Default 10k ops
        var anisoDefault = EvalBatchOne("quad2d_gk(1 / (1 + 4 * x * x), x, y, 0, 1, 0, 1)");
        Assert.True(anisoDefault.IsOk, anisoDefault.Error?.Message);
        decimal expectedRational = (decimal)(Math.Atan(2.0) * 0.5);
        Assert.InRange(Math.Abs(anisoDefault.ScalarValue - expectedRational), 0m, 1e-7m);

        // 2. copula_gauss_cdf: high |rho| >= 0.925 accuracy, h == k zero-guard, and Frechet-Hoeffding bounds
        var highPosSymmetric = EvalBatchOne("copula_gauss_cdf(0.1, 0.1, 0.999999)");
        Assert.True(highPosSymmetric.IsOk, highPosSymmetric.Error?.Message);
        Assert.InRange(highPosSymmetric.ScalarValue, 0m, 0.1m);

        var highNeg = EvalBatchOne("copula_gauss_cdf(0.3, 0.7, -0.999999)");
        Assert.True(highNeg.IsOk, highNeg.Error?.Message);
        Assert.InRange(highNeg.ScalarValue, 0m, 0.3m);

        var cop95Pos = EvalBatchOne("copula_gauss_cdf(0.5, 0.5, 0.95)");
        Assert.True(cop95Pos.IsOk, cop95Pos.Error?.Message);
        decimal expCop95Pos = (decimal)(0.25 + (Math.Asin(0.95) / (2.0 * Math.PI)));
        Assert.InRange(Math.Abs(cop95Pos.ScalarValue - expCop95Pos), 0m, 1e-12m);

        var cop95Neg = EvalBatchOne("copula_gauss_cdf(0.5, 0.5, -0.95)");
        Assert.True(cop95Neg.IsOk, cop95Neg.Error?.Message);
        decimal expCop95Neg = (decimal)(0.25 + (Math.Asin(-0.95) / (2.0 * Math.PI)));
        Assert.InRange(Math.Abs(cop95Neg.ScalarValue - expCop95Neg), 0m, 1e-12m);

        // 3. bond_price_dirty: micro-elapsed daysSinceCoupon = 1e-9 preserves full precision without 1-(1-alpha) cancellation
        var basePrice = EvalBatchOne("bond_price(1000, 0.06, 0.05, 5, 2)");
        var microDirty = EvalBatchOne("bond_price_dirty(1000, 0.06, 0.05, 1e-9, 182.5, 10, 2)");
        Assert.True(basePrice.IsOk && microDirty.IsOk);
        double tMicro = (1e-9 / 182.5) * Math.Log(1.025);
        double expectedMicroDiff = (double)basePrice.ScalarValue * (tMicro * (1.0 + (0.5 * tMicro * (1.0 + (tMicro / 3.0)))));
        double actualMicroDiff = (double)(microDirty.ScalarValue - basePrice.ScalarValue);
        Assert.InRange(Math.Abs(microDirty.ScalarValue - basePrice.ScalarValue), 0m, 1e-9m);
        // NarrowDouble rounds ~1044m to 15 significant digits (~1e-11 step)
        Assert.InRange(Math.Abs(actualMicroDiff - expectedMicroDiff), 0.0, 1e-11);

        // 4. interp2d_bicubic: C^1 slope continuity across interior cell seam on non-uniform 5x5 grid
        var arrays5x5 = new Dictionary<string, IReadOnlyList<decimal>>(StringComparer.Ordinal)
        {
            ["g5"] = new[] { 0m, 0.7m, 1.8m, 3.1m, 5.0m },
            ["Z5"] = new decimal[25],
        };
        var z5 = (decimal[])arrays5x5["Z5"];
        decimal[] g5 = (decimal[])arrays5x5["g5"];
        for (int j = 0; j < 5; j++)
        {
            for (int i = 0; i < 5; i++)
            {
                z5[j * 5 + i] = (g5[i] * g5[i]) + (0.5m * g5[i] * g5[j]) - (0.3m * g5[j]);
            }
        }

        const decimal hStep = 1e-6m;
        var batchC1 = new[]
        {
            new ExpressionCalculator.CalculatorBatchItem("fm2", "interp2d_bicubic(g5, g5, Z5, 1.8 - 2e-6, 1.25)"),
            new ExpressionCalculator.CalculatorBatchItem("fm1", "interp2d_bicubic(g5, g5, Z5, 1.8 - 1e-6, 1.25)"),
            new ExpressionCalculator.CalculatorBatchItem("fp1", "interp2d_bicubic(g5, g5, Z5, 1.8 + 1e-6, 1.25)"),
            new ExpressionCalculator.CalculatorBatchItem("fp2", "interp2d_bicubic(g5, g5, Z5, 1.8 + 2e-6, 1.25)"),
        };
        var c1Res = ExpressionCalculator.EvaluateBatch(EmptyScalars, arrays5x5, batchC1, CalculatorLimits.Default);
        Assert.All(c1Res, r => Assert.True(r.IsOk, r.Error?.Message));
        decimal leftDeriv = (c1Res[1].ScalarValue - c1Res[0].ScalarValue) / hStep;
        decimal rightDeriv = (c1Res[3].ScalarValue - c1Res[2].ScalarValue) / hStep;
        Assert.InRange(Math.Abs(leftDeriv - rightDeriv), 0m, 1e-4m);

        // 5. ExpressionCalculator.TryValidateDeclaredIdentifier SSoT check
        Assert.False(ExpressionCalculator.TryValidateDeclaredIdentifier("sum", false, out var rErr));
        Assert.Equal(ExpressionCalculator.DeclaredIdentifierValidationError.ReservedFunctionName, rErr);
        Assert.True(ExpressionCalculator.TryValidateDeclaredIdentifier("sum", true, out _));
        Assert.False(ExpressionCalculator.TryValidateDeclaredIdentifier("nan", true, out var nErr));
        Assert.Equal(ExpressionCalculator.DeclaredIdentifierValidationError.NonFiniteLiteral, nErr);
        Assert.False(ExpressionCalculator.TryValidateDeclaredIdentifier("1bad", true, out var sErr));
        Assert.Equal(ExpressionCalculator.DeclaredIdentifierValidationError.InvalidSyntax, sErr);
    }

    [Fact]
    public void ParallelEvaluation_16Threads_RemainsBitDeterministicAcrossV8Functions()
    {
        var items = new[]
        {
            new ExpressionCalculator.CalculatorBatchItem("scan", "arr_scan_state([s0 + x, s1 + s0], [s0, s1], x, [1, 2, 3, 4], [0, 0], 1)[3]"),
            new ExpressionCalculator.CalculatorBatchItem("roll", "arr_rolling_map(stdev_s(w), w, [2, 4, 6, 8, 10], 3)[1]"),
            new ExpressionCalculator.CalculatorBatchItem("quad", "quad2d_gk(x^2 + y^2, x, y, 0, 1, 0, 1)"),
            new ExpressionCalculator.CalculatorBatchItem("lp", "opt_lp([-5, -4], [1, 1], [4], [0, 0], [2, 3])[0]"),
            new ExpressionCalculator.CalculatorBatchItem("qp", "opt_qp_eq([0.04, 0, 0, 0.09], [0, 0], [1, 1], [1], [0, 0], [1, 1])[0]"),
            new ExpressionCalculator.CalculatorBatchItem("cop", "copula_gauss_cdf(0.5, 0.5, 0.5)"),
            new ExpressionCalculator.CalculatorBatchItem("dirty", "bond_price_dirty(1000, 0.06, 0.05, 45.5, 182.5, 10, 2)"),
            new ExpressionCalculator.CalculatorBatchItem("arima_fc", "arima_forecast([10, 10.5, 11.2, 10.9, 11.8, 12.1, 11.9, 12.6, 13.0, 12.8, 13.5, 13.9, 13.7, 14.3, 14.8, 14.6, 15.2, 15.6, 15.5, 16.1, 16.5, 16.4, 17.0, 17.4], 1, 1, 0, 2)[0]"),
        };

        var baseline = ExpressionCalculator.EvaluateBatch(EmptyScalars, null, items, CalculatorLimits.Default);
        Assert.All(baseline, r => Assert.True(r.IsOk, r.Error?.Message));

        var threadOutputs = new decimal[16][];
        System.Threading.Tasks.Parallel.For(0, 16, tid =>
        {
            var res = ExpressionCalculator.EvaluateBatch(EmptyScalars, null, items, CalculatorLimits.Default);
            threadOutputs[tid] = res.Select(r => r.ScalarValue).ToArray();
        });

        decimal[] expectedScalars = baseline.Select(r => r.ScalarValue).ToArray();
        for (int tid = 0; tid < 16; tid++)
        {
            Assert.Equal(expectedScalars, threadOutputs[tid]);
        }
    }

    [Fact]
    public void ArimaMultiStepProjection_PreservesLegacyCompatAndStabilizesHighOrder()
    {
        double[] s80 = new double[80];
        for (int i = 0; i < s80.Length; i++)
        {
            s80[i] = 100.0 + (0.4 * i) + (1.5 * Math.Sin(0.55 * i)) + (0.6 * Math.Cos(1.1 * i));
        }

        double[] sPers = new double[120];
        sPers[0] = 50.0;
        sPers[1] = 50.5;
        for (int t = 2; t < sPers.Length; t++)
        {
            sPers[t] = (0.72 * sPers[t - 1]) + (0.31 * sPers[t - 2]) - 1.5 + (0.25 * Math.Sin(0.9 * t));
        }

        double[] projTest = new double[80];
        for (int i = 0; i < projTest.Length; i++)
        {
            projTest[i] = 100.0 + (0.5 * i) + (5.0 * Math.Sin(2.0 * Math.PI * i / 12.0));
        }

        void RunSnap(string name, ReadOnlySpan<double> data, int p, int d, int q, int steps, out double[] f, out double[] v, out double inv, out double rsd)
        {
            f = new double[steps];
            v = new double[steps];
            bool ok = ArimaMath.EstimateArimaMultiStepForecast(data, p, d, q, steps, f, v, out inv, out rsd);
            Assert.True(ok, $"{name} returned false");
            _output.WriteLine(string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{name}: ok={ok} | f0={f[0]:G12} f_last={f[^1]:G12} | v0={v[0]:G12} v_last={v[^1]:G12} | inv={inv:G12} | rsd={rsd:G12}"));
            for (int h = 0; h < steps; h++)
            {
                Assert.True(double.IsFinite(f[h]) && double.IsFinite(v[h]) && v[h] >= 0.0, $"{name} step {h}");
                if (h > 0)
                {
                    Assert.True(v[h] >= v[h - 1], $"{name} variance monotonicity at h={h}");
                }
            }
        }

        // 1. Well-conditioned AR & ARMA regression checks against pre-change baseline
        RunSnap("well_110_h10", s80, 1, 1, 0, 10, out var f110, out var v110, out var inv110, out _);
        Assert.InRange(Math.Abs(f110[0] - 132.334891891), 0.0, 1e-6);
        Assert.InRange(Math.Abs(f110[9] - 137.501405143), 0.0, 1e-6);
        Assert.InRange(Math.Abs(v110[0] - 0.27920853223), 0.0, 1e-9);
        Assert.InRange(Math.Abs(v110[9] - 18.2963837802), 0.0, 1e-7);
        Assert.InRange(Math.Abs(inv110 - 0.27920853223), 0.0, 1e-9);

        RunSnap("well_210_h10", s80, 2, 1, 0, 10, out var f210, out var v210, out var inv210, out _);
        Assert.InRange(Math.Abs(f210[0] - 132.625297072), 0.0, 1e-6);
        Assert.InRange(Math.Abs(f210[9] - 135.744010144), 0.0, 1e-6);
        Assert.InRange(Math.Abs(v210[0] - 0.109854375793), 0.0, 1e-9);
        Assert.InRange(Math.Abs(v210[9] - 4.13170952639), 0.0, 1e-7);

        RunSnap("well_111_h10", s80, 1, 1, 1, 10, out var f111, out var v111, out _, out _);
        Assert.InRange(Math.Abs(f111[0] - 132.694483271), 0.0, 1e-6);
        Assert.InRange(Math.Abs(f111[9] - 137.411824429), 0.0, 1e-6);
        Assert.InRange(Math.Abs(v111[0] - 0.189544889031), 0.0, 1e-8);
        Assert.InRange(Math.Abs(v111[9] - 24.0760218492), 0.0, 1e-6);

        // 2. Legacy outer-contract compatibility: ARIMA(0,d,0) and short window (w=22 < minWindow=25)
        RunSnap("rw_010_h10", s80, 0, 1, 0, 10, out var f010, out var v010, out var inv010, out _);
        Assert.InRange(Math.Abs(f010[0] - 131.516095635), 0.0, 1e-9);
        Assert.InRange(Math.Abs(f010[9] - 134.994156394), 0.0, 1e-9);
        Assert.InRange(Math.Abs(v010[0] - 0.519847119397), 0.0, 1e-11);
        Assert.InRange(Math.Abs(v010[9] - 5.19847119397), 0.0, 1e-10);
        Assert.InRange(Math.Abs(inv010 - 0.519847119397), 0.0, 1e-11);

        RunSnap("short22_111_h5", s80.AsSpan(0, 22), 1, 1, 1, 5, out var fs22, out var vs22, out var invs22, out _);
        Assert.InRange(Math.Abs(fs22[0] - 107.889761386), 0.0, 1e-9);
        Assert.InRange(Math.Abs(fs22[4] - 108.96792132), 0.0, 1e-8);
        Assert.InRange(Math.Abs(vs22[0] - 0.317818440355), 0.0, 1e-11);
        Assert.InRange(Math.Abs(vs22[4] - 3.99248579663), 0.0, 1e-10);
        Assert.InRange(Math.Abs(invs22 - 0.317818440355), 0.0, 1e-11);

        // 3. Persistent high-order ARIMA stabilization (eliminating exponential blowup seen in element-wise clamp)
        RunSnap("pers_201_h40", sPers, 2, 0, 1, 40, out _, out _, out _, out _);
        RunSnap("pers_302_h40", sPers, 3, 0, 2, 40, out var fp302, out var vp302, out _, out _);
        Assert.All(fp302, val => Assert.True(Math.Abs(val) < 200.0, $"Expected bounded forecast, got {val}"));
        Assert.InRange(vp302[39], 0.0, 10_000.0);

        // 4. DSL arima_forecast <-> EstimateArimaMultiStepForecast SSoT parity on projTest
        RunSnap("projtest_111_h10", projTest, 1, 1, 1, 10, out _, out _, out _, out _);
        RunSnap("projtest_211_h10", projTest, 2, 1, 1, 10, out var fp211, out var vp211, out _, out _);

        decimal[] projDec = projTest.Select(v => (decimal)v).ToArray();
        var arrays = new Dictionary<string, IReadOnlyList<decimal>>(StringComparer.Ordinal) { ["x"] = projDec };
        var dslRes = EvalBatchOne("arima_forecast(x, 2, 1, 1, 10)", arrays);
        Assert.True(dslRes.IsOk && dslRes.ArrayValues is { Count: 20 }, dslRes.Error?.Message);
        for (int h = 0; h < 10; h++)
        {
            Assert.InRange(Math.Abs(fp211[h] - (double)dslRes.ArrayValues![h]), 0.0, 1e-11);
            Assert.InRange(Math.Abs(Math.Sqrt(vp211[h]) - (double)dslRes.ArrayValues[10 + h]), 0.0, 1e-11);
        }
    }
}
