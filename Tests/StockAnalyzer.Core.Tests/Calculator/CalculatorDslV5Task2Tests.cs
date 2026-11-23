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
/// DSL v5 Task 2: bivariate statistics and tests (E), interpolation (G), quantitative finance, risk and information theory (H), spectral analysis (I).
/// </summary>
public sealed class CalculatorDslV5Task2Tests
{
    private static readonly IReadOnlyDictionary<string, decimal> Empty =
        new Dictionary<string, decimal>(StringComparer.Ordinal);

    private static readonly CalculatorLimits Heavy = new()
    {
        MaxExecutionOps = 800_000,
        MaxExpressionChars = 4096,
        MaxTokens = 2048,
        MaxArrayLiteralElements = 512,
    };

    private static ExpressionCalculator.CalculatorBatchResult Run(string expression, CalculatorLimits? limits = null)
        => ExpressionCalculator.EvaluateBatch(
            Empty,
            null,
            new[] { new ExpressionCalculator.CalculatorBatchItem("r", expression) },
            limits ?? CalculatorLimits.Default)[0];

    private static decimal Scalar(string expression, CalculatorLimits? limits = null)
    {
        ExpressionCalculator.CalculatorBatchResult result = Run(expression, limits);
        Assert.True(result.IsOk, $"{expression} => {result.Error?.Code}: {result.Error?.Message}");
        Assert.False(result.IsArray);
        return result.ScalarValue;
    }

    private static decimal[] Arr(string expression, CalculatorLimits? limits = null)
    {
        ExpressionCalculator.CalculatorBatchResult result = Run(expression, limits);
        Assert.True(result.IsOk, $"{expression} => {result.Error?.Code}: {result.Error?.Message}");
        Assert.True(result.IsArray);
        return result.ArrayValues!.ToArray();
    }

    private static void Fails(string expression, CalculatorErrorCode expected, CalculatorLimits? limits = null)
    {
        ExpressionCalculator.CalculatorBatchResult result = Run(expression, limits);
        Assert.False(result.IsOk, $"{expression} unexpectedly succeeded.");
        Assert.Equal(expected, result.Error!.Code);
    }

    private static void Close(double expected, decimal actual, double tolerance)
        => Assert.True(Math.Abs(expected - (double)actual) <= tolerance, $"expected {expected} but was {actual} (tolerance {tolerance}).");

    private static void Close(double[] expected, decimal[] actual, double tolerance)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            Close(expected[i], actual[i], tolerance);
        }
    }

    private static string Vector(IEnumerable<double> values)
        => "[" + string.Join(", ", values.Select(v => v.ToString("R", CultureInfo.InvariantCulture))) + "]";

    // ---------------------------------------------------------------- Domain E

    [Fact]
    public void CovarianceAndPearsonCorrelationFollowTheDefinitions()
    {
        Close(10.0 / 3.0, Scalar("cov_s([1, 2, 3, 4], [2, 4, 6, 8])"), 1e-12);
        Close(2.5, Scalar("cov_p([1, 2, 3, 4], [2, 4, 6, 8])"), 1e-12);
        Close(1.0, Scalar("corr_pearson([1, 2, 3, 4], [2, 4, 6, 8])"), 1e-12);
        Close(-1.0, Scalar("corr_pearson([4, 3, 2, 1], [1, 2, 3, 4])"), 1e-12);
        Close(0.0, Scalar("corr_pearson([1, 2, 3, 4, 5], [2, 1, 4, 1, 2])"), 1e-12);

        Fails("cov_s([1, 2], [1, 2, 3])", CalculatorErrorCode.DimensionMismatch);
        Fails("cov_s([1], [1])", CalculatorErrorCode.DomainError);
        Close(0.0, Scalar("cov_p([5], [7])"), 1e-12);
        Fails("corr_pearson([1], [1])", CalculatorErrorCode.DomainError);
        Fails("corr_pearson([3, 3, 3], [1, 2, 3])", CalculatorErrorCode.DivisionByZero);
        Fails("corr_pearson([1, 2, 3], [4, 4, 4])", CalculatorErrorCode.DivisionByZero);
        Fails("corr_pearson(1, [1, 2])", CalculatorErrorCode.DomainError);
        Fails("cov_p([1, 2], 3)", CalculatorErrorCode.DomainError);
    }

    [Fact]
    public void RankCorrelationsHandleTies()
    {
        Close(1.0, Scalar("corr_spearman([1, 2, 3, 4, 5], [1, 4, 9, 16, 25])"), 1e-12);
        Close(-1.0, Scalar("corr_spearman([1, 2, 3, 4, 5], [25, 16, 9, 4, 1])"), 1e-12);

        // Ranks of x are [1, 2.5, 2.5, 4]: r = 4.5 / sqrt(4.5 * 5).
        Close(4.5 / Math.Sqrt(22.5), Scalar("corr_spearman([1, 2, 2, 3], [1, 2, 3, 4])"), 1e-12);
        Fails("corr_spearman([2, 2, 2], [1, 2, 3])", CalculatorErrorCode.DivisionByZero);

        Close(4.0 / 6.0, Scalar("corr_kendall([1, 2, 3, 4], [1, 3, 2, 4])"), 1e-12);

        // tau-b with ties: n0 = 6, one tied pair in each series, four concordant pairs -> 4 / sqrt(5 * 5).
        Close(0.8, Scalar("corr_kendall([1, 2, 2, 3], [1, 2, 3, 3])"), 1e-12);
        Close(-1.0, Scalar("corr_kendall([1, 2, 3], [3, 2, 1])"), 1e-12);
        Fails("corr_kendall([5, 5, 5], [1, 2, 3])", CalculatorErrorCode.DivisionByZero);
        Fails("corr_kendall([1, 2], [1])", CalculatorErrorCode.DimensionMismatch);
        Fails("corr_kendall([1], [1])", CalculatorErrorCode.DomainError);
    }

    [Fact]
    public void KendallConsumesItsDocumentedBudget()
    {
        var budget = DslExecutionBudget.CreateDefault();
        double[] x = Enumerable.Range(0, 64).Select(i => (double)i).ToArray();
        MathResult<double> tau = DslBivariateAndTestMath.CorrKendall(x, x, ref budget);

        Assert.True(tau.IsOk);
        Assert.Equal(1.0, tau.Value, 12);
        Assert.Equal(DslExecutionBudget.DefaultMaxOps - (64 * 63 / 32), budget.RemainingOps);

        var tiny = DslExecutionBudget.Create(10);
        Assert.Equal(MathErrorCode.BudgetExceeded, DslBivariateAndTestMath.CorrKendall(x, x, ref tiny).Error);
    }

    [Fact]
    public void CrossCorrelationUsesFullSeriesMeansAndDenominator()
    {
        const string X = "[1, 2, 3, 4, 5]";
        const string Y = "[2, 4, 6, 8, 10]";
        Close(1.0, Scalar($"ccf({X}, {Y}, 0)"), 1e-12);
        Close(0.4, Scalar($"ccf({X}, {Y}, 1)"), 1e-12);
        Close(0.4, Scalar($"ccf({X}, {Y}, -1)"), 1e-12);
        Fails($"ccf({X}, {Y}, 5)", CalculatorErrorCode.DomainError);
        Fails($"ccf({X}, {Y}, -5)", CalculatorErrorCode.DomainError);
        Fails($"ccf({X}, {Y}, 1.5)", CalculatorErrorCode.DomainError);
        Fails($"ccf({X}, [1, 2], 0)", CalculatorErrorCode.DimensionMismatch);
        Fails("ccf([3, 3, 3], [1, 2, 3], 0)", CalculatorErrorCode.DivisionByZero);
        Fails("ccf([1], [1], 0)", CalculatorErrorCode.DomainError);
    }

    [Fact]
    public void StudentTestsMatchClosedFormsForTwoDegreesOfFreedom()
    {
        // df = 2: p = 1 - |t| / sqrt(t^2 + 2).
        decimal[] oneSample = Arr("ttest_1samp([1, 2, 3], 0)");
        double t = 2.0 / (1.0 / Math.Sqrt(3.0));
        Close(t, oneSample[0], 1e-12);
        Close(1.0 - (t / Math.Sqrt((t * t) + 2.0)), oneSample[1], 1e-10);
        Close(2.0, oneSample[2], 0.0);

        Close([0.0, 1.0, 4.0], Arr("ttest_1samp([1, 2, 3, 4, 5], 3)"), 1e-12);

        // Welch with n = 2 per group and equal variances: t = -4 / sqrt(2), df = 2.
        decimal[] welch = Arr("ttest_2samp([0, 2], [4, 6])");
        double tw = -4.0 / Math.Sqrt(2.0);
        Close(tw, welch[0], 1e-12);
        Close(1.0 - (Math.Abs(tw) / Math.Sqrt((tw * tw) + 2.0)), welch[1], 1e-10);
        Close(2.0, welch[2], 1e-12);

        decimal[] unequal = Arr("ttest_2samp([1, 2, 3, 4], [2, 4, 6, 8, 10])");
        double vx = (5.0 / 3.0) / 4.0;
        double vy = 10.0 / 5.0;
        Close(-3.5 / Math.Sqrt(vx + vy), unequal[0], 1e-12);
        Close(((vx + vy) * (vx + vy)) / ((vx * vx / 3.0) + (vy * vy / 4.0)), unequal[2], 1e-10);
        Assert.InRange(unequal[1], 0.05m, 0.15m);

        Fails("ttest_1samp([1], 0)", CalculatorErrorCode.DomainError);
        Fails("ttest_1samp([2, 2, 2], 0)", CalculatorErrorCode.DivisionByZero);
        Fails("ttest_2samp([1], [1, 2])", CalculatorErrorCode.DomainError);
        Fails("ttest_2samp([1, 1], [1, 1])", CalculatorErrorCode.DivisionByZero);
        Fails("ttest_1samp(3, 0)", CalculatorErrorCode.DomainError);
    }

    [Fact]
    public void JarqueBeraAndLjungBoxDelegateToTheExistingKernels()
    {
        double[] sample = Enumerable.Range(1, 10).Select(i => (double)i).ToArray();
        decimal[] jb = Arr($"jb_test({Vector(sample)})");
        double m2 = sample.Sum(v => Math.Pow(v - 5.5, 2)) / 10.0;
        double m4 = sample.Sum(v => Math.Pow(v - 5.5, 4)) / 10.0;
        double kurtosis = (m4 / (m2 * m2)) - 3.0;
        double expectedJb = 10.0 / 6.0 * (kurtosis * kurtosis / 4.0);
        Close(expectedJb, jb[0], 1e-9);
        Close(Math.Exp(-expectedJb / 2.0), jb[1], 1e-9);

        Fails("jb_test([1, 2, 3, 4, 5, 6, 7])", CalculatorErrorCode.DomainError);
        Fails("jb_test([5, 5, 5, 5, 5, 5, 5, 5])", CalculatorErrorCode.DivisionByZero);

        decimal[] lb = Arr($"ljung_box({Vector(sample)}, 2)");
        double mean = 5.5;
        double gamma0 = sample.Sum(v => (v - mean) * (v - mean)) / 10.0;
        double Rho(int k) => sample.Skip(k).Zip(sample, (a, b) => (a - mean) * (b - mean)).Sum() / 10.0 / gamma0;
        double q = 10.0 * 12.0 * ((Rho(1) * Rho(1) / 9.0) + (Rho(2) * Rho(2) / 8.0));
        Close(q, lb[0], 1e-9);
        Close(Math.Exp(-q / 2.0), lb[1], 1e-9);

        Fails($"ljung_box({Vector(sample)}, 0)", CalculatorErrorCode.DomainError);
        Fails($"ljung_box({Vector(sample)}, 10)", CalculatorErrorCode.DomainError);
        Fails("ljung_box([1], 1)", CalculatorErrorCode.DomainError);
        Fails("ljung_box([2, 2, 2, 2], 1)", CalculatorErrorCode.DivisionByZero);
        Fails($"ljung_box({Vector(sample)}, 1.5)", CalculatorErrorCode.DomainError);
    }

    [Fact]
    public void KolmogorovSmirnovUsesExactStatisticAndAsymptoticPValue()
    {
        Close([0.0, 1.0], Arr("ks_2samp([1, 2, 3], [1, 2, 3])"), 1e-12);

        decimal[] separated = Arr("ks_2samp([1, 2, 3, 4, 5], [6, 7, 8, 9, 10])");
        Close(1.0, separated[0], 1e-12);
        Close(0.0037815, separated[1], 2e-6);

        // Ties across the samples are consumed together: D = 5 / 12 at the value 2.
        Close(5.0 / 12.0, Arr("ks_2samp([1, 2, 2, 3], [2, 3, 4])")[0], 1e-12);
        Fails("ks_2samp(1, [1])", CalculatorErrorCode.DomainError);
    }

    // ---------------------------------------------------------------- Domain G

    [Fact]
    public void LinearInterpolationReturnsNodesExactlyAndRejectsBadGrids()
    {
        Close(5.0, Scalar("interp_linear([0, 1, 2], [0, 10, 20], 0.5)"), 1e-12);
        Assert.Equal(10m, Scalar("interp_linear([0, 1, 2], [0, 10, 20], 1)"));
        Assert.Equal(20m, Scalar("interp_linear([0, 1, 2], [0, 10, 20], 2)"));
        Assert.Equal(0m, Scalar("interp_linear([0, 1, 2], [0, 10, 20], 0)"));

        Fails("interp_linear([0, 1, 2], [0, 10, 20], -0.1)", CalculatorErrorCode.DomainError);
        Fails("interp_linear([0, 1, 2], [0, 10, 20], 2.1)", CalculatorErrorCode.DomainError);
        Fails("interp_linear([0, 1, 1], [0, 1, 2], 0.5)", CalculatorErrorCode.DomainError);
        Fails("interp_linear([0, 2, 1], [0, 1, 2], 0.5)", CalculatorErrorCode.DomainError);
        Fails("interp_linear([0, 0.0000000000005], [0, 1], 0)", CalculatorErrorCode.DomainError);
        Fails("interp_linear([0, 1], [0, 1, 2], 0.5)", CalculatorErrorCode.DimensionMismatch);
        Fails("interp_linear([1], [1], 1)", CalculatorErrorCode.DomainError);
        Fails("interp_linear(1, [1, 2], 1)", CalculatorErrorCode.DomainError);
    }

    [Fact]
    public void NaturalCubicSplineMatchesHandComputedValuesAndReproducesLines()
    {
        // Natural spline through (0,0), (1,1), (2,0): M1 = -3, S(0.5) = S(1.5) = 0.6875.
        Close(0.6875, Scalar("interp_cubic_spline([0, 1, 2], [0, 1, 0], 0.5)"), 1e-12);
        Close(0.6875, Scalar("interp_cubic_spline([0, 1, 2], [0, 1, 0], 1.5)"), 1e-12);
        Assert.Equal(1m, Scalar("interp_cubic_spline([0, 1, 2], [0, 1, 0], 1)"));

        // A straight line is reproduced exactly, also on non-uniform grids.
        Close(4.0, Scalar("interp_cubic_spline([0, 1, 2, 3], [1, 3, 5, 7], 1.5)"), 1e-12);
        Close(2.0 * 0.7 + 1.0, Scalar("interp_cubic_spline([0, 0.4, 1.5, 3], [1, 1.8, 4, 7], 0.7)"), 1e-12);

        Fails("interp_cubic_spline([0, 1], [0, 1], 0.5)", CalculatorErrorCode.DomainError);
        Fails("interp_cubic_spline([0, 1, 2], [0, 1, 0], 3)", CalculatorErrorCode.DomainError);
    }

    [Fact]
    public void PchipPreservesMonotonicityAndReducesToLinearForTwoPoints()
    {
        Close(1.5, Scalar("interp_pchip([0, 1, 2, 3], [0, 1, 2, 3], 1.5)"), 1e-12);
        Close(2.0, Scalar("interp_pchip([0, 2], [0, 4], 1)"), 1e-12);
        Close(0.5, Scalar("interp_pchip([0, 1, 2, 3, 4], [0, 0, 1, 1, 1], 1.5)"), 1e-12);
        Close(0.0, Scalar("interp_pchip([0, 1, 2, 3, 4], [0, 0, 1, 1, 1], 0.5)"), 1e-12);

        // A step-like monotone data set never leaves [0, 1].
        for (int i = 0; i <= 40; i++)
        {
            decimal v = Scalar($"interp_pchip([0, 1, 2, 3, 4], [0, 0, 1, 1, 1], {(i / 10.0).ToString(CultureInfo.InvariantCulture)})");
            Assert.InRange(v, -1e-12m, 1.000000000001m);
        }

        Fails("interp_pchip([0], [0], 0)", CalculatorErrorCode.DomainError);
        Fails("interp_pchip([0, 1, 2], [0, 1, 0], 5)", CalculatorErrorCode.DomainError);
    }

    // ---------------------------------------------------------------- Domain H: Black-Scholes-Merton

    [Fact]
    public void BlackScholesMatchesTheReferenceValues()
    {
        Close(10.4505835722, Scalar("bs_price(100, 100, 1, 0.05, 0.2, 1)"), 1e-8);
        Close(5.5735260223, Scalar("bs_price(100, 100, 1, 0.05, 0.2, 0)"), 1e-8);
        Close(0.6368306512, Scalar("bs_delta(100, 100, 1, 0.05, 0.2, 1)"), 1e-8);
        Close(0.6368306512 - 1.0, Scalar("bs_delta(100, 100, 1, 0.05, 0.2, 0)"), 1e-8);
        Close(0.0187620173, Scalar("bs_gamma(100, 100, 1, 0.05, 0.2)"), 1e-8);
        Close(37.5240346, Scalar("bs_vega(100, 100, 1, 0.05, 0.2)"), 1e-5);
        Close(-6.4140275, Scalar("bs_theta(100, 100, 1, 0.05, 0.2, 1)"), 1e-5);

        // Put-call parity with a dividend yield: C - P = S e^(-qT) - K e^(-rT).
        double parity = (double)Scalar("bs_price(100, 95, 0.5, 0.03, 0.3, 1, 0.02)") - (double)Scalar("bs_price(100, 95, 0.5, 0.03, 0.3, 0, 0.02)");
        Assert.Equal((100.0 * Math.Exp(-0.02 * 0.5)) - (95.0 * Math.Exp(-0.03 * 0.5)), parity, 8);
    }

    [Fact]
    public void BlackScholesGreeksAreConsistentWithFiniteDifferencesOfThePrice()
    {
        double Price(double s, double t, double sigma, int isCall)
            => (double)Scalar(FormattableString.Invariant($"bs_price({s}, 100, {t}, 0.03, {sigma}, {isCall}, 0.01)"));

        const double h = 1e-4;
        double delta = (Price(100 + h, 1, 0.25, 1) - Price(100 - h, 1, 0.25, 1)) / (2 * h);
        double gamma = (Price(100 + h, 1, 0.25, 1) - (2 * Price(100, 1, 0.25, 1)) + Price(100 - h, 1, 0.25, 1)) / (h * h);
        double vega = (Price(100, 1, 0.25 + h, 1) - Price(100, 1, 0.25 - h, 1)) / (2 * h);
        double theta = -(Price(100, 1 + h, 0.25, 0) - Price(100, 1 - h, 0.25, 0)) / (2 * h);

        Close(delta, Scalar("bs_delta(100, 100, 1, 0.03, 0.25, 1, 0.01)"), 1e-6);
        Close(gamma, Scalar("bs_gamma(100, 100, 1, 0.03, 0.25, 0.01)"), 1e-5);
        Close(vega, Scalar("bs_vega(100, 100, 1, 0.03, 0.25, 0.01)"), 1e-5);
        Close(theta, Scalar("bs_theta(100, 100, 1, 0.03, 0.25, 0, 0.01)"), 1e-5);
    }

    [Fact]
    public void BlackScholesBoundariesAndErrors()
    {
        // T = 0 returns the intrinsic value, whatever the volatility.
        Assert.Equal(10m, Scalar("bs_price(110, 100, 0, 0.05, 0.2, 1)"));
        Assert.Equal(0m, Scalar("bs_price(110, 100, 0, 0.05, 0.2, 0)"));
        Assert.Equal(10m, Scalar("bs_price(90, 100, 0, 0.05, 0, 0)"));

        Fails("bs_price(0, 100, 1, 0.05, 0.2, 1)", CalculatorErrorCode.DomainError);
        Fails("bs_price(100, 0, 1, 0.05, 0.2, 1)", CalculatorErrorCode.DomainError);
        Fails("bs_price(100, 100, -1, 0.05, 0.2, 1)", CalculatorErrorCode.DomainError);
        Fails("bs_price(100, 100, 1, 0.05, 0, 1)", CalculatorErrorCode.DomainError);
        Fails("bs_price(100, 100, 1, 0.05, 0.2, 2)", CalculatorErrorCode.DomainError);
        Fails("bs_price(100, 100, 1, 0.05, 0.2, 0.5)", CalculatorErrorCode.DomainError);
        Fails("bs_price([100], 100, 1, 0.05, 0.2, 1)", CalculatorErrorCode.DomainError);
        Fails("bs_delta(100, 100, 0, 0.05, 0.2, 1)", CalculatorErrorCode.DomainError);
        Fails("bs_gamma(100, 100, 0, 0.05, 0.2)", CalculatorErrorCode.DomainError);
        Fails("bs_vega(100, 100, 1, 0.05, 0)", CalculatorErrorCode.DomainError);
        Fails("bs_theta(100, 100, 1, 0.05, 0.2, 3)", CalculatorErrorCode.DomainError);
    }

    [Fact]
    public void ImpliedVolatilityRecoversTheInputAndChecksNoArbitrageBounds()
    {
        double call = (double)Scalar("bs_price(100, 105, 0.75, 0.02, 0.37, 1, 0.01)");
        double put = (double)Scalar("bs_price(100, 105, 0.75, 0.02, 0.37, 0, 0.01)");
        Close(0.37, Scalar(FormattableString.Invariant($"bs_implied_vol({call}, 100, 105, 0.75, 0.02, 1, 0.01)")), 1e-8);
        Close(0.37, Scalar(FormattableString.Invariant($"bs_implied_vol({put}, 100, 105, 0.75, 0.02, 0, 0.01)")), 1e-8);

        // Below the lower bound (intrinsic 10), at or above the upper bound (S), invalid S/K/T and flag.
        Fails("bs_implied_vol(3, 100, 90, 1, 0, 1)", CalculatorErrorCode.DomainError);
        Fails("bs_implied_vol(10, 100, 90, 1, 0, 1)", CalculatorErrorCode.DomainError);
        Fails("bs_implied_vol(100, 100, 90, 1, 0, 1)", CalculatorErrorCode.DomainError);
        Fails("bs_implied_vol(5, 100, 100, 0, 0, 1)", CalculatorErrorCode.DomainError);
        Fails("bs_implied_vol(5, 0, 100, 1, 0, 1)", CalculatorErrorCode.DomainError);
        Fails("bs_implied_vol(5, 100, 100, 1, 0, 2)", CalculatorErrorCode.DomainError);
        Fails("bs_implied_vol(1000, 100, 100, 1, 0, 0)", CalculatorErrorCode.DomainError);
    }

    // ---------------------------------------------------------------- Domain H: cash flows, risk, information

    [Fact]
    public void NpvAndIrrUseTimeZeroConvention()
    {
        Close(-100.0 + (60.0 / 1.1) + (60.0 / 1.21), Scalar("npv(0.1, [-100, 60, 60])"), 1e-10);
        Close(7.0, Scalar("npv(0, [3, 4])"), 1e-12);
        Close(100.0, Scalar("npv(0.5, [100])"), 1e-12);

        decimal irr = Scalar("irr([-100, 60, 60])");
        double u = (-1.0 + Math.Sqrt(1.0 + (20.0 / 3.0))) / 2.0;
        Close((1.0 / u) - 1.0, irr, 1e-9);
        Close(0.0, Scalar($"npv({irr.ToString(CultureInfo.InvariantCulture)}, [-100, 60, 60])"), 1e-8);

        Close(0.1, Scalar("irr([-100, 110])"), 1e-9);
        Close(-0.5, Scalar("irr([-100, 50], 0.1)"), 1e-9);

        // A start rate far from the root leaves the valid range, so the bracketed Brent fallback is used.
        Close(0.1, Scalar("irr([-100, 110], 50)"), 1e-8);

        Fails("npv(-1, [1, 2])", CalculatorErrorCode.DomainError);
        Fails("npv(-2, [1, 2])", CalculatorErrorCode.DomainError);
        Fails("npv(0.1, 5)", CalculatorErrorCode.DomainError);
        Fails("irr([100])", CalculatorErrorCode.DomainError);
        Fails("irr([100, 50, 20])", CalculatorErrorCode.DomainError);
        Fails("irr([-100, -50])", CalculatorErrorCode.DomainError);
        Fails("irr([0, 0, 5])", CalculatorErrorCode.DomainError);
        Fails("irr([-100, 110], -1)", CalculatorErrorCode.DomainError);
        Fails("irr(5)", CalculatorErrorCode.DomainError);
    }

    /// <summary>The IRR is the root of a homogeneous equation, so multiplying every cash flow by a positive factor must not change it.</summary>
    [Theory]
    [InlineData("-0.0000000000000100", "0.0000000000000110")]
    [InlineData("-100", "110")]
    [InlineData("-100000000000", "110000000000")]
    [InlineData("-100000000000000000", "110000000000000000")]
    public void IrrIsInvariantToTheScaleOfTheCashFlows(string outflow, string inflow)
    {
        Close(0.1, Scalar($"irr([{outflow}, {inflow}])"), 1e-8);
        Close(0.5, Scalar($"irr([{outflow}, {ScaleBy(inflow, 150m / 110m)}])"), 1e-8);
    }

    private static string ScaleBy(string value, decimal factor)
        => (decimal.Parse(value, CultureInfo.InvariantCulture) * factor).ToString("0.############################", CultureInfo.InvariantCulture);

    [Fact]
    public void IrrDoesNotReturnTheStartRateForTinyCashFlows()
    {
        // |f(0.1)| = 3.6e-14 is below the old absolute tolerance, so the start rate used to be returned as the root.
        Close(0.5, Scalar("irr([-0.0000000000001, 0.00000000000015])"), 1e-9);
        Close(1.0, Scalar("irr([-0.000000000000001, 0.000000000000002])"), 1e-9);
    }

    [Fact]
    public void IrrConvergesByNewtonAtEveryScale()
    {
        // The Newton phase charges one budget unit per iteration, the bracketed fallback at least 64: a small count proves the fallback was not needed.
        foreach (double scale in new[] { 1e-12, 1.0, 1e6, 1e12 })
        {
            double[] cashFlows = [-1.0 * scale, 0.6 * scale, 0.6 * scale];
            var budget = DslExecutionBudget.CreateDefault();
            MathResult<double> irr = DslQuantAndInfoMath.Irr(cashFlows, DslQuantAndInfoMath.IrrDefaultGuess, ref budget);

            Assert.True(irr.IsOk, $"scale {scale}");
            Assert.InRange(DslExecutionBudget.DefaultMaxOps - budget.RemainingOps, 1, 20);
            double u = (-0.6 + Math.Sqrt(0.36 + 2.4)) / 1.2;
            Assert.Equal((1.0 / u) - 1.0, irr.Value, 8);
        }
    }

    [Fact]
    public void MaxDrawdownIsExactDecimalAndNonNegative()
    {
        Assert.Equal(0.5m, Scalar("max_drawdown([100, 120, 90, 130, 65])"));
        Assert.Equal(0.25m, Scalar("max_drawdown([100, 120, 90, 130])"));
        Assert.Equal(0m, Scalar("max_drawdown([1, 2, 3, 4])"));
        Assert.Equal(0m, Scalar("max_drawdown([5, 5])"));
        Assert.Equal(1m / 3m, Scalar("max_drawdown([3, 2])"), 20);

        Fails("max_drawdown([100])", CalculatorErrorCode.DomainError);
        Fails("max_drawdown([100, 0, 50])", CalculatorErrorCode.DomainError);
        Fails("max_drawdown([100, -5, 50])", CalculatorErrorCode.DomainError);
        Fails("max_drawdown(100)", CalculatorErrorCode.DomainError);
    }

    [Fact]
    public void EmpiricalCvarUsesTheDecimalExactTailCount()
    {
        const string R = "[0.09, -0.1, 0.02, -0.05, 0.03, 0.04, 0.05, 0.06, 0.07, 0.08]";

        // n = 10, c = 0.8: k = floor(0.2 * 10) = 2 -> mean of the three worst returns, as a positive loss.
        Assert.Equal(0.13m / 3m, Scalar($"cvar_emp({R}, 0.8)"));

        // c = 0.9: (1 - c) n is exactly 1 in decimal, so k = 1 (the double product 0.09999.. * 10 would give k = 0).
        Assert.Equal(0.075m, Scalar($"cvar_emp({R}, 0.9)"));
        Assert.Equal(0.1m, Scalar($"cvar_emp({R}, 0.99)"));
        Assert.Equal(-0.025m, Scalar("cvar_emp([0.02, 0.03], 0.5)"));

        Assert.Equal(0m, Scalar("cvar_emp([0], 0.5)"));
        Fails($"cvar_emp({R}, 0)", CalculatorErrorCode.DomainError);
        Fails($"cvar_emp({R}, 1)", CalculatorErrorCode.DomainError);
        Fails($"cvar_emp({R}, 1.5)", CalculatorErrorCode.DomainError);
        Fails("cvar_emp(0.5, 0.5)", CalculatorErrorCode.DomainError);
        Fails("cvar_emp([1, 2], [0.5])", CalculatorErrorCode.DomainError);
    }

    [Fact]
    public void EntropyAndKlDivergenceAreInNats()
    {
        Close(Math.Log(2.0), Scalar("shannon_entropy([0.5, 0.5])"), 1e-12);
        Close(0.0, Scalar("shannon_entropy([1, 0])"), 1e-12);
        Close(Math.Log(4.0), Scalar("shannon_entropy([0.25, 0.25, 0.25, 0.25])"), 1e-12);
        Fails("shannon_entropy([0.5, 0.6])", CalculatorErrorCode.DomainError);
        Fails("shannon_entropy([1.5, -0.5])", CalculatorErrorCode.DomainError);
        Fails("shannon_entropy(0.5)", CalculatorErrorCode.DomainError);

        Close((0.5 * Math.Log(2.0)) + (0.5 * Math.Log(0.5 / 0.75)), Scalar("kl_divergence([0.5, 0.5], [0.25, 0.75])"), 1e-12);
        Close(0.0, Scalar("kl_divergence([0.3, 0.7], [0.3, 0.7])"), 1e-12);
        Close(Math.Log(2.0), Scalar("kl_divergence([1, 0], [0.5, 0.5])"), 1e-12);
        Fails("kl_divergence([0.5, 0.5], [1, 0])", CalculatorErrorCode.DomainError);
        Fails("kl_divergence([0.5, 0.5], [0.5, 0.25, 0.25])", CalculatorErrorCode.DimensionMismatch);
        Fails("kl_divergence([0.5, 0.6], [0.5, 0.5])", CalculatorErrorCode.DomainError);
        Fails("kl_divergence([0.5, 0.5], [0.4, 0.4])", CalculatorErrorCode.DomainError);
    }

    // ---------------------------------------------------------------- Domain I

    [Fact]
    public void GoertzelMatchesTheExistingKernelAndRecoversAmplitude()
    {
        Close(3.0, Scalar("goertzel([9, 3, 0, -3, 0, 3, 0, -3, 0], 4)"), 1e-12);

        foreach (int period in new[] { 2, 3, 5 })
        {
            double[] series = [1.5, -2.0, 0.5, 4.0, -1.0, 2.5, 0.0];
            Span<double> expected = stackalloc double[period];
            GoertzelMath.CalculateRollingAmplitude(series.AsSpan(series.Length - period, period), period, expected);
            Close(expected[period - 1], Scalar($"goertzel({Vector(series)}, {period})"), 1e-12);
        }

        // Period 2: bin 1 is the Nyquist bin, so an alternating series returns its amplitude (not twice).
        Close(1.0, Scalar("goertzel([1, -1, 1, -1], 2)"), 1e-12);
        Close(2.0, Scalar("goertzel([5, 3, 5, 1], 2)"), 1e-12);

        Fails("goertzel([1, 2, 3], 1)", CalculatorErrorCode.DomainError);
        Fails("goertzel([1, 2, 3], 4)", CalculatorErrorCode.DomainError);
        Fails("goertzel([1, 2, 3], 2.5)", CalculatorErrorCode.DomainError);
        Fails("goertzel([1, 2, 3], 513)", CalculatorErrorCode.DomainError);
        Fails("goertzel(5, 2)", CalculatorErrorCode.DomainError);
    }

    [Fact]
    public void DftMagnitudeReturnsTheOneSidedSpectrum()
    {
        // n = 4: A = [0, sqrt(2), 0.5] (the Nyquist bin of an even length is not doubled).
        Close([0.0, Math.Sqrt(2.0), 0.5], Arr("dft_mag([1, 2, 3, 4])"), 1e-12);

        // A cosine of amplitude 2 at bin 2 of 8 samples is recovered as 2.
        decimal[] cosine = Arr("dft_mag([2, 0, -2, 0, 2, 0, -2, 0])");
        Assert.Equal(5, cosine.Length);
        Close(2.0, cosine[2], 1e-12);
        Close(0.0, cosine[0], 1e-12);

        // A length that is not a power of two matches the naive DFT.
        double[] series = [1.0, 4.0, 2.0, 8.0, 5.0];
        decimal[] spectrum = Arr($"dft_mag({Vector(series)})");
        double mean = series.Average();
        Assert.Equal(3, spectrum.Length);
        for (int k = 0; k < spectrum.Length; k++)
        {
            double re = 0.0;
            double im = 0.0;
            for (int t = 0; t < series.Length; t++)
            {
                double angle = -2.0 * Math.PI * k * t / series.Length;
                re += (series[t] - mean) * Math.Cos(angle);
                im += (series[t] - mean) * Math.Sin(angle);
            }

            double scale = k == 0 ? 1.0 : 2.0;
            Close(scale / series.Length * Math.Sqrt((re * re) + (im * im)), spectrum[k], 1e-10);
        }

        Fails("dft_mag([5])", CalculatorErrorCode.DomainError);
        Fails("dft_mag(5)", CalculatorErrorCode.DomainError);

        // The budget cost is max(1, n (n / 2 + 1) / 32).
        var budget = DslExecutionBudget.CreateDefault();
        double[] longSeries = Enumerable.Range(0, 64).Select(i => Math.Sin(i)).ToArray();
        Span<double> bins = stackalloc double[33];
        Assert.Equal(MathErrorCode.None, DslQuantAndInfoMath.DftMagnitude(longSeries, bins, ref budget));
        Assert.Equal(DslExecutionBudget.DefaultMaxOps - (64 * 33 / 32), budget.RemainingOps);
        var tiny = DslExecutionBudget.Create(1);
        Assert.Equal(MathErrorCode.BudgetExceeded, DslQuantAndInfoMath.DftMagnitude(longSeries, bins, ref tiny));
    }

    // ---------------------------------------------------------------- Registration & composition

    [Fact]
    public void ArrayReturningTestsComposeAsArrayArguments()
    {
        Assert.Equal(3m, Scalar("arr_len(ttest_2samp([1, 2, 3], [2, 3, 5]))"));
        Assert.Equal(3m, Scalar("arr_len(dft_mag([1, 2, 3, 4]))"));
        Assert.Equal(2m, Scalar("arr_len(ks_2samp([1, 2, 3], [2, 3, 4]))"));
        Close(Math.Sqrt(2.0), Scalar("max(dft_mag([1, 2, 3, 4]))"), 1e-12);
    }

    [Fact]
    public void Task2ArrayFunctionsAreRegisteredAsArrayReturning()
    {
        string[] arrayFunctions = ["ttest_1samp", "ttest_2samp", "jb_test", "ljung_box", "ks_2samp", "dft_mag"];
        Assert.All(arrayFunctions, name => Assert.True(CalculatorArrayFunctions.ReturnsArray(name), name));
        // Exact total registry count (760 in v8) is enforced in CalculatorContractTests; verify uniqueness and v5 subset here.
        Assert.Equal(ExpressionCalculator.Functions.Count, ExpressionCalculator.Functions.Select(f => f.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(ExpressionCalculator.Functions, f => f.Name == "dft_mag");
    }

    [Fact]
    public void LargeInputsRunWithinTheDefaultBudget()
    {
        // Expression-level: the expression character and token limits bound the literal size (two 400-element arrays / one 512-element array).
        string x400 = Vector(Enumerable.Range(0, 400).Select(i => (double)((i * 7) % 10)));
        string y400 = Vector(Enumerable.Range(0, 400).Select(i => (double)((i * 3) % 10)));
        string x512 = Vector(Enumerable.Range(0, 512).Select(i => (double)((i * 7) % 10)));

        Assert.InRange(Scalar($"corr_kendall({x400}, {y400})", Heavy), -1m, 1m);
        Assert.InRange(Scalar($"corr_spearman({x400}, {y400})", Heavy), -1m, 1m);
        Assert.Equal(257, Arr($"dft_mag({x512})", Heavy).Length);
        Assert.Equal(2, Arr($"ks_2samp({x400}, {y400})", Heavy).Length);

        // Kernel-level: the longest allowed Kendall input (n = 512) costs 8176 of the 10000 default operations.
        var budget = DslExecutionBudget.CreateDefault();
        double[] a = Enumerable.Range(0, 512).Select(i => (double)((i * 7) % 13)).ToArray();
        double[] b = Enumerable.Range(0, 512).Select(i => (double)((i * 5) % 11)).ToArray();
        Assert.True(DslBivariateAndTestMath.CorrKendall(a, b, ref budget).IsOk);
        Assert.Equal(DslExecutionBudget.DefaultMaxOps - (512 * 511 / 32), budget.RemainingOps);
    }
}
