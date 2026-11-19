#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Calculator;
using StockAnalyzer.Core.MathUtils;
using Xunit;

namespace StockAnalyzer.Core.Tests.Calculator;

public sealed class CalculatorDslSpecialAndDistTests
{
    private static readonly IReadOnlyDictionary<string, decimal> Empty = new Dictionary<string, decimal>(StringComparer.Ordinal);

    private static decimal EvalOk(string expr, IReadOnlyDictionary<string, decimal>? vars = null, CalculatorLimits? limits = null)
    {
        CalculatorOutcome outcome = ExpressionCalculator.Evaluate(expr, vars ?? Empty, limits ?? CalculatorLimits.Default);
        Assert.True(outcome.Error is null, $"Expected Ok for '{expr}' but got {outcome.Error}");
        return outcome.Value!.Value;
    }

    private static void EvalErr(string expr, CalculatorErrorCode expectedCode, int expectedPos = 0, string? expectedToken = null, CalculatorLimits? limits = null)
    {
        CalculatorOutcome outcome = ExpressionCalculator.Evaluate(expr, Empty, limits ?? CalculatorLimits.Default);
        Assert.True(outcome.Value is null, $"Expected Error({expectedCode}) for '{expr}' but got {outcome.Value}");
        Assert.NotNull(outcome.Error);
        Assert.Equal(expectedCode, outcome.Error!.Code);
        Assert.Equal(expectedPos, outcome.Error.Position);
        if (expectedToken is not null)
        {
            Assert.Equal(expectedToken, outcome.Error.Token);
        }
    }

    [Fact]
    public void SpecialFunctionsGammaBetaZetaErrorAndCombinatorialMatchReferenceValues()
    {
        // Gamma & LogAbsGamma negative non-integer poles vs reflection
        Assert.Equal(24m, EvalOk("gamma(5)"));
        Assert.True(Math.Abs(EvalOk("gamma(0.5)") - 1.772453850905516m) < 1e-12m);
        Assert.True(Math.Abs(EvalOk("gamma(-0.5)") - (-3.544907701811032m)) < 1e-12m);
        Assert.True(Math.Abs(EvalOk("gamma(-1.5)") - 2.363271801207355m) < 1e-12m);
        EvalErr("gamma(0)", CalculatorErrorCode.DomainError, 0, "gamma");
        EvalErr("gamma(-3)", CalculatorErrorCode.DomainError, 0, "gamma");
        EvalErr("lgamma(-2)", CalculatorErrorCode.DomainError, 0, "lgamma");
        EvalErr("gamma(29)", CalculatorErrorCode.ArithmeticOverflow);

        // Digamma, IncGamma P + Q == 1 without cancellation
        Assert.True(Math.Abs(EvalOk("digamma(1)") + MathConstants.EulerGammaDecimal) < 1e-12m);
        EvalErr("digamma(0)", CalculatorErrorCode.DomainError, 0, "digamma");
        decimal igp = EvalOk("inc_gamma_p(5, 0.01)");
        decimal igq = EvalOk("inc_gamma_q(5, 0.01)");
        Assert.True(igp > 0m && igp < 1e-10m);
        Assert.True(Math.Abs((igp + igq) - 1m) < 1e-15m);

        // Beta & IncBeta
        Assert.True(Math.Abs(EvalOk("beta(2, 3)") - (1m / 12m)) < 1e-14m);
        Assert.True(Math.Abs(EvalOk("inc_beta(0.5, 2, 2)") - 0.5m) < 1e-14m);
        EvalErr("beta(0, 1)", CalculatorErrorCode.DomainError, 0, "beta");

        // Zeta & Hurwitz Zeta
        Assert.Equal(-0.5m, EvalOk("zeta(0)"));
        Assert.Equal(0m, EvalOk("zeta(-2)"));
        Assert.True(Math.Abs(EvalOk("zeta(2)") - 1.6449340668482264m) < 1e-12m);
        Assert.True(Math.Abs(EvalOk("hurwitz_zeta(2, 1)") - 1.6449340668482264m) < 1e-12m);
        EvalErr("zeta(1)", CalculatorErrorCode.DomainError, 0, "zeta");

        // Error functions
        Assert.Equal(0m, EvalOk("erf(0)"));
        Assert.Equal(1m, EvalOk("erfc(0)"));
        Assert.Equal(1m, EvalOk("erfcx(0)"));
        Assert.Equal(0m, EvalOk("erfi(0)"));
        Assert.Equal(0m, EvalOk("erfinv(0)"));
        Assert.Equal(0m, EvalOk("erfcinv(1)"));
        Assert.True(Math.Abs(EvalOk("erfinv(erf(0.75))") - 0.75m) < 1e-13m);
        Assert.True(Math.Abs(EvalOk("erfcinv(erfc(0.75))") - 0.75m) < 1e-13m);
        Assert.True(EvalOk("erfcx(50)") > 0m);
        EvalErr("erfinv(1)", CalculatorErrorCode.DomainError, 0, "erfinv");
        EvalErr("erfcinv(0)", CalculatorErrorCode.DomainError, 0, "erfcinv");

        // Bernoulli, Stirling, Pochhammer, Brillouin, Langevin
        Assert.Equal(1m, EvalOk("bernoulli(0)"));
        Assert.Equal(-0.5m, EvalOk("bernoulli(1)"));
        Assert.Equal(0m, EvalOk("bernoulli(3)"));
        Assert.True(Math.Abs(EvalOk("bernoulli(2)") - (1m / 6m)) < 1e-27m);
        EvalErr("bernoulli(46)", CalculatorErrorCode.ArithmeticOverflow);
        Assert.Equal(-6m, EvalOk("stirling1(4, 1)"));
        Assert.Equal(7m, EvalOk("stirling2(4, 2)"));
        Assert.Equal(-60m, EvalOk("pochhammer(-5, 3)"));
        Assert.Equal(0m, EvalOk("langevin(0)"));
        Assert.True(Math.Abs(EvalOk("brillouin(0.5, 1)") - EvalOk("tanh(1)")) < 1e-13m);
    }

    [Fact]
    public void SpecialFunctionsBesselAiryKelvinStruveEllipticOrthogonalHyperMatchReferenceValues()
    {
        Assert.Equal(1m, EvalOk("bessel_j(0, 0)"));
        Assert.True(Math.Abs(EvalOk("bessel_j(0, 1)") - 0.7651976865579666m) < 1e-13m);
        Assert.True(Math.Abs(EvalOk("bessel_y(0, 1)") - 0.08825696421567696m) < 1e-13m);
        Assert.True(Math.Abs(EvalOk("bessel_i(0, 1)") - 1.2660658777520084m) < 1e-13m);
        Assert.True(Math.Abs(EvalOk("bessel_k(0, 1)") - 0.42102443824070834m) < 1e-13m);
        Assert.True(EvalOk("bessel_ie(0, 1000)") > 0m);
        Assert.True(EvalOk("bessel_ke(0, 1000)") > 0m);
        EvalErr("bessel_y(0, 0)", CalculatorErrorCode.DomainError, 0, "bessel_y");
        EvalErr("bessel_k(0, 0)", CalculatorErrorCode.DomainError, 0, "bessel_k");

        Assert.Equal(1m, EvalOk("sph_bessel_j(0, 0)"));
        Assert.True(Math.Abs(EvalOk("sph_bessel_j(1, 1)") - (EvalOk("sin(1)") - EvalOk("cos(1)"))) < 1e-13m);

        Assert.True(Math.Abs(EvalOk("airy_ai(0)") - 0.3550280538878172m) < 1e-13m);
        Assert.True(Math.Abs(EvalOk("airy_bi(0)") - 0.6149266274460007m) < 1e-13m);
        Assert.True(Math.Abs(EvalOk("kelvin_ber(0)") - 1m) < 1e-14m);
        Assert.Equal(0m, EvalOk("kelvin_bei(0)"));
        Assert.True(EvalOk("kelvin_ker(1)") > 0.28m);
        Assert.True(EvalOk("struve_h(0, 1)") > 0.56m);
        Assert.True(EvalOk("struve_l(0, 1)") > 0.71m);

        // Integrals & Fresnel
        Assert.True(Math.Abs(EvalOk("expint_e1(1)") - 0.21938393439552028m) < 1e-13m);
        Assert.True(Math.Abs(EvalOk("expint_ei(1)") - 1.8951178163559368m) < 1e-13m);
        Assert.Equal(0m, EvalOk("sinint_si(0)"));
        Assert.True(Math.Abs(EvalOk("cosint_ci(1)") - 0.3374039229009681m) < 1e-13m);
        Assert.True(EvalOk("logint_li(2)") > 1.04m);
        EvalErr("logint_li(1)", CalculatorErrorCode.DomainError, 0, "logint_li");
        Assert.Equal(0m, EvalOk("fresnel_s(0)"));
        Assert.Equal(0m, EvalOk("fresnel_c(0)"));

        // Elliptic & Jacobi
        Assert.True(Math.Abs(EvalOk("ellip_k(0)") - MathConstants.PiDecimal / 2m) < 1e-14m);
        Assert.Equal(1m, EvalOk("ellip_e(1)"));
        EvalErr("ellip_k(1)", CalculatorErrorCode.DomainError, 0, "ellip_k");
        Assert.True(Math.Abs(EvalOk("jacobi_sn(0.5, 0)") - EvalOk("sin(0.5)")) < 1e-14m);
        Assert.True(Math.Abs(EvalOk("jacobi_cn(0.5, 0)") - EvalOk("cos(0.5)")) < 1e-14m);
        Assert.Equal(1m, EvalOk("jacobi_dn(0.5, 0)"));

        // Orthogonal polynomials & Hypergeometric
        Assert.Equal(1m, EvalOk("chebyshev_t(0, 0.5)"));
        Assert.Equal(-0.5m, EvalOk("chebyshev_t(2, 0.5)"));
        Assert.Equal(7m, EvalOk("hermite_h(2, 1.5)"));
        Assert.True(Math.Abs(EvalOk("laguerre_l(2, 0, 1)") - (-0.5m)) < 1e-14m);
        Assert.True(Math.Abs(EvalOk("jacobi_p(2, 0, 0, 0.5)") - (-0.125m)) < 1e-14m);
        Assert.True(Math.Abs(EvalOk("hyp1f1(1, 1, 2)") - EvalOk("exp(2)")) < 1e-13m);
        Assert.True(Math.Abs(EvalOk("hyp2f1(1, 1, 2, -1)") - EvalOk("ln(2)")) < 1e-12m);
        Assert.True(Math.Abs(EvalOk("hyp2f1(0.5, 0.5, 1.5, 1)") - MathConstants.PiDecimal / 2m) < 1e-12m);
        EvalErr("hyp2f1(1, 1, 1, 1)", CalculatorErrorCode.DomainError, 0, "hyp2f1");
    }

    [Fact]
    public void StatisticsVariadicsAndAll26DistributionsSatisfyIdentitiesAndDomainGuards()
    {
        // Extended variadic statistics up to 32 args
        Assert.Equal(4m, EvalOk("geomean(2, 8)"));
        Assert.Equal(2.4m, EvalOk("harmmean(2, 3)"));
        Assert.Equal(0m, EvalOk("skew(1, 2, 3)"));
        Assert.Equal(-1.2m, EvalOk("kurt(1, 2, 3, 4)"));
        Assert.Equal(2.5m, EvalOk("quantile(0.5, 1, 2, 3, 4)"));
        Assert.Equal(2m, EvalOk("iqr(1, 2, 3, 4, 5)"));
        Assert.Equal(1m, EvalOk("mad(1, 2, 3, 4, 5)"));
        Assert.Equal(1m, EvalOk("zscore(4, 2, 2, 4, 4)"));
        Assert.True(EvalOk("softplus_lse(1000, 1000)") > 1000.69m);
        EvalErr("skew(5, 5, 5)", CalculatorErrorCode.DivisionByZero);
        EvalErr("zscore(5, 5, 5)", CalculatorErrorCode.DivisionByZero);

        // Normal & LogNormal & Bivariate Normal (including 3-arg norm and 7-arg bvnorm)
        Assert.True(Math.Abs(EvalOk("norm_cdf(0)") - 0.5m) < 1e-15m);
        Assert.True(Math.Abs(EvalOk("norm_cdf(10, 10, 2)") - 0.5m) < 1e-15m);
        Assert.True(Math.Abs(EvalOk("norm_cdf(1.96, 0, 1) + norm_sf(1.96, 0, 1)") - 1m) < 1e-15m);
        Assert.True(Math.Abs(EvalOk("norm_icdf(norm_cdf(1.5))") - 1.5m) < 1e-13m);
        EvalErr("norm_pdf(0, 1)", CalculatorErrorCode.WrongArgumentCount, 0, "norm_pdf");
        Assert.Equal(0m, EvalOk("lognorm_cdf(-1, 0, 1)"));
        Assert.Equal(1m, EvalOk("lognorm_sf(0, 0, 1)"));
        EvalErr("lognorm_logpdf(0, 0, 1)", CalculatorErrorCode.DomainError, 0, "lognorm_logpdf");
        Assert.True(Math.Abs(EvalOk("bvnorm_cdf(0, 0, 0)") - 0.25m) < 1e-14m);
        Assert.True(Math.Abs(EvalOk("bvnorm_cdf(10, 20, 10, 20, 2, 3, 0)") - 0.25m) < 1e-14m);
        Assert.True(Math.Abs(EvalOk("bvnorm_cdf(0, 0, 0.5)") - (1m / 3m)) < 1e-12m);
        Assert.True(Math.Abs(EvalOk("bvnorm_sf(0, 0, 0.5)") - (1m / 3m)) < 1e-12m);
        EvalErr("bvnorm_pdf(0, 0, 0, 0)", CalculatorErrorCode.WrongArgumentCount, 0, "bvnorm_pdf");

        // Tight execution budget aborts iterative D2 distributions with BudgetExceeded
        var tightBudget = new CalculatorLimits { MaxExecutionOps = 5 };
        EvalErr("ncchi2_cdf(500, 4, 200)", CalculatorErrorCode.BudgetExceeded, 0, limits: tightBudget);
        EvalErr("tukey_q_cdf(3.5, 4, 12)", CalculatorErrorCode.BudgetExceeded, 0, limits: tightBudget);

        // 32-arg statistical variadics
        string values31 = string.Join(", ", Enumerable.Range(1, 31));
        Assert.Equal(16m, EvalOk($"quantile(0.5, {values31})"));
        EvalErr("geomean(-1, 2)", CalculatorErrorCode.DomainError, 0, "geomean");
        EvalErr("harmmean(0, 2)", CalculatorErrorCode.DomainError, 0, "harmmean");

        // Chi2, t, F, Non-central Chi2, Non-central t, Non-central F
        Assert.True(Math.Abs(EvalOk("chi2_icdf(chi2_cdf(5.5, 4), 4)") - 5.5m) < 1e-11m);
        Assert.True(Math.Abs(EvalOk("t_cdf(0, 10)") - 0.5m) < 1e-14m);
        Assert.True(Math.Abs(EvalOk("t_icdf(t_cdf(2.1, 7), 7)") - 2.1m) < 1e-11m);
        Assert.True(Math.Abs(EvalOk("f_icdf(f_cdf(2.5, 5, 10), 5, 10)") - 2.5m) < 1e-10m);
        Assert.True(Math.Abs(EvalOk("ncchi2_cdf(5, 4, 0)") - EvalOk("chi2_cdf(5, 4)")) < 1e-13m);
        Assert.True(Math.Abs(EvalOk("ncchi2_icdf(ncchi2_cdf(7, 4, 2.5), 4, 2.5)") - 7m) < 1e-9m);
        Assert.True(Math.Abs(EvalOk("nct_cdf(1.5, 6, 0)") - EvalOk("t_cdf(1.5, 6)")) < 1e-13m);
        Assert.True(Math.Abs(EvalOk("nct_cdf(2.0, 10, 1.5) + nct_sf(2.0, 10, 1.5)") - 1m) < 1e-13m);
        Assert.True(Math.Abs(EvalOk("nct_icdf(nct_cdf(1.8, 10, 1.5), 10, 1.5)") - 1.8m) < 1e-9m);
        Assert.True(Math.Abs(EvalOk("ncf_cdf(2, 4, 8, 0)") - EvalOk("f_cdf(2, 4, 8)")) < 1e-13m);

        // Exp, Unif, Weibull, Gamma, Beta, Laplace, Cauchy, Logistic, VonMises, Pareto, Levy
        Assert.True(Math.Abs(EvalOk("expd_icdf(expd_cdf(2.5, 1.5), 1.5)") - 2.5m) < 1e-13m);
        Assert.Equal(0.5m, EvalOk("unif_icdf(0.5, 0, 1)"));
        Assert.Equal(0m, EvalOk("unif_icdf(0, 0, 10)"));
        Assert.Equal(10m, EvalOk("unif_icdf(1, 0, 10)"));
        Assert.True(Math.Abs(EvalOk("weibull_icdf(weibull_cdf(1.7, 2, 3), 2, 3)") - 1.7m) < 1e-12m);
        Assert.True(Math.Abs(EvalOk("gamma_icdf(gamma_cdf(2.2, 3, 1.5), 3, 1.5)") - 2.2m) < 1e-10m);
        Assert.True(Math.Abs(EvalOk("beta_icdf(beta_cdf(0.35, 2, 5), 2, 5)") - 0.35m) < 1e-11m);
        Assert.Equal(5m, EvalOk("laplace_icdf(0.5, 5, 2)"));
        EvalErr("cauchy_icdf(0.00000000000000001, 0, 1)", CalculatorErrorCode.ArithmeticOverflow);
        Assert.Equal(0.5m, EvalOk("logistic_cdf(3, 3, 2)"));
        Assert.True(Math.Abs(EvalOk("vonmises_cdf(0, 0, 2) + vonmises_sf(0, 0, 2)") - 1m) < 1e-13m);
        Assert.True(Math.Abs(EvalOk("pareto_icdf(pareto_cdf(3, 1, 2), 1, 2)") - 3m) < 1e-12m);
        Assert.True(Math.Abs(EvalOk("levy_icdf(levy_cdf(4, 0, 1), 0, 1)") - 4m) < 1e-11m);

        // Discrete distributions: Poisson, Binom, NBinom, Geom, Hypergeom + Tukey q
        Assert.True(Math.Abs(EvalOk("poisson_cdf(3, 2.5) + poisson_sf(3, 2.5)") - 1m) < 1e-14m);
        Assert.Equal(3m, EvalOk("poisson_icdf(poisson_cdf(3, 2.5), 2.5)"));
        Assert.True(Math.Abs(EvalOk("binom_cdf(4, 10, 0.3) + binom_sf(4, 10, 0.3)") - 1m) < 1e-14m);
        Assert.Equal(4m, EvalOk("binom_icdf(binom_cdf(4, 10, 0.3), 10, 0.3)"));
        Assert.True(Math.Abs(EvalOk("nbinom_cdf(3, 5, 0.4) + nbinom_sf(3, 5, 0.4)") - 1m) < 1e-14m);
        Assert.Equal(3m, EvalOk("nbinom_icdf(nbinom_cdf(3, 5, 0.4), 5, 0.4)"));
        Assert.Equal(0.3m, EvalOk("geom_pmf(1, 0.3)"));
        Assert.Equal(3m, EvalOk("geom_icdf(geom_cdf(3, 0.3), 0.3)"));
        Assert.True(Math.Abs(EvalOk("hypergeom_cdf(2, 20, 7, 5) + hypergeom_sf(2, 20, 7, 5)") - 1m) < 1e-14m);
        Assert.Equal(2m, EvalOk("hypergeom_icdf(hypergeom_cdf(2, 20, 7, 5), 20, 7, 5)"));
        Assert.True(Math.Abs(EvalOk("tukey_q_cdf(3.5, 4, 12) + tukey_q_sf(3.5, 4, 12)") - 1m) < 1e-14m);
        Assert.True(Math.Abs(EvalOk("tukey_q_icdf(tukey_q_cdf(3.5, 4, 12), 4, 12)") - 3.5m) < 1e-8m);
    }

    [Fact]
    public void ZeroAllocationSpecialAndDistributionKernelsAllocateZeroBytes()
    {
        Span<decimal> stats = stackalloc decimal[] { 2m, 4m, 6m, 8m, 10m };
        var budget = DslExecutionBudget.CreateDefault();

        // Warmup JIT
        _ = DslStatisticsMath.GeometricMean(stats);
        _ = DslStatisticsMath.MedianAbsoluteDeviation(stats);
        _ = DslSpecialFunctionsMath.BesselJ(1m, 2m);
        _ = DslSpecialFunctionsMath.EllipK(0.5m);
        _ = DslProbabilityDistributionsMath.BvNormCdf(0.5m, -0.3m, 0m, 0m, 1m, 1m, 0.4m);
        _ = DslProbabilityDistributionsMath.NcChi2Cdf(5m, 4m, 2m, ref budget);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 50; i++)
        {
            budget = DslExecutionBudget.CreateDefault();
            _ = DslStatisticsMath.GeometricMean(stats);
            _ = DslStatisticsMath.MedianAbsoluteDeviation(stats);
            _ = DslSpecialFunctionsMath.BesselJ(1m, 2m);
            _ = DslSpecialFunctionsMath.EllipK(0.5m);
            _ = DslProbabilityDistributionsMath.BvNormCdf(0.5m, -0.3m, 0m, 0m, 1m, 1m, 0.4m);
            _ = DslProbabilityDistributionsMath.NcChi2Cdf(5m, 4m, 2m, ref budget);
        }

        long after = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(0L, after - before);
    }

    [Fact]
    public void Step2Fixes_MaintainNegativeZetaDiscreteLowerTailNonCentralAndBudgetSafety()
    {
        // (1) LogAbsGamma & Digamma negative pole reflection distance in decimal before double cast
        decimal nearPole = -1m - 1e-20m;
        decimal lgNear = EvalOk("lgamma(x)", new Dictionary<string, decimal> { ["x"] = nearPole });
        Assert.True(Math.Abs(lgNear - 46.051701859880914m) < 1e-12m);

        decimal dgNear = EvalOk("digamma(x)", new Dictionary<string, decimal> { ["x"] = nearPole });
        Assert.True(Math.Abs(dgNear - 1e20m) / 1e20m < 1e-12m);

        // (2) Hurwitz Zeta negative domain s < 0 via finite shift + DLMF 25.11.18 Fourier functional equation
        // Verified against exact Bernoulli polynomials -B_{m+1}(q)/(m+1):
        // -B_2(0.25)/2 = -(-1/48)/2 = +1/96.
        Assert.True(Math.Abs(EvalOk("hurwitz_zeta(-1, 0.25)") - (1m / 96m)) < 1e-14m);
        decimal hzNeg15 = EvalOk("hurwitz_zeta(-15, 0.3)");
        Assert.True(Math.Abs(hzNeg15 - (-0.136978182513009628m)) / 0.136978182513009628m < 1e-13m);
        Assert.True(Math.Abs(EvalOk("hurwitz_zeta(-20, 0.5)")) < 1e-13m);
        decimal hzNeg25 = EvalOk("hurwitz_zeta(-25, 0.7)");
        Assert.True(Math.Abs(hzNeg25 - 16942.655418981827m) / 16942.655418981827m < 1e-14m);
        Assert.True(Math.Abs(EvalOk("hurwitz_zeta(-2, 1.5)") - (-0.25m)) < 1e-14m);

        // (3) Hyp2F1 terminating polynomial at z = 1 (even with c - a - b <= 0) & Pfaff convergence
        Assert.Equal(1m, EvalOk("hyp2f1(-2, 3, 1, 1)"));
        Assert.Equal(0m, EvalOk("hyp2f1(2, -0.5, 2, 1)"));
        Assert.True(Math.Abs(EvalOk("hyp2f1(1.5, 2.0, 2.5, -1.0)") - 0.428097245096172m) < 1e-13m);

        // (4) Discrete _icdf lower-tail roundtrips where F(k) <= 1e-14
        Assert.Equal(0m, EvalOk("poisson_icdf(poisson_cdf(0, 42), 42)"));
        Assert.Equal(1m, EvalOk("poisson_icdf(poisson_cdf(1, 42), 42)"));
        Assert.Equal(3m, EvalOk("poisson_icdf(poisson_cdf(3, 42), 42)"));
        Assert.Equal(36m, EvalOk("poisson_icdf(poisson_cdf(36, 100), 100)"));
        Assert.Equal(0m, EvalOk("binom_icdf(binom_cdf(0, 100, 0.5), 100, 0.5)"));
        Assert.Equal(14m, EvalOk("binom_icdf(binom_cdf(14, 100, 0.5), 100, 0.5)"));
        Assert.Equal(0m, EvalOk("hypergeom_icdf(hypergeom_cdf(0, 200, 100, 50), 200, 100, 50)"));
        Assert.Equal(2m, EvalOk("hypergeom_icdf(hypergeom_cdf(2, 200, 100, 50), 200, 100, 50)"));
        Assert.Equal(5m, EvalOk("nbinom_icdf(nbinom_cdf(5, 50, 0.5), 50, 0.5)"));
        Assert.Equal(2m, EvalOk("geom_icdf(geom_cdf(2, 0.000000000000001), 0.000000000000001)"));

        // (5) Non-central distributions at lambda = 10,000 (|delta| = 100) and tail saddlepoint
        // At x = 6400 (~18 sigma below mean 10010), log-pdf (~-184) is well-defined while pdf underflows decimal (< 1e-28m).
        decimal ncPdf6400 = EvalOk("ncchi2_pdf(6400, 10, 10000)");
        decimal ncLogPdf6400 = EvalOk("ncchi2_logpdf(6400, 10, 10000)");
        Assert.Equal(0m, ncPdf6400);
        Assert.True(ncLogPdf6400 < -150m);

        decimal ncPdf8600 = EvalOk("ncchi2_pdf(8600, 10, 10000)");
        decimal ncLogPdf8600 = EvalOk("ncchi2_logpdf(8600, 10, 10000)");
        decimal expected8600FromLog = StockAnalyzer.Core.Models.Indicators.IndicatorCalculationHelper.ToDecimalOrNull(Math.Exp((double)ncLogPdf8600))!.Value;
        Assert.True(ncPdf8600 > 0m);
        Assert.True(Math.Abs(ncPdf8600 - expected8600FromLog) / expected8600FromLog < 1e-10m);

        decimal ncPdfTail = EvalOk("ncchi2_pdf(8800, 10, 10000)");
        decimal ncLogPdfTail = EvalOk("ncchi2_logpdf(8800, 10, 10000)");
        decimal expectedPdfFromLog = StockAnalyzer.Core.Models.Indicators.IndicatorCalculationHelper.ToDecimalOrNull(Math.Exp((double)ncLogPdfTail))!.Value;
        Assert.True(ncPdfTail > 0m);
        Assert.True(Math.Abs(ncPdfTail - expectedPdfFromLog) / expectedPdfFromLog < 1e-10m);
        Assert.True(Math.Abs((EvalOk("ncchi2_cdf(10010, 10, 10000)") + EvalOk("ncchi2_sf(10010, 10, 10000)")) - 1m) < 1e-12m);
        Assert.True(Math.Abs((EvalOk("nct_cdf(100, 20, 100)") + EvalOk("nct_sf(100, 20, 100)")) - 1m) < 1e-12m);
        Assert.True(Math.Abs((EvalOk("ncf_cdf(1000, 10, 10, 10000)") + EvalOk("ncf_sf(1000, 10, 10, 10000)")) - 1m) < 1e-11m);

        // (6) Cauchy tails & Tukey q inside adaptive quadrature hits BudgetExceeded deterministically
        decimal cLeft = EvalOk("cauchy_cdf(cauchy_icdf(0.000000000000001, 0, 1), 0, 1)");
        Assert.True(Math.Abs(cLeft - 1e-15m) / 1e-15m < 1e-12m);
        decimal cRight = EvalOk("cauchy_sf(cauchy_icdf(0.999999999999999, 0, 1), 0, 1)");
        Assert.True(Math.Abs(cRight - 1e-15m) / 1e-15m < 1e-12m);
        EvalErr("quad_gk21(tukey_q_cdf(x, 20, 10), x, 0.1, 20, 0.0001)", CalculatorErrorCode.BudgetExceeded);

        // (7) Zero-allocation check on updated Step 2 kernels
        var budget = DslExecutionBudget.CreateDefault();
        _ = DslSpecialFunctionsMath.LogAbsGamma(nearPole, out _);
        _ = DslSpecialFunctionsMath.HurwitzZeta(-15m, 0.3m);
        _ = DslProbabilityDistributionsMath.PoissonIcdf(1e-15m, 100m, ref budget);
        _ = DslProbabilityDistributionsMath.NcChi2Pdf(6400m, 10m, 10000m, ref budget);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 50; i++)
        {
            budget = DslExecutionBudget.CreateDefault();
            _ = DslSpecialFunctionsMath.LogAbsGamma(nearPole, out _);
            _ = DslSpecialFunctionsMath.HurwitzZeta(-15m, 0.3m);
            _ = DslProbabilityDistributionsMath.PoissonIcdf(1e-15m, 100m, ref budget);
            _ = DslProbabilityDistributionsMath.NcChi2Pdf(6400m, 10m, 10000m, ref budget);
        }

        long after = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(0L, after - before);
    }
}
