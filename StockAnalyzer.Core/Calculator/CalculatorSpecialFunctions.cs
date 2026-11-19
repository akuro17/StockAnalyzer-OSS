using System;
using StockAnalyzer.Core.MathUtils;

namespace StockAnalyzer.Core.Calculator;

/// <summary>
/// Evaluator dispatch for Phase 2 DSL special functions (23 groups) and extended variadic statistics.
/// </summary>
internal static class CalculatorSpecialFunctions
{
    public static bool TryEvaluate(
        string name,
        int position,
        scoped ReadOnlySpan<decimal> args,
        ref DslExecutionBudget budget,
        out CalculatorOutcome outcome)
    {
        int extraCost = GetExtraBudgetCost(name);
        if (extraCost > 0 && !budget.TryConsume(extraCost))
        {
            outcome = CalculatorEvaluator.Fail(CalculatorErrorCode.BudgetExceeded, position, "Execution budget exceeded.");
            return true;
        }

        switch (name)
        {
            // Extended variadic statistics (DslStatisticsMath)
            case "geomean":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslStatisticsMath.GeometricMean(args), position, name);
                return true;
            case "harmmean":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslStatisticsMath.HarmonicMean(args), position, name);
                return true;
            case "skew":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslStatisticsMath.SampleSkewness(args), position, name);
                return true;
            case "kurt":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslStatisticsMath.SampleExcessKurtosis(args), position, name);
                return true;
            case "quantile":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslStatisticsMath.Quantile(args[0], args[1..]), position, name);
                return true;
            case "iqr":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslStatisticsMath.InterquartileRange(args), position, name);
                return true;
            case "mad":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslStatisticsMath.MedianAbsoluteDeviation(args), position, name);
                return true;
            case "zscore":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslStatisticsMath.ZScore(args[0], args[1..]), position, name);
                return true;
            case "softplus_lse":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslStatisticsMath.LogSumExp(args), position, name);
                return true;

            // Gamma, Beta, Zeta
            case "gamma":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslSpecialFunctionsMath.Gamma(args[0]), position, name);
                return true;
            case "lgamma":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslSpecialFunctionsMath.LogAbsGamma(args[0], out _), position, name);
                return true;
            case "digamma":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslSpecialFunctionsMath.Digamma(args[0]), position, name);
                return true;
            case "inc_gamma_p":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslSpecialFunctionsMath.IncGammaP(args[0], args[1]), position, name);
                return true;
            case "inc_gamma_q":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslSpecialFunctionsMath.IncGammaQ(args[0], args[1]), position, name);
                return true;
            case "beta":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslSpecialFunctionsMath.Beta(args[0], args[1]), position, name);
                return true;
            case "lbeta":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslSpecialFunctionsMath.LBeta(args[0], args[1]), position, name);
                return true;
            case "inc_beta":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslSpecialFunctionsMath.IncBeta(args[0], args[1], args[2]), position, name);
                return true;
            case "zeta":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslSpecialFunctionsMath.Zeta(args[0]), position, name);
                return true;
            case "hurwitz_zeta":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslSpecialFunctionsMath.HurwitzZeta(args[0], args[1]), position, name);
                return true;

            // Bessel & Spherical Bessel
            case "bessel_j":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslSpecialFunctionsMath.BesselJ(args[0], args[1]), position, name);
                return true;
            case "bessel_y":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslSpecialFunctionsMath.BesselY(args[0], args[1]), position, name);
                return true;
            case "bessel_i":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslSpecialFunctionsMath.BesselI(args[0], args[1]), position, name);
                return true;
            case "bessel_k":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslSpecialFunctionsMath.BesselK(args[0], args[1]), position, name);
                return true;
            case "bessel_ie":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslSpecialFunctionsMath.BesselIe(args[0], args[1]), position, name);
                return true;
            case "bessel_ke":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslSpecialFunctionsMath.BesselKe(args[0], args[1]), position, name);
                return true;
            case "sph_bessel_j":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslSpecialFunctionsMath.SphBesselJ(args[0], args[1]), position, name);
                return true;
            case "sph_bessel_y":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslSpecialFunctionsMath.SphBesselY(args[0], args[1]), position, name);
                return true;
            case "sph_bessel_i":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslSpecialFunctionsMath.SphBesselI(args[0], args[1]), position, name);
                return true;
            case "sph_bessel_k":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslSpecialFunctionsMath.SphBesselK(args[0], args[1]), position, name);
                return true;

            // Airy, Kelvin, Struve
            case "airy_ai":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslSpecialFunctionsMath.AiryAi(args[0]), position, name);
                return true;
            case "airy_bi":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslSpecialFunctionsMath.AiryBi(args[0]), position, name);
                return true;
            case "airy_aip":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslSpecialFunctionsMath.AiryAiPrime(args[0]), position, name);
                return true;
            case "airy_bip":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslSpecialFunctionsMath.AiryBiPrime(args[0]), position, name);
                return true;
            case "kelvin_ber":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslSpecialFunctionsMath.KelvinBer(args[0]), position, name);
                return true;
            case "kelvin_bei":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslSpecialFunctionsMath.KelvinBei(args[0]), position, name);
                return true;
            case "kelvin_ker":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslSpecialFunctionsMath.KelvinKer(args[0]), position, name);
                return true;
            case "kelvin_kei":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslSpecialFunctionsMath.KelvinKei(args[0]), position, name);
                return true;
            case "struve_h":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslSpecialFunctionsMath.StruveH(args[0], args[1]), position, name);
                return true;
            case "struve_l":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslSpecialFunctionsMath.StruveL(args[0], args[1]), position, name);
                return true;

            // Error functions
            case "erf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslSpecialFunctionsMath.Erf(args[0]), position, name);
                return true;
            case "erfc":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslSpecialFunctionsMath.Erfc(args[0]), position, name);
                return true;
            case "erfcx":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslSpecialFunctionsMath.Erfcx(args[0]), position, name);
                return true;
            case "erfi":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslSpecialFunctionsMath.Erfi(args[0]), position, name);
                return true;
            case "erfinv":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslSpecialFunctionsMath.ErfInv(args[0]), position, name);
                return true;
            case "erfcinv":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslSpecialFunctionsMath.ErfcInv(args[0]), position, name);
                return true;

            // Legendre, Bernoulli, Stirling, Pochhammer, Brillouin, Langevin
            case "legendre_p":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslSpecialFunctionsMath.LegendreP(args[0], args[1]), position, name);
                return true;
            case "legendre_q":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslSpecialFunctionsMath.LegendreQ(args[0], args[1]), position, name);
                return true;
            case "assoc_legendre":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslSpecialFunctionsMath.AssocLegendre(args[0], args[1], args[2]), position, name);
                return true;
            case "bernoulli":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslSpecialFunctionsMath.Bernoulli(args[0]), position, name);
                return true;
            case "stirling1":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslSpecialFunctionsMath.Stirling1(args[0], args[1]), position, name);
                return true;
            case "stirling2":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslSpecialFunctionsMath.Stirling2(args[0], args[1]), position, name);
                return true;
            case "pochhammer":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslSpecialFunctionsMath.Pochhammer(args[0], args[1]), position, name);
                return true;
            case "brillouin":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslSpecialFunctionsMath.Brillouin(args[0], args[1]), position, name);
                return true;
            case "langevin":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslSpecialFunctionsMath.Langevin(args[0]), position, name);
                return true;

            // Integrals & Fresnel
            case "expint_ei":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslSpecialFunctionsMath.ExpIntEi(args[0]), position, name);
                return true;
            case "expint_e1":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslSpecialFunctionsMath.ExpIntE1(args[0]), position, name);
                return true;
            case "sinint_si":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslSpecialFunctionsMath.SinIntSi(args[0]), position, name);
                return true;
            case "cosint_ci":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslSpecialFunctionsMath.CosIntCi(args[0]), position, name);
                return true;
            case "logint_li":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslSpecialFunctionsMath.LogIntLi(args[0]), position, name);
                return true;
            case "fresnel_s":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslSpecialFunctionsMath.FresnelS(args[0]), position, name);
                return true;
            case "fresnel_c":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslSpecialFunctionsMath.FresnelC(args[0]), position, name);
                return true;

            // Elliptic integrals & Jacobi elliptic functions
            case "ellip_k":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslSpecialFunctionsMath.EllipK(args[0]), position, name);
                return true;
            case "ellip_e":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslSpecialFunctionsMath.EllipE(args[0]), position, name);
                return true;
            case "ellip_f":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslSpecialFunctionsMath.EllipF(args[0], args[1]), position, name);
                return true;
            case "ellip_e_inc":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslSpecialFunctionsMath.EllipEInc(args[0], args[1]), position, name);
                return true;
            case "ellip_pi":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslSpecialFunctionsMath.EllipPi(args[0], args[1], args[2]), position, name);
                return true;
            case "jacobi_sn":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslSpecialFunctionsMath.JacobiSn(args[0], args[1]), position, name);
                return true;
            case "jacobi_cn":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslSpecialFunctionsMath.JacobiCn(args[0], args[1]), position, name);
                return true;
            case "jacobi_dn":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslSpecialFunctionsMath.JacobiDn(args[0], args[1]), position, name);
                return true;

            // Orthogonal polynomials & Hypergeometric
            case "chebyshev_t":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslSpecialFunctionsMath.ChebyshevT(args[0], args[1]), position, name);
                return true;
            case "chebyshev_u":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslSpecialFunctionsMath.ChebyshevU(args[0], args[1]), position, name);
                return true;
            case "hermite_h":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslSpecialFunctionsMath.HermiteH(args[0], args[1]), position, name);
                return true;
            case "laguerre_l":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslSpecialFunctionsMath.LaguerreL(args[0], args[1], args[2]), position, name);
                return true;
            case "jacobi_p":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslSpecialFunctionsMath.JacobiP(args[0], args[1], args[2], args[3]), position, name);
                return true;
            case "hyp0f1":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslSpecialFunctionsMath.Hyp0F1(args[0], args[1]), position, name);
                return true;
            case "hyp1f1":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslSpecialFunctionsMath.Hyp1F1(args[0], args[1], args[2]), position, name);
                return true;
            case "hyp2f1":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslSpecialFunctionsMath.Hyp2F1(args[0], args[1], args[2], args[3]), position, name);
                return true;

            default:
                outcome = default;
                return false;
        }
    }

    private static int GetExtraBudgetCost(string name) => name switch
    {
        "inc_gamma_p" or "inc_gamma_q" or "inc_beta" or "zeta" or "hurwitz_zeta"
            or "bessel_j" or "bessel_y" or "bessel_i" or "bessel_k" or "bessel_ie" or "bessel_ke"
            or "sph_bessel_j" or "sph_bessel_y" or "sph_bessel_i" or "sph_bessel_k"
            or "struve_h" or "struve_l"
            or "ellip_k" or "ellip_e" or "ellip_f" or "ellip_e_inc" or "ellip_pi"
            or "hyp0f1" or "hyp1f1" or "hyp2f1" => 25,
        "gamma" or "lgamma" or "digamma" or "beta" or "lbeta"
            or "airy_ai" or "airy_bi" or "airy_aip" or "airy_bip"
            or "kelvin_ber" or "kelvin_bei" or "kelvin_ker" or "kelvin_kei"
            or "legendre_p" or "legendre_q" or "assoc_legendre" or "pochhammer"
            or "jacobi_sn" or "jacobi_cn" or "jacobi_dn"
            or "chebyshev_t" or "chebyshev_u" or "hermite_h" or "laguerre_l" or "jacobi_p" => 10,
        _ => 0,
    };
}
