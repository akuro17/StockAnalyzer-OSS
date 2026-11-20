using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Calculator;
using StockAnalyzer.Core.MathUtils;
using Xunit;

namespace StockAnalyzer.Core.Tests.Calculator;

public class CalculatorDslArrayAndRegressionV4Tests
{
    private static readonly IReadOnlyDictionary<string, decimal> EmptyScalars =
        new Dictionary<string, decimal>(0, StringComparer.Ordinal);

    [Fact]
    public void DslArrayArenaHandlesLimitsCompactionAndRollback()
    {
        var arena = new DslArrayArena();
        try
        {
            Assert.False(arena.TryAllocate(-1, out _, out _));
            Assert.False(arena.TryAllocate(DslArrayArena.MaxArrayLength + 1, out _, out _));

            Assert.True(arena.TryAllocate(0, out DslArrayHandle empty, out _));
            Assert.Equal(0, empty.Length);
            Assert.True(arena.GetSlice(empty).IsEmpty);

            int checkpoint = arena.Cursor;
            Assert.True(arena.TryAllocate(4, out DslArrayHandle transient, out Span<decimal> s1));
            s1[0] = 99m;
            Assert.True(arena.TryAllocate(3, out DslArrayHandle keep, out Span<decimal> s2));
            s2[0] = 10m;
            s2[1] = 20m;
            s2[2] = 30m;

            DslArrayHandle compacted = arena.CompactResult(checkpoint, keep);
            Assert.Equal(checkpoint, compacted.Offset);
            Assert.Equal(3, compacted.Length);
            Assert.Equal(checkpoint + 3, arena.Cursor);
            Assert.Equal(new decimal[] { 10m, 20m, 30m }, arena.GetSlice(compacted).ToArray());

            int beforeErr = arena.Cursor;
            Assert.True(arena.TryAllocate(100, out _, out _));
            arena.RestoreCursor(beforeErr);
            Assert.Equal(beforeErr, arena.Cursor);

            // Compacting an earlier handle (Offset < targetOffset) reuses it without duplicating arena space.
            DslArrayHandle reused = arena.CompactResult(beforeErr, compacted);
            Assert.Equal(compacted, reused);
            Assert.Equal(beforeErr, arena.Cursor);
        }
        finally
        {
            arena.Dispose();
        }
    }

    [Fact]
    public void DslArrayArenaIsLazyAndAllocatesZeroBytesWhenUnused()
    {
        var vars = new Dictionary<string, decimal>(StringComparer.Ordinal) { ["x"] = 3m };
        var items = new[] { new ExpressionCalculator.CalculatorBatchItem("y", "x * 2 + 1") };

        long RunScalarBatch()
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            IReadOnlyList<ExpressionCalculator.CalculatorBatchResult> res =
                ExpressionCalculator.EvaluateBatch(vars, null, items, CalculatorLimits.Default);
            long after = GC.GetAllocatedBytesForCurrentThread();
            Assert.True(res[0].IsOk);
            Assert.Equal(7m, res[0].ScalarValue);
            return after - before;
        }

        _ = RunScalarBatch();
        long allocated = RunScalarBatch();
        // Only small managed result containers, never a 4096 * 16 byte arrayVariables buffer.
        Assert.True(allocated < 4_096, $"Unexpected allocation {allocated} bytes for scalar-only batch.");
    }

    [Fact]
    public void DeterministicRngProducesBitExactSequencesAndBounds()
    {
        var r1 = new DeterministicRng(123456789UL);
        var r2 = new DeterministicRng(123456789UL);
        var rZero = new DeterministicRng(0UL);

        for (int i = 0; i < 32; i++)
        {
            Assert.Equal(r1.NextUInt64(), r2.NextUInt64());
            double u = rZero.NextUnitDouble();
            Assert.InRange(u, 0.0, 1.0);
            Assert.True(u < 1.0);
            int idx1 = r1.NextInt(7);
            int idx2 = r2.NextInt(7);
            Assert.Equal(idx1, idx2);
            Assert.InRange(idx1, 0, 6);
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => r1.NextInt(0));
    }

    [Fact]
    public void ScalarEvaluateRejectsArrayResultWhileBatchAllowsArrays()
    {
        CalculatorOutcome scalarRes = ExpressionCalculator.Evaluate("[1, 2, 3]", EmptyScalars, CalculatorLimits.Default);
        Assert.NotNull(scalarRes.Error);
        Assert.Equal(CalculatorErrorCode.DomainError, scalarRes.Error!.Code);

        var items = new[]
        {
            new ExpressionCalculator.CalculatorBatchItem("arr", "[10, 20, 30]"),
            new ExpressionCalculator.CalculatorBatchItem("tot", "sum(arr)"),
        };
        IReadOnlyList<ExpressionCalculator.CalculatorBatchResult> batchRes =
            ExpressionCalculator.EvaluateBatch(EmptyScalars, null, items, CalculatorLimits.Default);

        Assert.True(batchRes[0].IsOk);
        Assert.True(batchRes[0].IsArray);
        Assert.Equal(new decimal[] { 10m, 20m, 30m }, batchRes[0].ArrayValues!.ToArray());
        Assert.True(batchRes[1].IsOk);
        Assert.False(batchRes[1].IsArray);
        Assert.Equal(60m, batchRes[1].ScalarValue);
    }

    [Fact]
    public void ArrayOperationsAndVariadicLoweringWorkAsSpecified()
    {
        var items = new[]
        {
            new ExpressionCalculator.CalculatorBatchItem("a", "[2, 4, 6, 8, 10]"),
            new ExpressionCalculator.CalculatorBatchItem("b", "[1, 1, 2, 2, 5]"),
            new ExpressionCalculator.CalculatorBatchItem("len_a", "arr_len(a)"),
            new ExpressionCalculator.CalculatorBatchItem("g2", "arr_get(a, 2)"),
            new ExpressionCalculator.CalculatorBatchItem("sl", "arr_slice(a, 1, 3)"),
            new ExpressionCalculator.CalculatorBatchItem("win", "arr_window(a, 3)"),
            new ExpressionCalculator.CalculatorBatchItem("zadd", "arr_zip_add(a, b)"),
            new ExpressionCalculator.CalculatorBatchItem("zmul", "arr_zip_mul(a, b)"),
            new ExpressionCalculator.CalculatorBatchItem("med", "median(a)"),
            new ExpressionCalculator.CalculatorBatchItem("q50", "quantile(0.5, a)"),
            new ExpressionCalculator.CalculatorBatchItem("zs", "zscore(10, a)"),
            new ExpressionCalculator.CalculatorBatchItem("iqr_v", "iqr(a)"),
            new ExpressionCalculator.CalculatorBatchItem("mad_v", "mad(a)"),
            new ExpressionCalculator.CalculatorBatchItem("flag_ok", "has_flag(256, 8)"),
            new ExpressionCalculator.CalculatorBatchItem("flag_bad", "has_flag(256, 16)"),
            new ExpressionCalculator.CalculatorBatchItem("nested_bad", "[[1]]"),
            new ExpressionCalculator.CalculatorBatchItem("bin_bad", "a + b"),
            new ExpressionCalculator.CalculatorBatchItem("unary_bad", "-a"),
        };

        IReadOnlyList<ExpressionCalculator.CalculatorBatchResult> res =
            ExpressionCalculator.EvaluateBatch(EmptyScalars, null, items, CalculatorLimits.Default);

        Assert.Equal(5m, res[2].ScalarValue);
        Assert.Equal(6m, res[3].ScalarValue);
        Assert.Equal(new decimal[] { 4m, 6m, 8m }, res[4].ArrayValues!.ToArray());
        Assert.Equal(new decimal[] { 6m, 8m, 10m }, res[5].ArrayValues!.ToArray());
        Assert.Equal(new decimal[] { 3m, 5m, 8m, 10m, 15m }, res[6].ArrayValues!.ToArray());
        Assert.Equal(new decimal[] { 2m, 4m, 12m, 16m, 50m }, res[7].ArrayValues!.ToArray());
        Assert.Equal(6m, res[8].ScalarValue);
        Assert.Equal(6m, res[9].ScalarValue);
        Assert.True(res[10].ScalarValue > 1.4142m && res[10].ScalarValue < 1.4143m);
        Assert.Equal(4m, res[11].ScalarValue);
        Assert.Equal(2m, res[12].ScalarValue);
        Assert.Equal(1m, res[13].ScalarValue);
        Assert.Equal(CalculatorErrorCode.DomainError, res[14].Error!.Code);
        Assert.Equal(CalculatorErrorCode.DomainError, res[15].Error!.Code);
        Assert.Equal(CalculatorErrorCode.DomainError, res[16].Error!.Code);
        Assert.Equal(CalculatorErrorCode.DomainError, res[17].Error!.Code);
    }

    [Fact]
    public void ArrayLiteralExceedingMaxElementsFailsParse()
    {
        var tightLimits = new CalculatorLimits { MaxArrayLiteralElements = 4 };
        CalculatorOutcome res = ExpressionCalculator.Evaluate("arr_len([1, 2, 3, 4, 5])", EmptyScalars, tightLimits);
        Assert.NotNull(res.Error);
        Assert.Equal(CalculatorErrorCode.DomainError, res.Error!.Code);
    }

    [Fact]
    public void BatchArenaCompactionReclaimsTransientArraysWhileEnforcingBatchBudget()
    {
        decimal[] big = Enumerable.Range(1, 512).Select(i => (decimal)i).ToArray();
        var arrVars = new Dictionary<string, IReadOnlyList<decimal>>(StringComparer.Ordinal)
        {
            ["s1"] = big,
            ["s2"] = big,
        };

        var items = new[]
        {
            new ExpressionCalculator.CalculatorBatchItem("t1", "sum(arr_slice(arr_zip_add(s1, s2), 0, 512))"),
            new ExpressionCalculator.CalculatorBatchItem("t2", "sum(arr_window(s1, 200))"),
            new ExpressionCalculator.CalculatorBatchItem("t3", "sum(arr_window(s2, 200))"),
        };

        IReadOnlyList<ExpressionCalculator.CalculatorBatchResult> res =
            ExpressionCalculator.EvaluateBatch(EmptyScalars, arrVars, items, CalculatorLimits.Default);
        Assert.True(res[0].IsOk);
        Assert.True(res[1].IsOk);
        Assert.True(res[2].IsOk);

        // Saving 7 * 512 + 2 * 512 = 4608 elements across named outputs exceeds the 4096 batch arena budget.
        ExpressionCalculator.CalculatorBatchItem[] overflowItems = Enumerable.Range(0, 7)
            .Select(i => new ExpressionCalculator.CalculatorBatchItem($"o{i}", "arr_slice(s1, 0, 512)"))
            .ToArray();
        IReadOnlyList<ExpressionCalculator.CalculatorBatchResult> overRes =
            ExpressionCalculator.EvaluateBatch(EmptyScalars, arrVars, overflowItems, CalculatorLimits.Default);
        Assert.True(overRes[5].IsOk); // 2 * 512 + 6 * 512 = 4096 fits exactly.
        Assert.False(overRes[6].IsOk); // 4096 + 512 overflows!
        Assert.Equal(CalculatorErrorCode.BudgetExceeded, overRes[6].Error!.Code);
    }

    [Fact]
    public void MatrixConditionAndValidationFlagsMatchMathematicalContract()
    {
        var budget = DslExecutionBudget.CreateDefault();

        double[] diag = [4.0, 0.0, 0.0, 1.0];
        Assert.Equal(4.0, DslLinearAlgebraMath.MatCond(diag, 2, 2, ref budget, 1).Value, 12);
        Assert.Equal(4.0, DslLinearAlgebraMath.MatCond(diag, 2, 2, ref budget, 2).Value, 12);
        Assert.Equal(4.0, DslLinearAlgebraMath.MatCond(diag, 2, 2, ref budget, 3).Value, 12);
        Assert.Equal(0.25, DslLinearAlgebraMath.MatRCond(diag, 2, 2, ref budget, 2).Value, 12);

        // normKind=0 or 4 must be DomainError; rectangular 1-norm/inf-norm must be DimensionMismatch.
        Assert.Equal(MathErrorCode.DomainError, DslLinearAlgebraMath.MatCond(diag, 2, 2, ref budget, 0).Error);
        Assert.Equal(MathErrorCode.DomainError, DslLinearAlgebraMath.MatCond(diag, 2, 2, ref budget, 4).Error);
        double[] rect = [1.0, 0.0, 0.0, 2.0, 0.0, 0.0];
        Assert.Equal(MathErrorCode.DimensionMismatch, DslLinearAlgebraMath.MatRCond(rect, 3, 2, ref budget, 1).Error);
        Assert.Equal(0.5, DslLinearAlgebraMath.MatRCond(rect, 3, 2, ref budget, 2).Value, 10);

        // Invalid non-PSD correlation matrix
        double[] badCorr =
        [
            1.0, 0.9, 0.9,
            0.9, 1.0, -0.9,
            0.9, -0.9, 1.0
        ];
        Assert.Equal(MathErrorCode.None, DslLinearAlgebraMath.MatValidateCorr(badCorr, 3, 3, 1e-9, out MatrixValidationFlags flags, ref budget));
        Assert.True((flags & MatrixValidationFlags.NotPsd) != 0);

        Span<double> fixedCorr = stackalloc double[9];
        Assert.Equal(MathErrorCode.None, DslLinearAlgebraMath.MatNearestCorr(badCorr, 3, fixedCorr, ref budget));
        Assert.Equal(MathErrorCode.None, DslLinearAlgebraMath.MatValidateCorr(fixedCorr, 3, 3, 1e-9, out MatrixValidationFlags fixedFlags, ref budget));
        Assert.Equal(MatrixValidationFlags.Valid, fixedFlags & ~(MatrixValidationFlags.NotPd | MatrixValidationFlags.IllConditioned));
        Assert.Equal(MatrixValidationFlags.Valid, fixedFlags & MatrixValidationFlags.NotPsd);

        // MatIsCorr defaults to PSD valid, while requirePd=true enforces strict PD (flags == Valid).
        Assert.True(DslLinearAlgebraMath.MatIsCorr(fixedCorr, 3, 3, 1e-9, ref budget).Value);
        Assert.False(DslLinearAlgebraMath.MatIsCorr(fixedCorr, 3, 3, 1e-9, ref budget, requirePd: true).Value);
        Assert.True(DslLinearAlgebraMath.MatIsCorr([1.0, 1.0, 1.0, 1.0], 2, 2, 1e-9, ref budget).Value);
        Assert.False(DslLinearAlgebraMath.MatIsCorr([1.0, 1.0, 1.0, 1.0], 2, 2, 1e-9, ref budget, requirePd: true).Value);

        // MatRCond preserves sub-1e-12 finite rcond while MatCond treats <= 1e-12 as SingularMatrix.
        double[] illDiag = [1.0, 0.0, 0.0, 1e-13];
        double rcondIll = DslLinearAlgebraMath.MatRCond(illDiag, 2, 2, ref budget, 2).Value;
        Assert.True(Math.Abs(rcondIll - 1e-13) < 1e-24, $"Expected ~1e-13, got {rcondIll:e16}");
        Assert.Equal(MathErrorCode.SingularMatrix, DslLinearAlgebraMath.MatCond(illDiag, 2, 2, ref budget, 2).Error);

        // Positive scaling invariance for MatValidateCov & MatCovToCorr across [1e-20, 1e8] and zero matrix handling.
        double[] covBad = [1.0, 2.0, 2.0, 1.0];
        double[] covGood = [1.0, 0.5, 0.5, 1.0];
        Assert.Equal(MathErrorCode.None, DslLinearAlgebraMath.MatValidateCov(covBad, 2, 2, true, 1e-3, out MatrixValidationFlags refBad, ref budget));
        Assert.True((refBad & (MatrixValidationFlags.OutOfRange | MatrixValidationFlags.NotPsd | MatrixValidationFlags.NotPd)) != 0);
        foreach (double scale in new[] { 1e-20, 1e-13, 1.0, 1e8 })
        {
            double[] sb = [covBad[0] * scale, covBad[1] * scale, covBad[2] * scale, covBad[3] * scale];
            double[] sg = [covGood[0] * scale, covGood[1] * scale, covGood[2] * scale, covGood[3] * scale];
            Assert.Equal(MathErrorCode.None, DslLinearAlgebraMath.MatValidateCov(sb, 2, 2, true, 1e-3, out MatrixValidationFlags fb, ref budget));
            Assert.Equal(refBad, fb);
            Assert.Equal(MathErrorCode.None, DslLinearAlgebraMath.MatValidateCov(sg, 2, 2, true, 1e-9, out MatrixValidationFlags fg, ref budget));
            Assert.Equal(MatrixValidationFlags.Valid, fg);
            Span<double> rConv = stackalloc double[4];
            Assert.Equal(MathErrorCode.None, DslLinearAlgebraMath.MatCovToCorr(sg, 2, rConv));
            Assert.Equal(0.5, rConv[1], 12);
        }

        double[] covZero = [0.0, 0.0, 0.0, 0.0];
        Assert.Equal(MathErrorCode.None, DslLinearAlgebraMath.MatValidateCov(covZero, 2, 2, true, 1e-9, out MatrixValidationFlags zPd, ref budget));
        Assert.True((zPd & MatrixValidationFlags.InvalidDiag) != 0);
        Assert.Equal(MathErrorCode.None, DslLinearAlgebraMath.MatValidateCov(covZero, 2, 2, false, 1e-9, out MatrixValidationFlags zPsd, ref budget));
        Assert.Equal(MatrixValidationFlags.Valid, zPsd & MatrixValidationFlags.InvalidDiag);

        // Mahalanobis rejects indefinite covInv upfront even along a positive direction.
        Assert.Equal(MathErrorCode.DomainError, DslRegressionMath.Mahalanobis([1.0, 0.0], [0.0, 0.0], [1.0, 0.0, 0.0, -1.0], 2, ref budget).Error);
    }

    [Fact]
    public void RegressionAndPcaKernelsComputeAccurateDeterministicResults()
    {
        var budget = DslExecutionBudget.CreateDefault();

        // y = 2 + 3*x1 - 1*x2 across 6 rows (row-major)
        double[] x =
        [
            1.0, 0.0,
            2.0, 1.0,
            3.0, 0.0,
            4.0, 2.0,
            5.0, 1.0,
            6.0, 3.0
        ];
        double[] y = new double[6];
        for (int i = 0; i < 6; i++)
        {
            y[i] = 2.0 + 3.0 * x[i * 2] - 1.0 * x[i * 2 + 1];
        }

        Span<double> beta = stackalloc double[3];
        Assert.Equal(MathErrorCode.None, DslRegressionMath.OlsFit(x, 6, 2, y, beta, ref budget));
        Assert.Equal(2.0, beta[0], 8);
        Assert.Equal(3.0, beta[1], 8);
        Assert.Equal(-1.0, beta[2], 8);

        Span<double> ridgeBeta = stackalloc double[3];
        Assert.Equal(MathErrorCode.None, DslRegressionMath.RidgeFit(x, 6, 2, y, 0.5, ridgeBeta, ref budget));
        Assert.True(double.IsFinite(ridgeBeta[0]));

        // Quadratic series y_t = 1 + 2*t + t^2 for t=0..5 -> t=6 is 1 + 12 + 36 = 49
        double[] series = [1.0, 4.0, 9.0, 16.0, 25.0, 36.0];
        MathResult<double> proj = DslRegressionMath.PolyProject(series, 2, 1, ref budget);
        Assert.True(proj.IsOk);
        Assert.Equal(49.0, proj.Value, 6);

        // PCA projection deterministic column sign: centered scores must have mean ~ 0
        Span<double> z = stackalloc double[6];
        Assert.Equal(MathErrorCode.None, DslRegressionMath.PcaProject(x, 6, 2, 1, z, ref budget));
        double zMean = (z[0] + z[1] + z[2] + z[3] + z[4] + z[5]) / 6.0;
        Assert.Equal(0.0, zMean, 8);

        // PCA sign tie-break: negatively correlated 2-feature matrix perturbed by 1e-14 keeps row 0 dominant positive sign convention.
        double[] tieBase =
        [
            -1.0,  1.0,
             1.0, -1.0,
            -0.1, -0.1,
             0.1,  0.1
        ];
        double[] tiePerturbed =
        [
            -1.0,  1.00000000000001,
             1.0, -1.00000000000001,
            -0.1, -0.1,
             0.1,  0.1
        ];
        Span<double> zBase = stackalloc double[4];
        Span<double> zPert = stackalloc double[4];
        Assert.Equal(MathErrorCode.None, DslRegressionMath.PcaProject(tieBase, 4, 2, 1, zBase, ref budget));
        Assert.Equal(MathErrorCode.None, DslRegressionMath.PcaProject(tiePerturbed, 4, 2, 1, zPert, ref budget));
        Assert.True(zBase[0] < 0.0 && zPert[0] < 0.0, $"Expected stable row-0 sign under 1e-14 perturbation, got {zBase[0]}, {zPert[0]}");

        // WeightedMean & WeightedVariance positive scaling invariance (1e-15m .. 1e12m) & all-zero guard
        decimal[] wBase = [1m, 2m, 3m, 4m];
        decimal[] vBase = [10m, 20m, 30m, 40m];
        decimal expectedMean = DslRegressionMath.WeightedMean(wBase, vBase).Value;
        decimal expectedVar = DslRegressionMath.WeightedVariance(wBase, vBase).Value;
        foreach (decimal factor in new[] { 1e-15m, 1e-8m, 1e12m })
        {
            decimal[] wScaled = wBase.Select(w => w * factor).ToArray();
            Assert.Equal(expectedMean, DslRegressionMath.WeightedMean(wScaled, vBase).Value);
            Assert.Equal(expectedVar, DslRegressionMath.WeightedVariance(wScaled, vBase).Value);
        }

        Assert.Equal(MathErrorCode.DivisionByZero, DslRegressionMath.WeightedMean([0m, 0m, 0m], [1m, 2m, 3m]).Error);
        Assert.Equal(MathErrorCode.DivisionByZero, DslRegressionMath.WeightedVariance([0m, 0m, 0m], [1m, 2m, 3m]).Error);
    }

    [Fact]
    public void StackOnlyLinearDiagnosticsAllocateZeroManagedBytes()
    {
        long RunLinearDiagnostics()
        {
            var budget = DslExecutionBudget.CreateDefault();
            ReadOnlySpan<double> cov = stackalloc double[] { 4.0, 1.0, 1.0, 3.0 };
            ReadOnlySpan<double> corr = stackalloc double[] { 1.0, 0.5, 0.5, 1.0 };
            ReadOnlySpan<double> x = stackalloc double[] { 1.0, 2.0 };
            ReadOnlySpan<double> mu = stackalloc double[] { 0.0, 0.0 };
            ReadOnlySpan<double> covInv = stackalloc double[] { 1.0, 0.0, 0.0, 1.0 };

            long before = GC.GetAllocatedBytesForCurrentThread();
            MathResult<double> rc = DslLinearAlgebraMath.MatRCond(cov, 2, 2, ref budget, 2);
            MathErrorCode vc = DslLinearAlgebraMath.MatValidateCorr(corr, 2, 2, 1e-9, out MatrixValidationFlags f1, ref budget);
            MathErrorCode vs = DslLinearAlgebraMath.MatValidateCov(cov, 2, 2, true, 1e-9, out MatrixValidationFlags f2, ref budget);
            MathResult<double> mh = DslRegressionMath.Mahalanobis(x, mu, covInv, 2, ref budget);
            long after = GC.GetAllocatedBytesForCurrentThread();

            Assert.True(rc.IsOk);
            Assert.Equal(MathErrorCode.None, vc);
            Assert.Equal(MatrixValidationFlags.Valid, f1);
            Assert.Equal(MathErrorCode.None, vs);
            Assert.Equal(MatrixValidationFlags.Valid, f2);
            Assert.True(mh.IsOk);
            return after - before;
        }

        _ = RunLinearDiagnostics();
        long allocated = RunLinearDiagnostics();
        Assert.Equal(0L, allocated);
    }
}
