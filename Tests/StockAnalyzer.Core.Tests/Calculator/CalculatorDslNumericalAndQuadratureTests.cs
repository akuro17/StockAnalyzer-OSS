#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using StockAnalyzer.Core.Calculator;
using StockAnalyzer.Core.MathUtils;
using Xunit;

namespace StockAnalyzer.Core.Tests.Calculator;

public sealed class CalculatorDslNumericalAndQuadratureTests
{
    private static readonly IReadOnlyDictionary<string, decimal> Empty = new Dictionary<string, decimal>(StringComparer.Ordinal);

    private readonly struct PolyRootFunc : IScalarFunction
    {
        public bool TryEvaluate(double x, ref DslExecutionBudget budget, out double y)
        {
            y = x * x - 2.0;
            return true;
        }
    }

    private readonly struct PolyDerivFunc : IScalarFunction
    {
        public bool TryEvaluate(double x, ref DslExecutionBudget budget, out double y)
        {
            y = 2.0 * x;
            return true;
        }
    }

    private readonly struct SinFunc : IScalarFunction
    {
        public bool TryEvaluate(double x, ref DslExecutionBudget budget, out double y)
        {
            y = Math.Sin(x);
            return true;
        }
    }

    private readonly struct InvSqrtSingularityFunc : IScalarFunction
    {
        public bool TryEvaluate(double x, ref DslExecutionBudget budget, out double y)
        {
            if (x <= 0.0)
            {
                budget.LastError = MathErrorCode.DomainError;
                y = double.NaN;
                return false;
            }

            y = 1.0 / Math.Sqrt(x);
            return true;
        }
    }

    private readonly struct ExpDecayFunc : IScalarFunction
    {
        public bool TryEvaluate(double x, ref DslExecutionBudget budget, out double y)
        {
            y = Math.Exp(-x);
            return true;
        }
    }

    private readonly struct GaussianFunc : IScalarFunction
    {
        public bool TryEvaluate(double x, ref DslExecutionBudget budget, out double y)
        {
            y = Math.Exp(-x * x);
            return true;
        }
    }

    private readonly struct MonomialPowFunc : IScalarFunction
    {
        private readonly int _power;

        public MonomialPowFunc(int power) => _power = power;

        public bool TryEvaluate(double x, ref DslExecutionBudget budget, out double y)
        {
            y = Math.Pow(x, _power);
            return true;
        }
    }

    private readonly struct ConstOneFunc : IScalarFunction
    {
        public bool TryEvaluate(double x, ref DslExecutionBudget budget, out double y)
        {
            y = 1.0;
            return true;
        }
    }

    private readonly struct ReciprocalGapFunc : IScalarFunction
    {
        public bool TryEvaluate(double x, ref DslExecutionBudget budget, out double y)
        {
            if (Math.Abs(x) < 1e-12)
            {
                budget.LastError = MathErrorCode.DivisionByZero;
                y = double.NaN;
                return false;
            }

            y = 1.0 / x;
            return true;
        }
    }

    private readonly struct FlatZeroDerivFunc : IScalarFunction
    {
        public bool TryEvaluate(double x, ref DslExecutionBudget budget, out double y)
        {
            y = x * x + 1.0;
            return true;
        }
    }

    private readonly struct FlatDerivFunc : IScalarFunction
    {
        public bool TryEvaluate(double x, ref DslExecutionBudget budget, out double y)
        {
            y = 2.0 * x;
            return true;
        }
    }

    private readonly struct GoldenCfTerms : IContinuedFractionTerms
    {
        public bool TryGetTerm(int n, ref DslExecutionBudget budget, out double a, out double b)
        {
            a = 1.0;
            b = 1.0;
            return true;
        }
    }

    private readonly struct NonConvergentCfTerms : IContinuedFractionTerms
    {
        public bool TryGetTerm(int n, ref DslExecutionBudget budget, out double a, out double b)
        {
            a = -1.0;
            b = 0.5;
            return true;
        }
    }

    private readonly struct HarmonicOscillatorOde : IOdeSystem
    {
        public int Dimension => 2;

        public bool TryEvaluate(double t, scoped ReadOnlySpan<double> y, scoped Span<double> dydt, ref DslExecutionBudget budget)
        {
            dydt[0] = y[1];
            dydt[1] = -y[0];
            return true;
        }
    }

    private readonly struct ExplodingOde : IOdeSystem
    {
        public int Dimension => 1;

        public bool TryEvaluate(double t, scoped ReadOnlySpan<double> y, scoped Span<double> dydt, ref DslExecutionBudget budget)
        {
            dydt[0] = 50.0 * y[0];
            return true;
        }
    }

    // ---- Numerical Differentiation, Root Finding, Lentz CF, ODE, Curve Sampling ----

    [Fact]
    public void DiffRootAndLentzCfracCoverNormalBoundaryAndFailureCases()
    {
        DslExecutionBudget budget = DslExecutionBudget.CreateDefault();

        // Central diff: sin'(pi/3) = cos(pi/3) = 0.5
        MathResult<double> dAuto = DslNumericalAnalysisMath.DiffCentral(new SinFunc(), Math.PI / 3.0, 0.0, ref budget);
        Assert.True(dAuto.IsOk);
        Assert.Equal(0.5, dAuto.Value, 1e-8);
        Assert.Equal(MathErrorCode.DomainError, DslNumericalAnalysisMath.DiffCentral(new SinFunc(), 1.0, 1e-12, ref budget).Error);

        // Root finding on x^2 - 2 = 0 -> sqrt(2)
        double sqrt2 = Math.Sqrt(2.0);
        MathResult<double> rBrent = DslNumericalAnalysisMath.RootBrent(new PolyRootFunc(), 1.0, 2.0, 1e-12, ref budget);
        Assert.True(rBrent.IsOk);
        Assert.Equal(sqrt2, rBrent.Value, 1e-11);
        Assert.Equal(MathErrorCode.DomainError, DslNumericalAnalysisMath.RootBrent(new PolyRootFunc(), 2.0, 3.0, 1e-10, ref budget).Error);

        MathResult<double> rNewton = DslNumericalAnalysisMath.RootNewton(new PolyRootFunc(), new PolyDerivFunc(), 1.5, 1e-12, ref budget);
        Assert.True(rNewton.IsOk);
        Assert.Equal(sqrt2, rNewton.Value, 1e-12);
        Assert.Equal(MathErrorCode.DivisionByZero, DslNumericalAnalysisMath.RootNewton(new FlatZeroDerivFunc(), new FlatDerivFunc(), 0.0, 1e-10, ref budget).Error);

        MathResult<double> rSecant = DslNumericalAnalysisMath.RootSecant(new PolyRootFunc(), 1.0, 2.0, 1e-12, ref budget);
        Assert.True(rSecant.IsOk);
        Assert.Equal(sqrt2, rSecant.Value, 1e-11);

        // Modified Lentz continued fraction for phi = (1 + sqrt(5)) / 2
        MathResult<double> phi = DslNumericalAnalysisMath.CfracEval(new GoldenCfTerms(), 1e-12, ref budget);
        Assert.True(phi.IsOk);
        Assert.Equal((1.0 + Math.Sqrt(5.0)) * 0.5, phi.Value, 1e-12);
        Assert.Equal(MathErrorCode.ConvergenceFailure, DslNumericalAnalysisMath.CfracEval(new NonConvergentCfTerms(), 1e-12, ref budget).Error);
    }

    [Fact]
    public void OdeRk4Rk45AndSampleCurveEnforceAccuracyGapsAndGuards()
    {
        DslExecutionBudget budget = DslExecutionBudget.CreateDefault();
        ReadOnlySpan<double> y0 = [1.0, 0.0];
        Span<double> yOutRk4 = stackalloc double[2];
        Span<double> yOutRk45 = stackalloc double[2];

        Assert.Equal(MathErrorCode.None, DslNumericalAnalysisMath.OdeRk4(new HarmonicOscillatorOde(), 0.0, 1.0, y0, 200, yOutRk4, ref budget));
        Assert.Equal(Math.Cos(1.0), yOutRk4[0], 1e-8);
        Assert.Equal(-Math.Sin(1.0), yOutRk4[1], 1e-8);

        Assert.Equal(MathErrorCode.None, DslNumericalAnalysisMath.OdeRk45(new HarmonicOscillatorOde(), 0.0, 1.0, y0, 1e-10, yOutRk45, ref budget));
        Assert.Equal(Math.Cos(1.0), yOutRk45[0], 1e-9);
        Assert.Equal(-Math.Sin(1.0), yOutRk45[1], 1e-9);

        // Divergence guard ||y|| > 1e15 -> ArithmeticOverflow
        ReadOnlySpan<double> yExp0 = [1.0];
        Span<double> yExpOut = stackalloc double[1];
        Assert.Equal(MathErrorCode.ArithmeticOverflow, DslNumericalAnalysisMath.OdeRk4(new ExplodingOde(), 0.0, 2.0, yExp0, 100, yExpOut, ref budget));

        // Curve sampling records NaN gap at x = 0 instead of failing whole curve
        Span<double> xs = stackalloc double[5];
        Span<double> ys = stackalloc double[5];
        Assert.Equal(MathErrorCode.None, DslCurveMath.SampleCurve(new ReciprocalGapFunc(), -1.0, 1.0, xs, ys, ref budget));
        Assert.Equal(-1.0, ys[0], 1e-12);
        Assert.True(double.IsNaN(ys[2]));
        Assert.Equal(1.0, ys[4], 1e-12);

        Span<DslPoint2D> pts = stackalloc DslPoint2D[5];
        Span<bool> valid = stackalloc bool[5];
        Assert.Equal(MathErrorCode.None, DslCurveMath.SampleCurve(new ReciprocalGapFunc(), -1.0, 1.0, pts, valid, ref budget));
        Assert.True(valid[0]);
        Assert.False(valid[2]);
        Assert.True(valid[4]);

        DslExecutionBudget tinyBudget = DslExecutionBudget.Create(2);
        Assert.Equal(MathErrorCode.BudgetExceeded, DslCurveMath.SampleCurve(new SinFunc(), 0.0, 1.0, xs, ys, ref tinyBudget));
    }

    // ---- All 11 Quadrature Schemes + Select Router + SpecialViaQuad ----

    [Fact]
    public void All11QuadratureSchemesSatisfyExactMomentsAndSingularLimits()
    {
        DslExecutionBudget budget = DslExecutionBudget.CreateDefault();

        // 1. Trapezoidal & 2. Simpson
        Assert.Equal(2.0, DslQuadratureMath.QuadTrapz(new SinFunc(), 0.0, Math.PI, 256, ref budget).Value, 1e-4);
        Assert.Equal(2.0, DslQuadratureMath.QuadSimpson(new SinFunc(), 0.0, Math.PI, 128, ref budget).Value, 1e-8);
        Assert.Equal(MathErrorCode.DomainError, DslQuadratureMath.QuadSimpson(new SinFunc(), 0.0, Math.PI, 5, ref budget).Error);

        // 3. Double Exponential: singular finite int_0^1 x^(-1/2) dx = 2, semi-infinite int_0^inf e^-x dx = 1, infinite int_-inf^inf e^-x^2 dx = sqrt(pi)
        Assert.Equal(2.0, DslQuadratureMath.QuadDe(new InvSqrtSingularityFunc(), 0.0, 1.0, 1e-10, ref budget).Value, 1e-9);
        Assert.Equal(1.0, DslQuadratureMath.QuadDe(new ExpDecayFunc(), 0.0, double.PositiveInfinity, 1e-10, ref budget).Value, 1e-9);
        Assert.Equal(Math.Sqrt(Math.PI), DslQuadratureMath.QuadDe(new GaussianFunc(), double.NegativeInfinity, double.PositiveInfinity, 1e-10, ref budget).Value, 1e-9);

        // 5. Gauss-Legendre exactness for all orders {4, 8, 16, 32, 64} on deg = 2n - 2
        foreach (int n in new[] { 4, 8, 16, 32, 64 })
        {
            int deg = 2 * n - 2;
            MathResult<double> gl = DslQuadratureMath.QuadGaussLegendre(new MonomialPowFunc(deg), -1.0, 1.0, n, ref budget);
            Assert.True(gl.IsOk);
            Assert.Equal(2.0 / (deg + 1), gl.Value, 1e-13);
        }

        // 6. Gauss-Chebyshev kind 1 (int_-1^1 1/sqrt(1-x^2) dx = pi) & kind 2 (int_-1^1 sqrt(1-x^2) dx = pi/2)
        Assert.Equal(Math.PI, DslQuadratureMath.QuadGaussChebyshev(new ConstOneFunc(), 1, 16, ref budget).Value, 1e-13);
        Assert.Equal(0.5 * Math.PI, DslQuadratureMath.QuadGaussChebyshev(new ConstOneFunc(), 2, 16, ref budget).Value, 1e-13);

        // 7. Gauss-Laguerre alpha = 0 (table) and alpha = 0.5 (Golub-Welsch -> Gamma(1.5) = sqrt(pi)/2)
        Assert.Equal(1.0, DslQuadratureMath.QuadGaussLaguerre(new ConstOneFunc(), 0.0, 16, ref budget).Value, 1e-13);
        Assert.Equal(0.5 * Math.Sqrt(Math.PI), DslQuadratureMath.QuadGaussLaguerre(new ConstOneFunc(), 0.5, 16, ref budget).Value, 1e-13);

        // 8. Gauss-Hermite (int e^(-x^2) * x^2 dx = sqrt(pi)/2)
        Assert.Equal(0.5 * Math.Sqrt(Math.PI), DslQuadratureMath.QuadGaussHermite(new MonomialPowFunc(2), 8, ref budget).Value, 1e-13);

        // 9. Gauss-Jacobi including ab == -1 singular Chebyshev limit (alpha = -0.5, beta = -0.5 -> pi)
        Assert.Equal(Math.PI, DslQuadratureMath.QuadGaussJacobi(new ConstOneFunc(), -0.5, -0.5, 8, ref budget).Value, 1e-13);
        Assert.Equal(4.0 / 3.0, DslQuadratureMath.QuadGaussJacobi(new ConstOneFunc(), 1.0, 2.0, 8, ref budget).Value, 1e-13);

        // 10. Gauss-Lobatto {5, 9, 17, 33} + endpoint singularity rejection
        foreach (int n in new[] { 5, 9, 17, 33 })
        {
            Assert.Equal(2.0, DslQuadratureMath.QuadGaussLobatto(new SinFunc(), 0.0, Math.PI, n, ref budget).Value, n == 5 ? 1e-4 : 1e-9);
        }

        Assert.Equal(MathErrorCode.DomainError, DslQuadratureMath.QuadGaussLobatto(new InvSqrtSingularityFunc(), 0.0, 1.0, 9, ref budget).Error);

        // 11. Adaptive Gauss-Kronrod GK21 & 12. Romberg & 4. Router & SpecialViaQuad
        Assert.Equal(2.0, DslQuadratureMath.QuadGk21(new SinFunc(), 0.0, Math.PI, 1e-12, ref budget).Value, 1e-12);
        Assert.Equal(2.0, DslQuadratureMath.QuadRomberg(new SinFunc(), 0.0, Math.PI, 1e-12, ref budget).Value, 1e-12);
        Assert.Equal(2.0, DslQuadratureMath.QuadGaussSelect(new SinFunc(), 0.0, Math.PI, GaussWeightType.Legendre, 16, 1e-10, ref budget).Value, 1e-12);
        Assert.Equal(2.0, DslQuadratureMath.SpecialViaQuad(new SinFunc(), 0.0, Math.PI, false, ref budget).Value, 1e-12);
    }

    // ---- AST-Bound Higher-Order Calculator Functions ----

    [Fact]
    public void CalculatorHigherOrderFunctionsEvaluateNestedAndRejectInvalidDummyVars()
    {
        var names = new Dictionary<string, decimal>(StringComparer.Ordinal) { ["scale"] = 3m };

        Assert.Equal(0.5m, Math.Round(ExpressionCalculator.Evaluate("diff_central(sin(x), x, pi / 3)", Empty, CalculatorLimits.Default).Value!.Value, 6));
        Assert.Equal(1.414214m, Math.Round(ExpressionCalculator.Evaluate("root_brent(x^2 - 2, x, 1, 2, 0.0000000001)", Empty, CalculatorLimits.Default).Value!.Value, 6));
        Assert.Equal(1.414214m, Math.Round(ExpressionCalculator.Evaluate("root_newton(x^2 - 2, x, 1.5, 0.0000000001)", Empty, CalculatorLimits.Default).Value!.Value, 6));
        Assert.Equal(1.414214m, Math.Round(ExpressionCalculator.Evaluate("root_secant(x^2 - 2, x, 1, 2, 0.0000000001)", Empty, CalculatorLimits.Default).Value!.Value, 6));
        Assert.Equal(2m, Math.Round(ExpressionCalculator.Evaluate("quad_trapz(sin(x), x, 0, pi, 512)", Empty, CalculatorLimits.Default).Value!.Value, 4));
        Assert.Equal(2m, Math.Round(ExpressionCalculator.Evaluate("quad_simpson(sin(x), x, 0, pi, 64)", Empty, CalculatorLimits.Default).Value!.Value, 6));
        Assert.Equal(2m, Math.Round(ExpressionCalculator.Evaluate("quad_de(sin(x), x, 0, pi, 0.0000000001)", Empty, CalculatorLimits.Default).Value!.Value, 6));
        Assert.Equal(2m, Math.Round(ExpressionCalculator.Evaluate("quad_gauss_legendre(sin(x), x, 0, pi, 16)", Empty, CalculatorLimits.Default).Value!.Value, 6));
        Assert.Equal(6m, Math.Round(ExpressionCalculator.Evaluate("scale * quad_gk21(sin(x), x, 0, pi, 0.0000000001)", names, CalculatorLimits.Default).Value!.Value, 6));
        Assert.Equal(2m, Math.Round(ExpressionCalculator.Evaluate("quad_romberg(sin(x), x, 0, pi, 0.0000000001)", Empty, CalculatorLimits.Default).Value!.Value, 6));

        // Nested 2D quadrature using stack BoundVariableScope without Dictionary clone: int_0^2 int_0^1 (x * y) dy dx = 1
        CalculatorOutcome nested = ExpressionCalculator.Evaluate("quad_gk21(quad_gk21(x * y, y, 0, 1, 0.00000001), x, 0, 2, 0.00000001)", Empty, CalculatorLimits.Default);
        Assert.Null(nested.Error);
        Assert.Equal(1m, Math.Round(nested.Value!.Value, 8));

        // Dummy var "nan" / "inf" rejected with exact UTF-16 position
        CalculatorOutcome badNan = ExpressionCalculator.Evaluate("quad_gk21(1, nan, 0, 1)", Empty, CalculatorLimits.Default);
        Assert.Equal(CalculatorErrorCode.DomainError, badNan.Error!.Code);
        Assert.Equal(13, badNan.Error.Position);
        Assert.Equal("nan", badNan.Error.Token);

        // Budget enforcement on expensive quadrature
        var smallBudget = new CalculatorLimits { MaxExecutionOps = 40 };
        CalculatorOutcome budgetErr = ExpressionCalculator.Evaluate("quad_gk21(sin(x), x, 0, pi, 0.000000000001)", Empty, smallBudget);
        Assert.Equal(CalculatorErrorCode.BudgetExceeded, budgetErr.Error!.Code);
    }

    // ---- Zero-Allocation & 5,000-Bar Chart Budget Benchmark ----

    [Fact]
    public void QuadratureAnd5000BarBenchmarkMeetZeroAllocationAndLatencyBudget()
    {
        Span<double> xs = stackalloc double[16];
        Span<double> ys = stackalloc double[16];
        Span<double> yOut = stackalloc double[2];
        ReadOnlySpan<double> y0 = [1.0, 0.0];

        // Warmup JIT
        DslExecutionBudget budget = DslExecutionBudget.CreateDefault();
        _ = DslQuadratureMath.QuadGk21(new SinFunc(), 0.0, Math.PI, 1e-10, ref budget);
        _ = DslQuadratureMath.QuadRomberg(new SinFunc(), 0.0, Math.PI, 1e-10, ref budget);
        _ = DslQuadratureMath.QuadDe(new ExpDecayFunc(), 0.0, double.PositiveInfinity, 1e-10, ref budget);
        _ = DslQuadratureMath.QuadGaussJacobi(new ConstOneFunc(), 0.5, 1.5, 8, ref budget);
        _ = DslNumericalAnalysisMath.RootBrent(new PolyRootFunc(), 1.0, 2.0, 1e-10, ref budget);
        _ = DslNumericalAnalysisMath.OdeRk45(new HarmonicOscillatorOde(), 0.0, 1.0, y0, 1e-8, yOut, ref budget);
        _ = DslCurveMath.SampleCurve(new SinFunc(), 0.0, Math.PI, xs, ys, ref budget);

        double acc = 0.0;
        var sw = Stopwatch.StartNew();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int bar = 0; bar < 5_000; bar++)
        {
            budget = DslExecutionBudget.CreateDefault();
            MathResult<double> q1 = DslQuadratureMath.QuadGk21(new SinFunc(), 0.0, Math.PI, 1e-10, ref budget);
            MathResult<double> q2 = DslQuadratureMath.QuadGaussJacobi(new ConstOneFunc(), 0.5, 1.5, 8, ref budget);
            MathResult<double> r1 = DslNumericalAnalysisMath.RootBrent(new PolyRootFunc(), 1.0, 2.0, 1e-10, ref budget);
            MathErrorCode odeErr = DslNumericalAnalysisMath.OdeRk45(new HarmonicOscillatorOde(), 0.0, 1.0, y0, 1e-8, yOut, ref budget);
            if (!q1.IsOk || !q2.IsOk || !r1.IsOk || odeErr != MathErrorCode.None)
            {
                Assert.Fail("Unexpected error during 5,000-bar benchmark.");
            }

            acc += q1.Value + q2.Value + r1.Value + yOut[0];
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        sw.Stop();

        Assert.True(acc > 0.0);
        Assert.Equal(0L, allocated);
        Assert.True(sw.ElapsedMilliseconds < 1_000, $"5,000-bar benchmark took {sw.ElapsedMilliseconds} ms (budget < 1,000 ms).");
    }

    private readonly struct QuadraticScalarFunc : IScalarFunction
    {
        public bool TryEvaluate(double x, ref DslExecutionBudget budget, out double y)
        {
            if (!budget.TryConsume())
            {
                y = 0.0;
                return false;
            }

            y = x * x;
            return true;
        }
    }

    private readonly struct UpperSingularityDecimalFunc : IScalarFunction
    {
        // Simulates decimal AstScalarFunction evaluating 1 / sqrt(1 - x) on [0, 1],
        // where Convert.ToDecimal(x) rounds x very close to 1 to 1m -> DivisionByZero on +t only.
        public bool TryEvaluate(double x, ref DslExecutionBudget budget, out double y)
        {
            y = 0.0;
            if (!budget.TryConsume())
            {
                return false;
            }

            decimal? dec = StockAnalyzer.Core.Models.Indicators.IndicatorCalculationHelper.ToDecimalOrNull(x);
            if (!dec.HasValue)
            {
                budget.LastError = MathErrorCode.NonFiniteResult;
                return false;
            }

            decimal diff = 1m - dec.Value;
            if (diff <= 0m)
            {
                budget.LastError = MathErrorCode.DivisionByZero;
                return false;
            }

            y = 1.0 / Math.Sqrt((double)diff);
            return true;
        }
    }

    private readonly struct LowerSingularityDecimalFunc : IScalarFunction
    {
        public bool TryEvaluate(double x, ref DslExecutionBudget budget, out double y)
        {
            y = 0.0;
            if (!budget.TryConsume())
            {
                return false;
            }

            decimal? dec = StockAnalyzer.Core.Models.Indicators.IndicatorCalculationHelper.ToDecimalOrNull(x);
            if (!dec.HasValue)
            {
                budget.LastError = MathErrorCode.NonFiniteResult;
                return false;
            }

            if (dec.Value <= 0m)
            {
                budget.LastError = MathErrorCode.DivisionByZero;
                return false;
            }

            y = 1.0 / Math.Sqrt((double)dec.Value);
            return true;
        }
    }

    private readonly struct DoubleUpperSingularityFunc : IScalarFunction
    {
        public bool TryEvaluate(double x, ref DslExecutionBudget budget, out double y)
        {
            y = 0.0;
            if (!budget.TryConsume())
            {
                return false;
            }

            if (x >= 1.0)
            {
                budget.LastError = MathErrorCode.DivisionByZero;
                return false;
            }

            y = 1.0 / Math.Sqrt(1.0 - x);
            return true;
        }
    }

    private readonly struct AsymmetricOverflowDeFunc : IScalarFunction
    {
        // Simulates 1 / (1 + x^4) where decimal overflows above x = 1e7 on the +t tail
        // while the -t tail (near x = 0) remains well-defined and significant.
        public bool TryEvaluate(double x, ref DslExecutionBudget budget, out double y)
        {
            y = 0.0;
            if (!budget.TryConsume())
            {
                return false;
            }

            if (x > 1e7)
            {
                budget.LastError = MathErrorCode.ArithmeticOverflow;
                return false;
            }

            double x2 = x * x;
            double x4 = x2 * x2;
            y = 1.0 / (1.0 + x4);
            return true;
        }
    }

    [Fact]
    public void Step3Fixes_ScaleDiffCentralStepAndDecoupleQuadDeTailsWithZeroAllocation()
    {
        DslExecutionBudget budget = DslExecutionBudget.CreateDefault();

        // 1. DiffCentral at large x with scaled step h = 1.0 (previously rejected by unscaled MaxStepH = 1e-2).
        MathResult<double> deriv = DslNumericalAnalysisMath.DiffCentral(new QuadraticScalarFunc(), 100_000.0, 1.0, ref budget);
        Assert.True(deriv.IsOk);
        Assert.Equal(200_000.0, deriv.Value, 1e-6);

        // 2. QuadDe upper endpoint singularity: finite IEEE-754 double (1 - x) floors delta ~ 1e-16 (~2e-8 truncation),
        // so double converges cleanly at tol = 1e-8 and decimal 15-digit rounding converges cleanly at tol = 1e-7.
        MathResult<double> upperSingDouble = DslQuadratureMath.QuadDe(new DoubleUpperSingularityFunc(), 0.0, 1.0, 1e-8, ref budget);
        Assert.Equal(MathErrorCode.None, upperSingDouble.Error);
        Assert.Equal(2.0, upperSingDouble.Value, 1e-7);

        MathResult<double> upperSing = DslQuadratureMath.QuadDe(new UpperSingularityDecimalFunc(), 0.0, 1.0, 1e-7, ref budget);
        Assert.Equal(MathErrorCode.None, upperSing.Error);
        Assert.Equal(2.0, upperSing.Value, 1e-6);

        // 3. QuadDe small interval [0, 1e-6] lower endpoint singularity.
        MathResult<double> lowerSing = DslQuadratureMath.QuadDe(new LowerSingularityDecimalFunc(), 0.0, 1e-6, 1e-10, ref budget);
        Assert.Equal(MathErrorCode.None, lowerSing.Error);
        Assert.Equal(2e-3, lowerSing.Value, 1e-9);

        // 4. QuadDe asymmetric overflow on +t must not abort -t tail convergence to pi / (2 * sqrt(2)).
        double expectedSemiInf = Math.PI / (2.0 * Math.Sqrt(2.0));
        MathResult<double> asym = DslQuadratureMath.QuadDe(new AsymmetricOverflowDeFunc(), 0.0, 1_000_000.0, 1e-8, ref budget);
        Assert.Equal(MathErrorCode.None, asym.Error);
        Assert.Equal(expectedSemiInf, asym.Value, 1e-6);

        // Warm JIT then verify 0 heap allocation.
        long before = GC.GetAllocatedBytesForCurrentThread();
        MathResult<double> dRes = DslNumericalAnalysisMath.DiffCentral(new QuadraticScalarFunc(), 100_000.0, 1.0, ref budget);
        MathResult<double> qRes = DslQuadratureMath.QuadDe(new AsymmetricOverflowDeFunc(), 0.0, 1_000_000.0, 1e-8, ref budget);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0L, allocated);
        Assert.True(dRes.IsOk);
        Assert.True(qRes.IsOk);

        // 5. End-to-end DSL regression coverage (DiffCentral scaled step, QuadDe endpoint singularity, semi-infinite, and early-cutoff safety)
        CalculatorOutcome dslDiff = ExpressionCalculator.Evaluate("diff_central(x^2, x, 100000, 1)", Empty, CalculatorLimits.Default);
        Assert.Null(dslDiff.Error);
        Assert.Equal(200000m, dslDiff.Value!.Value, 6);

        CalculatorOutcome dslUpperSing = ExpressionCalculator.Evaluate("quad_de(1 / sqrt(1 - x), x, 0, 1, 0.0000001)", Empty, CalculatorLimits.Default);
        Assert.Null(dslUpperSing.Error);
        Assert.True(Math.Abs(dslUpperSing.Value!.Value - 2m) < 1e-6m);

        CalculatorOutcome dslLowerSing = ExpressionCalculator.Evaluate("quad_de(1 / sqrt(x), x, 0, 0.000001, 0.00000001)", Empty, CalculatorLimits.Default);
        Assert.Null(dslLowerSing.Error);
        Assert.True(Math.Abs(dslLowerSing.Value!.Value - 0.002m) < 1e-9m);

        CalculatorOutcome dslSemiInf = ExpressionCalculator.Evaluate("quad_de(1 / (1 + x^3), x, 0, 1000000, 0.000001)", Empty, CalculatorLimits.Default);
        Assert.Null(dslSemiInf.Error);
        decimal expectedSemiCube = StockAnalyzer.Core.Models.Indicators.IndicatorCalculationHelper.ToDecimalOrNull(2.0 * Math.PI / (3.0 * Math.Sqrt(3.0)))!.Value;
        Assert.True(Math.Abs(dslSemiInf.Value!.Value - expectedSemiCube) < 1e-5m);

        // Decaying oscillatory integrand confirms two-consecutive-node relative tail cutoff avoids premature root stop.
        CalculatorOutcome dslOsc = ExpressionCalculator.Evaluate("quad_de(exp(-x) * cos(2 * x), x, 0, 10, 0.00000001)", Empty, CalculatorLimits.Default);
        Assert.Null(dslOsc.Error);
        decimal expectedOsc = StockAnalyzer.Core.Models.Indicators.IndicatorCalculationHelper.ToDecimalOrNull((1.0 - Math.Exp(-10.0) * (Math.Cos(20.0) - 2.0 * Math.Sin(20.0))) / 5.0)!.Value;
        Assert.True(Math.Abs(dslOsc.Value!.Value - expectedOsc) < 1e-7m);
    }
}
