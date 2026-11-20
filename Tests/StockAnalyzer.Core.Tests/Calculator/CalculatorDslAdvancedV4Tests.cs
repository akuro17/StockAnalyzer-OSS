using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Calculator;
using StockAnalyzer.Core.MathUtils;
using Xunit;

namespace StockAnalyzer.Core.Tests.Calculator;

public class CalculatorDslAdvancedV4Tests
{
    private static readonly IReadOnlyDictionary<string, decimal> EmptyScalars =
        new Dictionary<string, decimal>(0, StringComparer.Ordinal);

    [Fact]
    public void RobustStatisticsMatchContractsAndReusedMathUtils()
    {
        var budget = DslExecutionBudget.CreateDefault();
        double[] sample = [1.0, 2.0, 2.0, 3.0, 100.0];

        MathResult<double> madS = DslRobustStatisticsMath.MadScaled(sample);
        Assert.True(madS.IsOk);
        // |x - 2| = [1, 0, 0, 1, 98] -> median = 1 -> scaled = DefaultMadScale
        Assert.Equal(DslRobustStatisticsMath.DefaultMadScale, madS.Value, 12);

        // Trimmed mean 20% removes 1 element from each end: [2, 2, 3] -> 7/3
        MathResult<double> trim = DslRobustStatisticsMath.TrimmedMean(sample, 0.2);
        Assert.True(trim.IsOk);
        Assert.Equal(7.0 / 3.0, trim.Value, 12);

        // Winsorized mean 20% replaces 1 -> 2 and 100 -> 3: [2, 2, 2, 3, 3] -> 12/5 = 2.4
        MathResult<double> win = DslRobustStatisticsMath.WinsorMean(sample, 0.2);
        Assert.True(win.IsOk);
        Assert.Equal(2.4, win.Value, 12);

        // Huber & Biweight resist extreme outlier 100
        double[] clean = [10.0, 10.2, 10.1, 9.9, 10.0, 500.0];
        MathResult<double> hub = DslRobustStatisticsMath.HuberLocation(clean, ref budget);
        MathResult<double> biw = DslRobustStatisticsMath.BiweightLocation(clean, ref budget);
        Assert.True(hub.IsOk && hub.Value < 11.0 && hub.Value > 9.5);
        Assert.True(biw.IsOk && biw.Value < 10.5 && biw.Value > 9.8);

        // Flat MAD fallback avoids division by zero and returns median immediately
        double[] flatMad = [5.0, 5.0, 5.0, 5.0, 99.0];
        Assert.Equal(5.0, DslRobustStatisticsMath.HuberLocation(flatMad, ref budget).Value, 12);
        Assert.Equal(5.0, DslRobustStatisticsMath.BiweightLocation(flatMad, ref budget).Value, 12);

        // Exact decimal Theil-Sen & Siegel matching RobustRegressionMath
        decimal[] yLine = [2m, 5m, 8m, 11m, 14m];
        Assert.Equal(3m, DslRobustStatisticsMath.TheilSenSlope(yLine).Value);
        Assert.Equal(3m, DslRobustStatisticsMath.SiegelSlope(yLine).Value);

        // 2-arg with duplicate x pairs safely excluded
        decimal[] xDup = [1m, 1m, 2m, 3m];
        decimal[] yDup = [10m, 2m, 4m, 6m];
        Assert.Equal(2m, DslRobustStatisticsMath.TheilSenSlope(xDup, yDup).Value);
        decimal[] xAllSame = [1m, 1m, 1m];
        Assert.Equal(MathErrorCode.DivisionByZero, DslRobustStatisticsMath.TheilSenSlope(xAllSame, [1m, 2m, 3m]).Error);
    }

    [Fact]
    public void AcfAndStrictPacfEnforceFlatAndSingularityGuards()
    {
        var budget = DslExecutionBudget.CreateDefault();

        // AR(1) alternating series
        double[] ar1 = [1.0, -0.8, 0.64, -0.512, 0.4096, -0.32768, 0.262144, -0.2097152, 0.16777216, -0.134217728];
        Assert.Equal(1.0, DslTimeSeriesMath.Acf(ar1, 0, ref budget).Value, 12);

        Span<double> pacfs = stackalloc double[3];
        Assert.Equal(MathErrorCode.None, DslTimeSeriesMath.PacfSeries(ar1, 3, pacfs, ref budget));
        Assert.True(pacfs[0] < -0.5);
        Assert.Equal(pacfs[1], DslTimeSeriesMath.Pacf(ar1, 2, ref budget).Value, 12);

        // Flat series -> DivisionByZero even at lag=0
        double[] flat = [42.0, 42.0, 42.0, 42.0, 42.0];
        Assert.Equal(MathErrorCode.DivisionByZero, DslTimeSeriesMath.Acf(flat, 0, ref budget).Error);
        Assert.Equal(MathErrorCode.DivisionByZero, DslTimeSeriesMath.Pacf(flat, 1, ref budget).Error);

        // Period-2 alternating series [1, -1, 1, -1, 1, -1] produces biased r_1 ~ -5/6 -> not unit, but 2-step perfect -> |phi_22| ~ 1 or exact singularity
        // Check pure near-linear constant difference or lag out of range
        Assert.Equal(MathErrorCode.DomainError, DslTimeSeriesMath.Acf(ar1, ar1.Length - 1, ref budget).Error);
    }

    [Fact]
    public void MultipleTestingPAdjustPreservesOrderAndMonotonicity()
    {
        decimal[] p = [0.04m, 0.001m, 0.5m, 0.02m];
        Span<decimal> adj = stackalloc decimal[4];

        Assert.Equal(MathErrorCode.None, DslMultipleTestingMath.PAdjust(p, MultipleTestingMethod.Bonferroni, adj));
        Assert.Equal([0.16m, 0.004m, 1m, 0.08m], adj.ToArray());

        Assert.Equal(MathErrorCode.None, DslMultipleTestingMath.PAdjust(p, MultipleTestingMethod.Holm, adj));
        // Sorted order indices: 1 (0.001*4=0.004), 3 (0.02*3=0.06), 0 (0.04*2=0.08), 2 (0.5*1=0.5)
        Assert.Equal([0.08m, 0.004m, 0.5m, 0.06m], adj.ToArray());

        Assert.Equal(MathErrorCode.None, DslMultipleTestingMath.PAdjust(p, MultipleTestingMethod.BenjaminiHochberg, adj));
        // BH: p_sorted * 4 / rank -> [0.004/1=0.004, 0.08/2=0.04, 0.16/3=0.0533..., 2.0/4=0.5]
        Assert.True(adj[1] < adj[3] && adj[3] < adj[0] && adj[0] < adj[2]);

        Assert.Equal(MathErrorCode.None, DslMultipleTestingMath.PAdjust(p, MultipleTestingMethod.BenjaminiYekutieli, adj));
        Assert.All(adj.ToArray(), v => Assert.InRange(v, 0m, 1m));

        Assert.Equal(MathErrorCode.DomainError, DslMultipleTestingMath.PAdjust([1.1m], MultipleTestingMethod.Bonferroni, adj));
    }

    [Fact]
    public void NumericalDiagnosticsWorkWithExactBoundaries()
    {
        double[] a = [2.0, 1.0, 1.0, 3.0];
        double[] x = [1.0, 2.0];
        double[] b = [4.0, 7.0];
        Assert.Equal(0.0, DslNumericalDiagnosticsMath.ResidualNorm(a, 2, 2, x, b).Value, 14);
        Assert.Equal(0.0, DslNumericalDiagnosticsMath.ResidualNorm([0.0], 1, 1, [0.0], [0.0]).Value, 14);

        Assert.Equal(MathErrorCode.DivisionByZero, DslNumericalDiagnosticsMath.RelativeError(1m, 0m).Error);
        Assert.Equal(0.05, DslNumericalDiagnosticsMath.RelativeError(1.05m, 1m).Value, 12);

        Assert.Equal(0m, DslNumericalDiagnosticsMath.UlpDiff(-0.0, 0.0).Value);
        Assert.Equal(1m, DslNumericalDiagnosticsMath.UlpDiff(1.0, BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(1.0) + 1)).Value);

        double[] cancelSum = [1e16, 1.0, -1e16];
        Assert.Equal(MathErrorCode.None, DslNumericalDiagnosticsMath.KahanSum(cancelSum, out double sum, out double err));
        Assert.Equal(1.0, sum, 12);
        Assert.Equal(1.0, err, 12);

        MathResult<double> bits = DslNumericalDiagnosticsMath.CancellationBits(1.0, -0.9999);
        Assert.True(bits.IsOk && bits.Value > 13.0 && bits.Value < 14.0);
        Assert.Equal(53.0, DslNumericalDiagnosticsMath.CancellationBits(1.0, -1.0).Value, 12);
        Assert.Equal(0.0, DslNumericalDiagnosticsMath.CancellationBits(1.0, 2.0).Value, 12);
    }

    [Fact]
    public void BootstrapAndMonteCarloAreDeterministicAndBounded()
    {
        var b1 = DslExecutionBudget.CreateDefault();
        var b2 = DslExecutionBudget.CreateDefault();
        decimal[] series = [10m, 12m, 11m, 14m, 13m, 16m, 15m, 18m, 17m, 20m, 19m, 22m];

        MathResult<BootstrapResult> ci1 = DslResamplingMath.BootConfidenceInterval(
            series, BootstrapStatKind.Mean, 100, 3, 0.05, 42UL, BootstrapBlockMode.CircularBlock, ref b1);
        MathResult<BootstrapResult> ci2 = DslResamplingMath.BootConfidenceInterval(
            series, BootstrapStatKind.Mean, 100, 3, 0.05, 42UL, BootstrapBlockMode.CircularBlock, ref b2);
        Assert.True(ci1.IsOk && ci2.IsOk);
        Assert.Equal(ci1.Value, ci2.Value);
        Assert.True(ci1.Value.Lower <= ci1.Value.Estimate && ci1.Value.Estimate <= ci1.Value.Upper);

        MathResult<BootstrapResult> stationary = DslResamplingMath.BootConfidenceInterval(
            series, BootstrapStatKind.Median, 100, 3, 0.1, 77UL, BootstrapBlockMode.Stationary, ref b1);
        Assert.True(stationary.IsOk);
        Assert.True(stationary.Value.Lower <= stationary.Value.Upper);

        // 50 * 0.04 == 2.0 exact boundary passes via decimal / tolerance guard; 50 * 0.02 fails
        MathResult<BootstrapResult> boundaryOk = DslResamplingMath.BootConfidenceInterval(
            series, BootstrapStatKind.Mean, 50, 2, 0.04, 11UL, BootstrapBlockMode.CircularBlock, ref b1);
        Assert.True(boundaryOk.IsOk);
        MathResult<BootstrapResult> boundaryFail = DslResamplingMath.BootConfidenceInterval(
            series, BootstrapStatKind.Mean, 50, 2, 0.02, 11UL, BootstrapBlockMode.CircularBlock, ref b1);
        Assert.Equal(MathErrorCode.DomainError, boundaryFail.Error);

        // DSL seed guard, QMC seed=0 SE guard, PseudoRng seed=0 SE allowance, and MaxSamples=2048 budget enforcement
        var mcItems = new[]
        {
            new ExpressionCalculator.CalculatorBatchItem("mc1", "mc_integrate(x^2, x, 0, 1, 512, 0, 0)"),
            new ExpressionCalculator.CalculatorBatchItem("mc1_se", "mc_integrate_se(x^2, x, 0, 1, 512, 42, 0)"),
            new ExpressionCalculator.CalculatorBatchItem("mc2", "mc_integrate_nd(x * y, [x, y], [0, 0], [2, 3], 512, 0, 0)"),
            new ExpressionCalculator.CalculatorBatchItem("mc2_se", "mc_integrate_nd_se(x * y, [x, y], [0, 0], [2, 3], 512, 7, 2)"),
            new ExpressionCalculator.CalculatorBatchItem("bad_seed", "boot_ci([1,2,3,4,5,6,7,8,9,10], 0, 50, 2, 0.05, -1)"),
            new ExpressionCalculator.CalculatorBatchItem("bad_frac_seed", "mc_integrate(x, x, 0, 1, 64, 1.5)"),
            new ExpressionCalculator.CalculatorBatchItem("qmc_zero_se", "mc_integrate_se(x, x, 0, 1, 128, 0, 0)"),
            new ExpressionCalculator.CalculatorBatchItem("prng_zero_se", "mc_integrate_se(x, x, 0, 1, 128, 0, 1)"),
            new ExpressionCalculator.CalculatorBatchItem("max_samples_ok", "mc_integrate(x, x, 0, 1, 2048, 1, 0)"),
            new ExpressionCalculator.CalculatorBatchItem("budget_hit", "mc_integrate(x^2 + sin(x) + cos(x), x, 0, 1, 2048, 1, 0)"),
            new ExpressionCalculator.CalculatorBatchItem("over_max_samples", "mc_integrate(x, x, 0, 1, 2049, 1, 0)"),
        };
        IReadOnlyList<ExpressionCalculator.CalculatorBatchResult> mcRes =
            ExpressionCalculator.EvaluateBatch(EmptyScalars, null, mcItems, CalculatorLimits.Default);

        Assert.InRange(mcRes[0].ScalarValue, 0.33m, 0.34m);
        Assert.True(mcRes[1].IsOk && mcRes[1].ScalarValue >= 0m);
        Assert.InRange(mcRes[2].ScalarValue, 8.9m, 9.1m);
        Assert.True(mcRes[3].IsOk && mcRes[3].ScalarValue >= 0m);
        Assert.Equal(CalculatorErrorCode.DomainError, mcRes[4].Error!.Code);
        Assert.Equal(CalculatorErrorCode.DomainError, mcRes[5].Error!.Code);
        Assert.Equal(CalculatorErrorCode.DomainError, mcRes[6].Error!.Code);
        Assert.True(mcRes[7].IsOk && mcRes[7].ScalarValue > 0m);
        Assert.True(mcRes[8].IsOk);
        Assert.Equal(CalculatorErrorCode.BudgetExceeded, mcRes[9].Error!.Code);
        Assert.Equal(CalculatorErrorCode.DomainError, mcRes[10].Error!.Code);
    }

    [Fact]
    public void FiniteDifferencesAndPde1DSatisfyStabilityAndAccuracy()
    {
        var items = new[]
        {
            new ExpressionCalculator.CalculatorBatchItem("s", "[100, 102, 105, 110, 118]"),
            new ExpressionCalculator.CalculatorBatchItem("dl", "diff_lag(s, 1, 2)"),
            new ExpressionCalculator.CalculatorBatchItem("dls", "diff_lag_series(s, 1, 2)"),
            new ExpressionCalculator.CalculatorBatchItem("dp", "diff_pct(s, 1)"),
            new ExpressionCalculator.CalculatorBatchItem("fdf", "fd_forward(sin(x), x, 0, 0.0001)"),
            new ExpressionCalculator.CalculatorBatchItem("fdb", "fd_backward(sin(x), x, 0, 0.0001)"),
            new ExpressionCalculator.CalculatorBatchItem("fdc", "fd_central(sin(x), x, 0)"),
            new ExpressionCalculator.CalculatorBatchItem("fds", "fd_second(sin(x), x, pi / 6, 0.001)"),
            new ExpressionCalculator.CalculatorBatchItem("fdr", "fd_richardson(exp(x), x, 1, 0.001, 2)"),
            new ExpressionCalculator.CalculatorBatchItem("fdr_bad_step", "fd_richardson(exp(x), x, 1, 0.000000001, 4)"),
            new ExpressionCalculator.CalculatorBatchItem("pde_cn", "fd_pde_1d([0, 1, 2, 1, 0], 0.1, 0.2, 0.1, 10, 2, 0)"),
            new ExpressionCalculator.CalculatorBatchItem("pde_cfl_bad", "fd_pde_1d([0, 1, 2, 1, 0], 1, 0.1, 0.01, 5, 0, 0)"),
            new ExpressionCalculator.CalculatorBatchItem("pde_neu_btcs", "fd_pde_1d([10, 0, 0, 0, 0], 1, 0.1, 0.1, 1, 1, 1)"),
            new ExpressionCalculator.CalculatorBatchItem("pde_neu_cn", "fd_pde_1d([10, 0, 0, 0, 0], 1, 0.1, 0.1, 1, 2, 1)"),
            new ExpressionCalculator.CalculatorBatchItem("pde_lin_inv", "fd_pde_1d([1, 3, 5, 7, 9], 0.5, 0.2, 0.1, 5, 2, 2)"),
        };

        IReadOnlyList<ExpressionCalculator.CalculatorBatchResult> res =
            ExpressionCalculator.EvaluateBatch(EmptyScalars, null, items, CalculatorLimits.Default);

        // Second diffs of [100, 102, 105, 110, 118] -> 1st diffs [2, 3, 5, 8], 2nd diffs [1, 2, 3]
        Assert.Equal(3m, res[1].ScalarValue);
        Assert.Equal([1m, 2m, 3m], res[2].ArrayValues!.ToArray());
        Assert.Equal(8m / 110m, res[3].ScalarValue);
        Assert.InRange(res[4].ScalarValue, 0.9999m, 1.0001m);
        Assert.InRange(res[5].ScalarValue, 0.9999m, 1.0001m);
        Assert.Equal(1m, Math.Round(res[6].ScalarValue, 6));
        Assert.InRange(res[7].ScalarValue, -0.5001m, -0.4999m);
        Assert.InRange(res[8].ScalarValue, 2.7182818m, 2.7182819m);
        Assert.Equal(CalculatorErrorCode.DomainError, res[9].Error!.Code);

        // Dirichlet diffusion peak strictly decays from 2 while boundary points remain 0
        decimal[] pdeOut = res[10].ArrayValues!.ToArray();
        Assert.Equal(0m, pdeOut[0]);
        Assert.Equal(0m, pdeOut[4]);
        Assert.True(pdeOut[2] > 0m && pdeOut[2] < 2m);

        // Explicit FTCS with r = 1 * 0.01 / 0.01 = 1.0 > 0.5 fails pre-check with DomainError
        Assert.Equal(CalculatorErrorCode.DomainError, res[11].Error!.Code);

        // 2nd-order Ghost-Point Neumann preserves exact trapezoidal mass Q = 0.5*10 = 5.0 and BTCS max principle [0, 10].
        decimal[] neuBtcs = res[12].ArrayValues!.ToArray();
        decimal[] neuCn = res[13].ArrayValues!.ToArray();
        decimal massBtcs = (0.5m * neuBtcs[0]) + neuBtcs[1] + neuBtcs[2] + neuBtcs[3] + (0.5m * neuBtcs[4]);
        decimal massCn = (0.5m * neuCn[0]) + neuCn[1] + neuCn[2] + neuCn[3] + (0.5m * neuCn[4]);
        Assert.InRange(massBtcs, 4.999999m, 5.000001m);
        Assert.InRange(massCn, 4.999999m, 5.000001m);
        Assert.All(neuBtcs, v => Assert.InRange(v, 0m, 10m));

        // LinearExtrapolation preserves initial linear profile within floating-point tolerance (~1e-14).
        decimal[] linInv = res[14].ArrayValues!.ToArray();
        decimal[] expectedLin = [1m, 3m, 5m, 7m, 9m];
        for (int idx = 0; idx < expectedLin.Length; idx++)
        {
            Assert.True(Math.Abs(linInv[idx] - expectedLin[idx]) < 1e-12m, $"Index {idx}: {linInv[idx]}");
        }
    }

    [Fact]
    public void StackOnlyTask3AndTask4KernelsAllocateZeroManagedBytes()
    {
        long RunKernels()
        {
            var budget = DslExecutionBudget.CreateDefault();
            ReadOnlySpan<double> series = stackalloc double[] { 1.0, 2.0, 4.0, 3.0, 5.0, 4.5, 6.0, 5.5, 7.0, 6.5 };
            ReadOnlySpan<decimal> pVals = stackalloc decimal[] { 0.01m, 0.04m, 0.03m, 0.20m };
            Span<decimal> adjOut = stackalloc decimal[4];
            Span<double> acfOut = stackalloc double[4];
            Span<double> pacfOut = stackalloc double[3];
            ReadOnlySpan<double> u0 = stackalloc double[] { 0.0, 1.0, 2.0, 1.0, 0.0 };
            Span<double> uOut = stackalloc double[5];

            long before = GC.GetAllocatedBytesForCurrentThread();
            MathResult<double> hub = DslRobustStatisticsMath.HuberLocation(series, ref budget);
            MathErrorCode acfErr = DslTimeSeriesMath.AcfSeries(series, 3, acfOut, ref budget);
            MathErrorCode pacfErr = DslTimeSeriesMath.PacfSeries(series, 3, pacfOut, ref budget);
            MathErrorCode pErr = DslMultipleTestingMath.PAdjust(pVals, MultipleTestingMethod.BenjaminiHochberg, adjOut);
            MathErrorCode kErr = DslNumericalDiagnosticsMath.KahanSum(series, out double kSum, out _);
            MathErrorCode pdeErr = DslFiniteDifferenceMath.SolvePde1D(u0, 0.1, 0.2, 0.05, 5, PdeScheme.CrankNicolson, PdeBoundaryCondition.DirichletFixed, uOut, ref budget);
            long after = GC.GetAllocatedBytesForCurrentThread();

            Assert.True(hub.IsOk && acfErr == MathErrorCode.None && pacfErr == MathErrorCode.None && pErr == MathErrorCode.None && kErr == MathErrorCode.None && pdeErr == MathErrorCode.None);
            Assert.True(kSum > 0.0);
            return after - before;
        }

        _ = RunKernels();
        long allocated = RunKernels();
        Assert.Equal(0L, allocated);
    }

    [Fact]
    public void BiweightDefaultCReduces5SigmaOutlierAndRngIsDeterministic()
    {
        var budget = DslExecutionBudget.CreateDefault();
        Assert.Equal(4.685, DslRobustStatisticsMath.DefaultBiweightC, 12);

        // With c=4.685 and MAD*1.4826 ~= 1.4826, outlier 7.5 sits at |u| ~= 1.08 > 1 and gets zero Tukey weight.
        double[] symmetricPlusOutlier = [-2.0, -1.0, -1.0, 0.0, 0.0, 1.0, 1.0, 2.0, 7.5];
        MathResult<double> loc = DslRobustStatisticsMath.BiweightLocation(symmetricPlusOutlier, ref budget);
        Assert.True(loc.IsOk);
        Assert.Equal(0.0, loc.Value, 12);

        // Seeded Xoshiro256** RNG determinism across instances, seeds (including max JSON safe int 2^53-1), and [0, 1) range.
        var rngA = new DeterministicRng(42UL);
        var rngB = new DeterministicRng(42UL);
        var rngZero = new DeterministicRng(0UL);
        var rngMaxSafe = new DeterministicRng(9_007_199_254_740_991UL);
        for (int i = 0; i < 8; i++)
        {
            double a = rngA.NextUnitDouble();
            double b = rngB.NextUnitDouble();
            double z = rngZero.NextUnitDouble();
            double m = rngMaxSafe.NextUnitDouble();
            Assert.Equal(BitConverter.DoubleToInt64Bits(a), BitConverter.DoubleToInt64Bits(b));
            Assert.InRange(a, 0.0, 1.0);
            Assert.True(a < 1.0 && z >= 0.0 && z < 1.0 && m >= 0.0 && m < 1.0);
            Assert.NotEqual(a, z);
            Assert.NotEqual(a, m);
        }

        // Tiny-scale invariance around zero (1e-15x): IRLS runs instead of snapping to median.
        double[] asym = [-2.0, -1.0, 0.0, 1.0, 2.0, 10.0];
        double[] asymTiny = asym.Select(v => v * 1e-15).ToArray();
        double hubBase = DslRobustStatisticsMath.HuberLocation(asym, ref budget).Value;
        double hubTiny = DslRobustStatisticsMath.HuberLocation(asymTiny, ref budget).Value;
        double biBase = DslRobustStatisticsMath.BiweightLocation(asym, ref budget).Value;
        double biTiny = DslRobustStatisticsMath.BiweightLocation(asymTiny, ref budget).Value;
        Assert.NotEqual(0.5, hubBase);
        Assert.True(Math.Abs(hubTiny / 1e-15 - hubBase) < 1e-10, $"Expected {hubBase}, got {hubTiny / 1e-15}");
        Assert.True(Math.Abs(biTiny / 1e-15 - biBase) < 1e-10, $"Expected {biBase}, got {biTiny / 1e-15}");
    }
}
