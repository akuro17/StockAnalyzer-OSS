#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Calculator;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models.Indicators;
using Xunit;

namespace StockAnalyzer.Core.Tests.Calculator;

public sealed class CalculatorDslElementaryTests
{
    private static readonly IReadOnlyDictionary<string, decimal> Empty = new Dictionary<string, decimal>(StringComparer.Ordinal);

    private static decimal EvalOk(string expr, IReadOnlyDictionary<string, decimal>? vars = null, CalculatorLimits? limits = null)
    {
        CalculatorOutcome outcome = ExpressionCalculator.Evaluate(expr, vars ?? Empty, limits ?? CalculatorLimits.Default);
        Assert.True(outcome.Error is null, $"Expected Ok for '{expr}' but got {outcome.Error}");
        return outcome.Value!.Value;
    }

    private static CalculatorError EvalErr(string expr, CalculatorErrorCode expectedCode, int expectedPos, string? expectedToken = null, IReadOnlyDictionary<string, decimal>? vars = null, CalculatorLimits? limits = null)
    {
        CalculatorOutcome outcome = ExpressionCalculator.Evaluate(expr, vars ?? Empty, limits ?? CalculatorLimits.Default);
        Assert.True(outcome.Value is null, $"Expected Error({expectedCode}) for '{expr}' but got {outcome.Value}");
        Assert.NotNull(outcome.Error);
        Assert.Equal(expectedCode, outcome.Error!.Code);
        Assert.Equal(expectedPos, outcome.Error.Position);
        if (expectedToken is not null)
        {
            Assert.Equal(expectedToken, outcome.Error.Token);
        }

        return outcome.Error;
    }

    [Fact]
    public void BuiltInConstantsResolveAndAllowCallerShadowing()
    {
        Assert.Equal(MathConstants.EDecimal, EvalOk("e"));
        Assert.Equal(MathConstants.PiDecimal, EvalOk("pi"));
        Assert.Equal(MathConstants.PhiDecimal, EvalOk("phi"));
        Assert.Equal(MathConstants.EulerGammaDecimal, EvalOk("euler_gamma"));

        var shadow = new Dictionary<string, decimal>(StringComparer.Ordinal) { ["e"] = 42m };
        Assert.Equal(42m, EvalOk("e", shadow));
    }

    [Theory]
    [InlineData("nan", 0, "nan")]
    [InlineData("inf", 0, "inf")]
    [InlineData("1 + nan", 4, "nan")]
    [InlineData("sin(inf)", 4, "inf")]
    public void NonFinitePseudoLiteralsFailWithDomainErrorAndExactPosition(string expr, int expectedPos, string expectedToken)
    {
        var shadow = new Dictionary<string, decimal>(StringComparer.Ordinal) { ["nan"] = 1m, ["inf"] = 2m };
        EvalErr(expr, CalculatorErrorCode.DomainError, expectedPos, expectedToken, shadow);
    }

    [Fact]
    public void ThreeWayErrorProjectionSeparatesDomainOverflowAndNonFinite()
    {
        // (1) Domain error on precheck
        Assert.Equal(MathErrorCode.DomainError, DslElementaryMath.Log10(0m).Error);
        Assert.Equal(MathErrorCode.DomainError, DslElementaryMath.Log1p(-1m).Error);

        // (2) Overflow on Infinity or |v| >= 7.9e27 (Rule G4)
        Assert.Equal(MathErrorCode.ArithmeticOverflow, DslElementaryMath.NarrowDouble(double.PositiveInfinity).Error);
        Assert.Equal(MathErrorCode.ArithmeticOverflow, DslElementaryMath.NarrowDouble(double.NegativeInfinity).Error);
        Assert.Equal(MathErrorCode.ArithmeticOverflow, DslElementaryMath.NarrowDouble(7.9e27).Error);
        Assert.Equal(MathErrorCode.ArithmeticOverflow, DslElementaryMath.NarrowDouble(-7.9e27).Error);

        // (3) Unexpected internal NaN maps to NonFiniteResult
        Assert.Equal(MathErrorCode.NonFiniteResult, DslElementaryMath.NarrowDouble(double.NaN).Error);
        Assert.Equal(CalculatorErrorCode.NonFiniteResult, CalculatorElementaryFunctions.FromMathError(MathErrorCode.NonFiniteResult, 0, "test").Error!.Code);
    }

    [Fact]
    public void ExecutionBudgetEnforcesOpsAndDepthBounds()
    {
        var tightOps = new CalculatorLimits { MaxExecutionOps = 3 };
        EvalErr("1 + 2 + 3 + 4", CalculatorErrorCode.BudgetExceeded, 4, limits: tightOps);

        var budget = DslExecutionBudget.Create(maxOps: 100, maxDepth: 1);
        Assert.True(budget.TryEnterDepth());
        Assert.False(budget.TryEnterDepth());
        Assert.Equal(MathErrorCode.BudgetExceeded, budget.LastError);
        budget.ExitDepth();
        Assert.True(budget.TryEnterDepth());

        var opsBudget = DslExecutionBudget.Create(maxOps: 2);
        Assert.True(opsBudget.TryConsume(1));
        Assert.True(opsBudget.TryConsume(1));
        Assert.False(opsBudget.TryConsume(1));
        Assert.Equal(MathErrorCode.BudgetExceeded, opsBudget.LastError);
    }

    [Fact]
    public void LogExpAndCbrtMaintainFullPrecisionAtTinyAndBoundaryInputs()
    {
        Assert.Equal(0m, EvalOk("log10(1)"));
        Assert.Equal(3m, EvalOk("log10(1000)"));
        Assert.Equal(0m, EvalOk("log2(1)"));
        Assert.Equal(10m, EvalOk("log2(1024)"));
        Assert.Equal(3m, EvalOk("log(2, 8)"));
        Assert.Equal(1m, EvalOk("log(5, 5)"));
        Assert.Equal(0m, EvalOk("log(5, 1)"));

        EvalErr("log10(0)", CalculatorErrorCode.DomainError, 0, "log10");
        EvalErr("log2(-1)", CalculatorErrorCode.DomainError, 0, "log2");
        EvalErr("log(1, 2)", CalculatorErrorCode.DomainError, 0, "log");
        EvalErr("log(2, 0)", CalculatorErrorCode.DomainError, 0, "log");

        // Pure decimal 4-term Taylor for |x| <= 1e-7m preserves tiny exponents without double round-to-zero
        decimal tiny = 1e-20m;
        decimal expm1Tiny = EvalOk("expm1(x)", new Dictionary<string, decimal> { ["x"] = tiny });
        decimal log1pTiny = EvalOk("log1p(x)", new Dictionary<string, decimal> { ["x"] = tiny });
        Assert.Equal(tiny, expm1Tiny);
        Assert.Equal(tiny, log1pTiny);
        Assert.Equal(-1m, EvalOk("expm1(-100)"));
        EvalErr("expm1(65)", CalculatorErrorCode.ArithmeticOverflow, 0);
        EvalErr("log1p(-1)", CalculatorErrorCode.DomainError, 0, "log1p");

        // Cbrt exact cubes, negative, and near-MaxValue overflow safety
        Assert.Equal(0m, EvalOk("cbrt(0)"));
        Assert.Equal(3m, EvalOk("cbrt(27)"));
        Assert.Equal(-2m, EvalOk("cbrt(-8)"));
        decimal cbrt2 = EvalOk("cbrt(2)");
        Assert.True(Math.Abs(cbrt2 * cbrt2 * cbrt2 - 2m) < 1e-27m);
        decimal cbrtMax = EvalOk("cbrt(m)", new Dictionary<string, decimal> { ["m"] = decimal.MaxValue });
        Assert.True(cbrtMax > 4_294_967_295m);
    }

    [Fact]
    public void TrigDegreeReductionAndHyperbolicBoundariesMatchContract()
    {
        Assert.Equal(0m, EvalOk("sind(0)"));
        Assert.Equal(0.5m, EvalOk("sind(30)"));
        Assert.Equal(1m, EvalOk("sind(90)"));
        Assert.Equal(0m, EvalOk("sind(180)"));
        Assert.Equal(-1m, EvalOk("sind(270)"));
        Assert.Equal(-0.5m, EvalOk("sind(-30)"));
        Assert.Equal(0.5m, EvalOk("sind(360000030)"));

        Assert.Equal(1m, EvalOk("cosd(0)"));
        Assert.Equal(0.5m, EvalOk("cosd(60)"));
        Assert.Equal(0m, EvalOk("cosd(90)"));
        Assert.Equal(-1m, EvalOk("cosd(180)"));
        Assert.Equal(0m, EvalOk("cosd(270)"));

        Assert.Equal(0m, EvalOk("tand(0)"));
        Assert.Equal(1m, EvalOk("tand(45)"));
        Assert.Equal(-1m, EvalOk("tand(135)"));
        EvalErr("tand(90)", CalculatorErrorCode.DomainError, 0, "tand");
        EvalErr("tand(-90)", CalculatorErrorCode.DomainError, 0, "tand");

        EvalErr("sin(1000000001)", CalculatorErrorCode.DomainError, 0, "sin");
        EvalErr("cos(1000000001)", CalculatorErrorCode.DomainError, 0, "cos");
        EvalErr("tan(1000000001)", CalculatorErrorCode.DomainError, 0, "tan");
        EvalErr("asin(1.1)", CalculatorErrorCode.DomainError, 0, "asin");
        EvalErr("acos(-1.1)", CalculatorErrorCode.DomainError, 0, "acos");
        EvalErr("atan2(0, 0)", CalculatorErrorCode.DomainError, 0, "atan2");
        Assert.Equal(0m, EvalOk("atan2(0, 5)"));

        Assert.Equal(0m, EvalOk("sinh(0)"));
        Assert.Equal(1m, EvalOk("cosh(0)"));
        Assert.Equal(1m, EvalOk("tanh(25)"));
        Assert.Equal(-1m, EvalOk("tanh(-25)"));
        Assert.Equal(0m, EvalOk("asinh(0)"));
        Assert.Equal(0m, EvalOk("acosh(1)"));
        Assert.Equal(0m, EvalOk("atanh(0)"));
        EvalErr("sinh(66)", CalculatorErrorCode.ArithmeticOverflow, 0);
        EvalErr("cosh(66)", CalculatorErrorCode.ArithmeticOverflow, 0);
        EvalErr("acosh(0.5)", CalculatorErrorCode.DomainError, 0, "acosh");
        EvalErr("atanh(1)", CalculatorErrorCode.DomainError, 0, "atanh");
        EvalErr("atanh(-1)", CalculatorErrorCode.DomainError, 0, "atanh");
    }

    [Fact]
    public void SequencesAndCombinatoricsHandleExactArithmeticAndOverflowBounds()
    {
        Assert.Equal(5050m, EvalOk("sum_arith(1, 1, 100)"));
        Assert.Equal(15m, EvalOk("sum_geom(1, 2, 4)"));
        Assert.Equal(50m, EvalOk("sum_geom(5, 1, 10)"));
        Assert.Equal(0m, EvalOk("sum_geom(5, -1, 4)"));
        Assert.Equal(5m, EvalOk("sum_geom(5, -1, 5)"));

        Assert.Equal(1m, EvalOk("fact(0)"));
        Assert.Equal(120m, EvalOk("fact(5)"));
        EvalErr("fact(28)", CalculatorErrorCode.ArithmeticOverflow, 0);
        EvalErr("fact(-1)", CalculatorErrorCode.DomainError, 0, "fact");

        Assert.Equal(1m, EvalOk("double_fact(-1)"));
        Assert.Equal(1m, EvalOk("double_fact(0)"));
        Assert.Equal(105m, EvalOk("double_fact(7)"));
        EvalErr("double_fact(-2)", CalculatorErrorCode.DomainError, 0, "double_fact");
        EvalErr("double_fact(46)", CalculatorErrorCode.ArithmeticOverflow, 0);

        Assert.Equal(210m, EvalOk("perm(7, 3)"));
        Assert.Equal(210m, EvalOk("comb(10, 4)"));
        // comb(98, 49) fits in decimal without intermediate factorial overflow!
        Assert.Equal(25477612258980856902730428600m, EvalOk("comb(98, 49)"));
        EvalErr("comb(100, 50)", CalculatorErrorCode.ArithmeticOverflow, 0);

        Assert.Equal(0m, EvalOk("lcomb(1000, 0)"));
        Assert.True(EvalOk("lcomb(1000, 500)") > 689m);
        Assert.Equal(55m, EvalOk("fib(10)"));
        Assert.Equal(50095301248058391139327916261m, EvalOk("fib(139)"));
        EvalErr("fib(140)", CalculatorErrorCode.ArithmeticOverflow, 0);
        Assert.Equal(123m, EvalOk("lucas(10)"));
        Assert.Equal(69230003648151669220777340178m, EvalOk("lucas(138)"));
        EvalErr("lucas(139)", CalculatorErrorCode.ArithmeticOverflow, 0);
    }

    [Fact]
    public void EquationsGeometryComplexStatsAndActivationsMatchContract()
    {
        Assert.Equal(-2m, EvalOk("solve_linear(3, 6)"));
        EvalErr("solve_linear(0, 1)", CalculatorErrorCode.DivisionByZero, 0);

        Assert.Equal(1m, EvalOk("quad_discriminant(1, -5, 6)"));
        Assert.Equal(2m, EvalOk("quad_root_min(1, -5, 6)"));
        Assert.Equal(3m, EvalOk("quad_root_max(1, -5, 6)"));
        EvalErr("quad_root_min(1, 0, 1)", CalculatorErrorCode.DomainError, 0, "quad_root_min");

        // Polynomial Span<decimal> solvers
        Span<decimal> roots = stackalloc decimal[4];
        Assert.Equal(MathErrorCode.None, DslElementaryMath.SolveCubic(1m, -6m, 11m, -6m, roots, out int cubicCount));
        Assert.Equal(3, cubicCount);
        Assert.True(Math.Abs(roots[0] - 1m) < 1e-12m);
        Assert.True(Math.Abs(roots[1] - 2m) < 1e-12m);
        Assert.True(Math.Abs(roots[2] - 3m) < 1e-12m);

        Assert.Equal(MathErrorCode.None, DslElementaryMath.SolveQuartic(1m, -10m, 35m, -50m, 24m, roots, out int quarticCount));
        Assert.Equal(4, quarticCount);
        Assert.True(Math.Abs(roots[0] - 1m) < 1e-11m);
        Assert.True(Math.Abs(roots[3] - 4m) < 1e-11m);

        // Geometry & Complex projections
        Assert.Equal(5m, EvalOk("hypot(3, 4)"));
        Assert.Equal(5m, EvalOk("dist2d(1, 2, 4, 6)"));
        Assert.Equal(7m, EvalOk("dist3d(0, 0, 0, 2, 3, 6)"));
        Assert.Equal(6m, EvalOk("tri_area_sss(3, 4, 5)"));
        Assert.Equal(1m, EvalOk("tri_inradius(3, 4, 5)"));
        Assert.Equal(2.5m, EvalOk("tri_circumradius(3, 4, 5)"));
        EvalErr("tri_area_sss(1, 2, 3)", CalculatorErrorCode.DomainError, 0, "tri_area_sss");
        Assert.True(EvalOk("sphere_vol(1)") > 4.18m);
        Assert.True(EvalOk("cone_area(3, 4)") > 75.3m);
        Assert.True(EvalOk("torus_vol(5, 2)") > 394m);
        EvalErr("torus_vol(2, 5)", CalculatorErrorCode.DomainError, 0, "torus_vol");

        Assert.Equal(5m, EvalOk("c_abs(3, 4)"));
        Assert.Equal(0m, EvalOk("c_arg(5, 0)"));
        EvalErr("c_arg(0, 0)", CalculatorErrorCode.DomainError, 0, "c_arg");
        Assert.Equal(1m, EvalOk("c_exp_re(0, 0)"));
        Assert.Equal(0m, EvalOk("c_exp_im(0, 0)"));
        Assert.Equal(0.5m, EvalOk("bayes(0.1, 0.5, 0.1)"));
        EvalErr("bayes(0.5, 0.8, 0.1)", CalculatorErrorCode.DomainError, 0, "bayes");

        // Statistical variadics up to 32 arguments (while min/sum remain capped at 8)
        string args32 = string.Join(", ", Enumerable.Range(1, 32));
        Assert.Equal(16.5m, EvalOk($"median({args32})"));
        Assert.Equal(85.25m, EvalOk($"var_p({args32})"));
        Assert.Equal(88m, EvalOk($"var_s({args32})"));
        Assert.Equal(0m, EvalOk("var_p(1000000.0000000001, 1000000.0000000002)"));
        Assert.True(EvalOk("stdev_p(2, 4, 4, 4, 5, 5, 7, 9)") == 2m);
        Assert.True(EvalOk("stdev_s(1, 3)") > 1.414m);

        string args33 = string.Join(", ", Enumerable.Range(1, 33));
        EvalErr($"median({args33})", CalculatorErrorCode.WrongArgumentCount, 0, "median");
        EvalErr("sum(1, 2, 3, 4, 5, 6, 7, 8, 9)", CalculatorErrorCode.WrongArgumentCount, 0, "sum");

        // Activations
        Assert.Equal(0.5m, EvalOk("sigmoid(0)"));
        Assert.Equal(1m, EvalOk("sigmoid(50)"));
        Assert.Equal(0m, EvalOk("sigmoid(-50)"));
        Assert.Equal(0m, EvalOk("relu(-3)"));
        Assert.Equal(3m, EvalOk("relu(3)"));
        Assert.Equal(-0.3m, EvalOk("leaky_relu(-3, 0.1)"));
        Assert.Equal(2m, EvalOk("elu(2, 1)"));
        Assert.Equal(0m, EvalOk("gelu(0)"));
        Assert.Equal(0m, EvalOk("silu(0)"));
        Assert.Equal(35m, EvalOk("softplus(35)"));
        Assert.Equal(0m, EvalOk("swish(0, 1)"));
    }

    [Fact]
    public void ZeroAllocationMathUtilsKernelsAllocateZeroBytes()
    {
        Span<decimal> stats = stackalloc decimal[] { 1m, 2m, 3m, 4m, 5m, 6m, 7m, 8m };
        Span<decimal> roots = stackalloc decimal[4];
        Span<DslPoint2D> poly = stackalloc DslPoint2D[] { new(0m, 0m), new(4m, 0m), new(4m, 3m), new(0m, 3m) };

        // Warmup JIT
        _ = DslElementaryMath.Cbrt(2m);
        _ = DslElementaryMath.Expm1(1e-8m);
        _ = DslElementaryMath.Log1p(1e-8m);
        _ = DslElementaryMath.SinDeg(37m);
        _ = DslElementaryMath.Variance(stats, 1);
        _ = DslElementaryMath.Median(stats);
        _ = DslElementaryMath.SolveQuadratic(1m, -5m, 6m, roots, out _);
        _ = DslElementaryMath.PolygonArea(poly);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            _ = DslElementaryMath.Cbrt(2m);
            _ = DslElementaryMath.Expm1(1e-8m);
            _ = DslElementaryMath.Log1p(1e-8m);
            _ = DslElementaryMath.SinDeg(37m);
            _ = DslElementaryMath.Variance(stats, 1);
            _ = DslElementaryMath.Median(stats);
            _ = DslElementaryMath.SolveQuadratic(1m, -5m, 6m, roots, out _);
            _ = DslElementaryMath.PolygonArea(poly);
        }

        long after = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(0L, after - before);
    }

    [Fact]
    public void Step1Fixes_MaintainPrecisionScaleInvarianceAndBudgetOrthogonality()
    {
        // (1) cbrt tiny-argument 10^(3k) pre-scaling recovers full 28-digit decimal precision
        decimal tinyCbrt = EvalOk("cbrt(0.0000000000000000000000000002)");
        Assert.Equal(0.0000000005848035476425732131m, tinyCbrt);

        decimal midSmallCbrt = EvalOk("cbrt(0.000000000000000003)");
        Assert.True(Math.Abs(midSmallCbrt - 0.0000014422495703074083823216m) <= 1e-28m);

        // (2) expm1 transition & decimal Taylor expansion eliminate 1e-4 discontinuity
        decimal expm1_1e4 = EvalOk("expm1(0.0001)");
        Assert.True(Math.Abs(expm1_1e4 - 0.0001000050001666708334166681m) <= 1e-27m);

        decimal expm1_025 = EvalOk("expm1(0.25)");
        Assert.True(Math.Abs(expm1_025 - 0.2840254166877414840734205681m) < 1e-15m);

        // (3) lcomb cancellation-free evaluation for k << n up to n = 1e9
        decimal lc1 = EvalOk("lcomb(1000000000, 1)");
        decimal expectedLc1 = StockAnalyzer.Core.Models.Indicators.IndicatorCalculationHelper.ToDecimalOrNull(Math.Log(1_000_000_000.0))!.Value;
        Assert.True(Math.Abs(lc1 - expectedLc1) < 1e-14m);

        decimal lc2 = EvalOk("lcomb(1000000000, 2)");
        decimal expectedLc2 = StockAnalyzer.Core.Models.Indicators.IndicatorCalculationHelper.ToDecimalOrNull(Math.Log(999_999_999.0) + Math.Log(500_000_000.0))!.Value;
        Assert.True(Math.Abs(lc2 - expectedLc2) < 1e-13m);

        decimal lc100 = EvalOk("lcomb(1000000000, 100)");
        double exactLc100 = 0.0;
        for (long j = 1; j <= 100; j++)
        {
            exactLc100 += Math.Log((double)(1_000_000_000L - 100L + j) / j);
        }

        Assert.True(Math.Abs((double)lc100 - exactLc100) / exactLc100 < 1e-14);

        // (4) Polynomial scale invariance (tiny & huge) + b = 0 quadratic + Ferrari shift-cancellation quartic
        Span<decimal> roots = stackalloc decimal[4];

        Assert.Equal(MathErrorCode.None, DslElementaryMath.SolveQuadratic(1m, 0m, -4m, roots, out int q0Count));
        Assert.Equal(2, q0Count);
        Assert.Equal(-2m, roots[0]);
        Assert.Equal(2m, roots[1]);

        Assert.Equal(MathErrorCode.None, DslElementaryMath.SolveQuadratic(1e-20m, -3e-20m, 2e-20m, roots, out int qTinyCount));
        Assert.Equal(2, qTinyCount);
        Assert.Equal(1m, roots[0]);
        Assert.Equal(2m, roots[1]);

        Assert.Equal(MathErrorCode.None, DslElementaryMath.SolveQuadratic(1e20m, -3e20m, 2e20m, roots, out int qHugeCount));
        Assert.Equal(2, qHugeCount);
        Assert.Equal(1m, roots[0]);
        Assert.Equal(2m, roots[1]);

        // (y^2 + 2u y + u^2 - 1)(y^2 - 2u y + u^2 + 100) = 0 for u = 1e-7 and u = 1e-8 (p ~ 99, q ~ 2e-5/2e-6, r ~ -100)
        Assert.Equal(MathErrorCode.None, DslElementaryMath.SolveQuartic(1m, 0m, 99m, 0.0000202m, -100m, roots, out int quart7Count));
        Assert.Equal(2, quart7Count);
        Assert.True(Math.Abs(roots[0] - (-1.0000001m)) < 1e-5m);
        Assert.True(Math.Abs(roots[1] - 0.9999999m) < 1e-5m);

        Assert.Equal(MathErrorCode.None, DslElementaryMath.SolveQuartic(1m, 0m, 99m, 0.00000202m, -100m, roots, out int quart8Count));
        Assert.Equal(2, quart8Count);
        Assert.True(Math.Abs(roots[0] - (-1.00000001m)) < 1e-5m);
        Assert.True(Math.Abs(roots[1] - 0.99999999m) < 1e-5m);

        // (5) CalculatorLimits orthogonality: MaxStatisticalFunctionArguments = 4 must not break fixed 9-arg det3,
        // and MaxNestingDepth = 24 must permit 20-level nesting in CalculatorEvaluator.
        var narrowStatsWideFixed = new CalculatorLimits
        {
            MaxFunctionArguments = 16,
            MaxStatisticalFunctionArguments = 4,
        };
        Assert.Equal(1m, EvalOk("det3(1,0,0, 0,1,0, 0,0,1)", limits: narrowStatsWideFixed));
        EvalErr("geomean(1, 2, 3, 4, 5)", CalculatorErrorCode.WrongArgumentCount, 0, "geomean", limits: narrowStatsWideFixed);
        EvalErr("clamp(1, 0, 2, 3)", CalculatorErrorCode.WrongArgumentCount, 0, "clamp", limits: narrowStatsWideFixed);

        var deepLimits = new CalculatorLimits { MaxNestingDepth = 24 };
        string unary20 = new string('-', 20) + "1";
        Assert.Equal(1m, EvalOk(unary20, limits: deepLimits));

        // (6) Heavy special function budget consumption prevents multiplicative DoS inside higher-order calls
        var tightBudget = new CalculatorLimits { MaxExecutionOps = 10 };
        EvalErr("hyp2f1(1.5, 2.0, 2.5, 0.5)", CalculatorErrorCode.BudgetExceeded, 0, limits: tightBudget);

        // (7) Zero-allocation check across updated Step 1 kernels
        _ = DslElementaryMath.Cbrt(2e-28m);
        _ = DslElementaryMath.Expm1(0.0001m);
        _ = DslElementaryMath.LComb(1_000_000_000m, 100m);
        _ = DslElementaryMath.SolveQuartic(1m, 0m, 99m, 0.0000202m, -100m, roots, out _);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            _ = DslElementaryMath.Cbrt(2e-28m);
            _ = DslElementaryMath.Expm1(0.0001m);
            _ = DslElementaryMath.LComb(1_000_000_000m, 100m);
            _ = DslElementaryMath.SolveQuartic(1m, 0m, 99m, 0.0000202m, -100m, roots, out _);
        }

        long after = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(0L, after - before);
    }
}
