using System;
using StockAnalyzer.Core.MathUtils;

namespace StockAnalyzer.Core.Calculator;

/// <summary>
/// Evaluator dispatch for Phase 2 DSL probability distributions (26 distributions, 129 functions).
/// </summary>
internal static class CalculatorDistributionFunctions
{
    public static bool TryEvaluate(
        string name,
        int position,
        scoped ReadOnlySpan<decimal> args,
        ref DslExecutionBudget budget,
        out CalculatorOutcome outcome)
    {
        switch (name)
        {
            // 1. Normal (1 or 3 args)
            case "norm_pdf":
            case "norm_logpdf":
            case "norm_cdf":
            case "norm_sf":
            case "norm_icdf":
                if (args.Length is not (1 or 3))
                {
                    outcome = CalculatorEvaluator.Fail(
                        CalculatorErrorCode.WrongArgumentCount,
                        position,
                        $"Function '{name}' expects 1 or 3 arguments, got {args.Length}.",
                        name);
                    return true;
                }

                decimal mu = args.Length == 3 ? args[1] : 0m;
                decimal sigma = args.Length == 3 ? args[2] : 1m;
                MathResult<decimal> normRes = name switch
                {
                    "norm_pdf" => DslProbabilityDistributionsMath.NormPdf(args[0], mu, sigma),
                    "norm_logpdf" => DslProbabilityDistributionsMath.NormLogPdf(args[0], mu, sigma),
                    "norm_cdf" => DslProbabilityDistributionsMath.NormCdf(args[0], mu, sigma),
                    "norm_sf" => DslProbabilityDistributionsMath.NormSf(args[0], mu, sigma),
                    _ => DslProbabilityDistributionsMath.NormIcdf(args[0], mu, sigma),
                };
                outcome = CalculatorElementaryFunctions.FromMathResult(normRes, position, name);
                return true;

            // 2. LogNormal
            case "lognorm_pdf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.LogNormPdf(args[0], args[1], args[2]), position, name);
                return true;
            case "lognorm_logpdf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.LogNormLogPdf(args[0], args[1], args[2]), position, name);
                return true;
            case "lognorm_cdf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.LogNormCdf(args[0], args[1], args[2]), position, name);
                return true;
            case "lognorm_sf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.LogNormSf(args[0], args[1], args[2]), position, name);
                return true;
            case "lognorm_icdf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.LogNormIcdf(args[0], args[1], args[2]), position, name);
                return true;

            // 3. Bivariate Normal (3 or 7 args)
            case "bvnorm_pdf":
            case "bvnorm_logpdf":
            case "bvnorm_cdf":
            case "bvnorm_sf":
                if (args.Length is not (3 or 7))
                {
                    outcome = CalculatorEvaluator.Fail(
                        CalculatorErrorCode.WrongArgumentCount,
                        position,
                        $"Function '{name}' expects 3 or 7 arguments, got {args.Length}.",
                        name);
                    return true;
                }

                decimal bmu1 = args.Length == 7 ? args[2] : 0m;
                decimal bmu2 = args.Length == 7 ? args[3] : 0m;
                decimal bs1 = args.Length == 7 ? args[4] : 1m;
                decimal bs2 = args.Length == 7 ? args[5] : 1m;
                decimal brho = args.Length == 7 ? args[6] : args[2];
                MathResult<decimal> bvRes = name switch
                {
                    "bvnorm_pdf" => DslProbabilityDistributionsMath.BvNormPdf(args[0], args[1], bmu1, bmu2, bs1, bs2, brho),
                    "bvnorm_logpdf" => DslProbabilityDistributionsMath.BvNormLogPdf(args[0], args[1], bmu1, bmu2, bs1, bs2, brho),
                    "bvnorm_cdf" => DslProbabilityDistributionsMath.BvNormCdf(args[0], args[1], bmu1, bmu2, bs1, bs2, brho),
                    _ => DslProbabilityDistributionsMath.BvNormSf(args[0], args[1], bmu1, bmu2, bs1, bs2, brho),
                };
                outcome = CalculatorElementaryFunctions.FromMathResult(bvRes, position, name);
                return true;

            case "copula_gauss_cdf":
                outcome = CalculatorElementaryFunctions.FromMathResult(
                    DslProbabilityDistributionsMath.CopulaGaussCdf(args[0], args[1], args[2]),
                    position,
                    name);
                return true;

            // 4. Chi-square
            case "chi2_pdf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.Chi2Pdf(args[0], args[1]), position, name);
                return true;
            case "chi2_logpdf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.Chi2LogPdf(args[0], args[1]), position, name);
                return true;
            case "chi2_cdf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.Chi2Cdf(args[0], args[1]), position, name);
                return true;
            case "chi2_sf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.Chi2Sf(args[0], args[1]), position, name);
                return true;
            case "chi2_icdf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.Chi2Icdf(args[0], args[1], ref budget), position, name);
                return true;

            // 5. Student's t
            case "t_pdf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.StudentTPdf(args[0], args[1]), position, name);
                return true;
            case "t_logpdf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.StudentTLogPdf(args[0], args[1]), position, name);
                return true;
            case "t_cdf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.StudentTCdf(args[0], args[1]), position, name);
                return true;
            case "t_sf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.StudentTSf(args[0], args[1]), position, name);
                return true;
            case "t_icdf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.StudentTIcdf(args[0], args[1], ref budget), position, name);
                return true;

            // 6. F
            case "f_pdf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.FPdf(args[0], args[1], args[2]), position, name);
                return true;
            case "f_logpdf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.FLogPdf(args[0], args[1], args[2]), position, name);
                return true;
            case "f_cdf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.FCdf(args[0], args[1], args[2]), position, name);
                return true;
            case "f_sf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.FSf(args[0], args[1], args[2]), position, name);
                return true;
            case "f_icdf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.FIcdf(args[0], args[1], args[2], ref budget), position, name);
                return true;

            // 7. Non-central Chi-square
            case "ncchi2_pdf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.NcChi2Pdf(args[0], args[1], args[2], ref budget), position, name);
                return true;
            case "ncchi2_logpdf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.NcChi2LogPdf(args[0], args[1], args[2], ref budget), position, name);
                return true;
            case "ncchi2_cdf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.NcChi2Cdf(args[0], args[1], args[2], ref budget), position, name);
                return true;
            case "ncchi2_sf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.NcChi2Sf(args[0], args[1], args[2], ref budget), position, name);
                return true;
            case "ncchi2_icdf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.NcChi2Icdf(args[0], args[1], args[2], ref budget), position, name);
                return true;

            // 8. Non-central t
            case "nct_pdf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.NctPdf(args[0], args[1], args[2], ref budget), position, name);
                return true;
            case "nct_logpdf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.NctLogPdf(args[0], args[1], args[2], ref budget), position, name);
                return true;
            case "nct_cdf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.NctCdf(args[0], args[1], args[2], ref budget), position, name);
                return true;
            case "nct_sf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.NctSf(args[0], args[1], args[2], ref budget), position, name);
                return true;
            case "nct_icdf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.NctIcdf(args[0], args[1], args[2], ref budget), position, name);
                return true;

            // 9. Non-central F
            case "ncf_pdf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.NcfPdf(args[0], args[1], args[2], args[3], ref budget), position, name);
                return true;
            case "ncf_logpdf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.NcfLogPdf(args[0], args[1], args[2], args[3], ref budget), position, name);
                return true;
            case "ncf_cdf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.NcfCdf(args[0], args[1], args[2], args[3], ref budget), position, name);
                return true;
            case "ncf_sf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.NcfSf(args[0], args[1], args[2], args[3], ref budget), position, name);
                return true;
            case "ncf_icdf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.NcfIcdf(args[0], args[1], args[2], args[3], ref budget), position, name);
                return true;

            // 10. Exponential
            case "expd_pdf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.ExpPdf(args[0], args[1]), position, name);
                return true;
            case "expd_logpdf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.ExpLogPdf(args[0], args[1]), position, name);
                return true;
            case "expd_cdf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.ExpCdf(args[0], args[1]), position, name);
                return true;
            case "expd_sf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.ExpSf(args[0], args[1]), position, name);
                return true;
            case "expd_icdf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.ExpIcdf(args[0], args[1]), position, name);
                return true;

            // 11. Uniform
            case "unif_pdf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.UnifPdf(args[0], args[1], args[2]), position, name);
                return true;
            case "unif_logpdf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.UnifLogPdf(args[0], args[1], args[2]), position, name);
                return true;
            case "unif_cdf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.UnifCdf(args[0], args[1], args[2]), position, name);
                return true;
            case "unif_sf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.UnifSf(args[0], args[1], args[2]), position, name);
                return true;
            case "unif_icdf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.UnifIcdf(args[0], args[1], args[2]), position, name);
                return true;

            // 12. Weibull
            case "weibull_pdf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.WeibullPdf(args[0], args[1], args[2]), position, name);
                return true;
            case "weibull_logpdf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.WeibullLogPdf(args[0], args[1], args[2]), position, name);
                return true;
            case "weibull_cdf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.WeibullCdf(args[0], args[1], args[2]), position, name);
                return true;
            case "weibull_sf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.WeibullSf(args[0], args[1], args[2]), position, name);
                return true;
            case "weibull_icdf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.WeibullIcdf(args[0], args[1], args[2]), position, name);
                return true;

            // 13. Gamma
            case "gamma_pdf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.GammaPdf(args[0], args[1], args[2]), position, name);
                return true;
            case "gamma_logpdf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.GammaLogPdf(args[0], args[1], args[2]), position, name);
                return true;
            case "gamma_cdf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.GammaCdf(args[0], args[1], args[2]), position, name);
                return true;
            case "gamma_sf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.GammaSf(args[0], args[1], args[2]), position, name);
                return true;
            case "gamma_icdf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.GammaIcdf(args[0], args[1], args[2], ref budget), position, name);
                return true;

            // 14. Beta
            case "beta_pdf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.BetaPdf(args[0], args[1], args[2]), position, name);
                return true;
            case "beta_logpdf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.BetaLogPdf(args[0], args[1], args[2]), position, name);
                return true;
            case "beta_cdf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.BetaCdf(args[0], args[1], args[2]), position, name);
                return true;
            case "beta_sf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.BetaSf(args[0], args[1], args[2]), position, name);
                return true;
            case "beta_icdf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.BetaIcdf(args[0], args[1], args[2], ref budget), position, name);
                return true;

            // 15. Laplace
            case "laplace_pdf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.LaplacePdf(args[0], args[1], args[2]), position, name);
                return true;
            case "laplace_logpdf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.LaplaceLogPdf(args[0], args[1], args[2]), position, name);
                return true;
            case "laplace_cdf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.LaplaceCdf(args[0], args[1], args[2]), position, name);
                return true;
            case "laplace_sf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.LaplaceSf(args[0], args[1], args[2]), position, name);
                return true;
            case "laplace_icdf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.LaplaceIcdf(args[0], args[1], args[2]), position, name);
                return true;

            // 16. Cauchy
            case "cauchy_pdf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.CauchyPdf(args[0], args[1], args[2]), position, name);
                return true;
            case "cauchy_logpdf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.CauchyLogPdf(args[0], args[1], args[2]), position, name);
                return true;
            case "cauchy_cdf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.CauchyCdf(args[0], args[1], args[2]), position, name);
                return true;
            case "cauchy_sf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.CauchySf(args[0], args[1], args[2]), position, name);
                return true;
            case "cauchy_icdf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.CauchyIcdf(args[0], args[1], args[2]), position, name);
                return true;

            // 17. Logistic
            case "logistic_pdf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.LogisticPdf(args[0], args[1], args[2]), position, name);
                return true;
            case "logistic_logpdf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.LogisticLogPdf(args[0], args[1], args[2]), position, name);
                return true;
            case "logistic_cdf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.LogisticCdf(args[0], args[1], args[2]), position, name);
                return true;
            case "logistic_sf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.LogisticSf(args[0], args[1], args[2]), position, name);
                return true;
            case "logistic_icdf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.LogisticIcdf(args[0], args[1], args[2]), position, name);
                return true;

            // 18. Von Mises
            case "vonmises_pdf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.VonMisesPdf(args[0], args[1], args[2]), position, name);
                return true;
            case "vonmises_logpdf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.VonMisesLogPdf(args[0], args[1], args[2]), position, name);
                return true;
            case "vonmises_cdf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.VonMisesCdf(args[0], args[1], args[2]), position, name);
                return true;
            case "vonmises_sf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.VonMisesSf(args[0], args[1], args[2]), position, name);
                return true;
            case "vonmises_icdf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.VonMisesIcdf(args[0], args[1], args[2], ref budget), position, name);
                return true;

            // 19. Pareto
            case "pareto_pdf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.ParetoPdf(args[0], args[1], args[2]), position, name);
                return true;
            case "pareto_logpdf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.ParetoLogPdf(args[0], args[1], args[2]), position, name);
                return true;
            case "pareto_cdf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.ParetoCdf(args[0], args[1], args[2]), position, name);
                return true;
            case "pareto_sf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.ParetoSf(args[0], args[1], args[2]), position, name);
                return true;
            case "pareto_icdf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.ParetoIcdf(args[0], args[1], args[2]), position, name);
                return true;

            // 20. Levy
            case "levy_pdf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.LevyPdf(args[0], args[1], args[2]), position, name);
                return true;
            case "levy_logpdf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.LevyLogPdf(args[0], args[1], args[2]), position, name);
                return true;
            case "levy_cdf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.LevyCdf(args[0], args[1], args[2]), position, name);
                return true;
            case "levy_sf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.LevySf(args[0], args[1], args[2]), position, name);
                return true;
            case "levy_icdf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.LevyIcdf(args[0], args[1], args[2]), position, name);
                return true;

            // 21. Poisson
            case "poisson_pmf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.PoissonPmf(args[0], args[1]), position, name);
                return true;
            case "poisson_logpmf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.PoissonLogPmf(args[0], args[1]), position, name);
                return true;
            case "poisson_cdf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.PoissonCdf(args[0], args[1]), position, name);
                return true;
            case "poisson_sf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.PoissonSf(args[0], args[1]), position, name);
                return true;
            case "poisson_icdf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.PoissonIcdf(args[0], args[1], ref budget), position, name);
                return true;

            // 22. Binomial
            case "binom_pmf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.BinomPmf(args[0], args[1], args[2]), position, name);
                return true;
            case "binom_logpmf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.BinomLogPmf(args[0], args[1], args[2]), position, name);
                return true;
            case "binom_cdf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.BinomCdf(args[0], args[1], args[2]), position, name);
                return true;
            case "binom_sf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.BinomSf(args[0], args[1], args[2]), position, name);
                return true;
            case "binom_icdf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.BinomIcdf(args[0], args[1], args[2], ref budget), position, name);
                return true;

            // 23. Negative Binomial
            case "nbinom_pmf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.NBinomPmf(args[0], args[1], args[2]), position, name);
                return true;
            case "nbinom_logpmf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.NBinomLogPmf(args[0], args[1], args[2]), position, name);
                return true;
            case "nbinom_cdf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.NBinomCdf(args[0], args[1], args[2]), position, name);
                return true;
            case "nbinom_sf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.NBinomSf(args[0], args[1], args[2]), position, name);
                return true;
            case "nbinom_icdf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.NBinomIcdf(args[0], args[1], args[2], ref budget), position, name);
                return true;

            // 24. Geometric
            case "geom_pmf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.GeomPmf(args[0], args[1]), position, name);
                return true;
            case "geom_logpmf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.GeomLogPmf(args[0], args[1]), position, name);
                return true;
            case "geom_cdf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.GeomCdf(args[0], args[1]), position, name);
                return true;
            case "geom_sf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.GeomSf(args[0], args[1]), position, name);
                return true;
            case "geom_icdf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.GeomIcdf(args[0], args[1]), position, name);
                return true;

            // 25. Hypergeometric
            case "hypergeom_pmf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.HypergeomPmf(args[0], args[1], args[2], args[3]), position, name);
                return true;
            case "hypergeom_logpmf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.HypergeomLogPmf(args[0], args[1], args[2], args[3]), position, name);
                return true;
            case "hypergeom_cdf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.HypergeomCdf(args[0], args[1], args[2], args[3], ref budget), position, name);
                return true;
            case "hypergeom_sf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.HypergeomSf(args[0], args[1], args[2], args[3], ref budget), position, name);
                return true;
            case "hypergeom_icdf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.HypergeomIcdf(args[0], args[1], args[2], args[3], ref budget), position, name);
                return true;

            // 26. Tukey Studentized Range
            case "tukey_q_pdf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.TukeyQPdf(args[0], args[1], args[2], ref budget), position, name);
                return true;
            case "tukey_q_logpdf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.TukeyQLogPdf(args[0], args[1], args[2], ref budget), position, name);
                return true;
            case "tukey_q_cdf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.TukeyQCdf(args[0], args[1], args[2], ref budget), position, name);
                return true;
            case "tukey_q_sf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.TukeyQSf(args[0], args[1], args[2], ref budget), position, name);
                return true;
            case "tukey_q_icdf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.TukeyQIcdf(args[0], args[1], args[2], ref budget), position, name);
                return true;

            // 27. Generalized Extreme Value (GEV)
            case "gev_pdf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.GevPdf(args[0], args[1], args[2], args[3]), position, name);
                return true;
            case "gev_cdf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.GevCdf(args[0], args[1], args[2], args[3]), position, name);
                return true;
            case "gev_icdf":
                outcome = CalculatorElementaryFunctions.FromMathResult(DslProbabilityDistributionsMath.GevIcdf(args[0], args[1], args[2], args[3]), position, name);
                return true;

            default:
                outcome = default;
                return false;
        }
    }
}
