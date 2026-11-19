using System;
using System.Collections.Generic;

namespace StockAnalyzer.Core.Calculator;

/// <summary>
/// Pure, thread-safe, deterministic <see cref="decimal"/> arithmetic expression evaluator.
/// Not a rendering or per-bar hot path; standard managed allocation during lexing and AST construction is intentional.
/// </summary>
public static class ExpressionCalculator
{
    /// <summary>Maximum decimal digits accepted by <c>round(x, digits)</c>.</summary>
    public const int MaxRoundDigits = 10;

    /// <summary>Maximum declared identifier length enforced by the expression API.</summary>
    public const int MaxIdentifierLength = 64;

    private static readonly IReadOnlyDictionary<string, decimal> EmptyNames = new Dictionary<string, decimal>(0, StringComparer.Ordinal);

    /// <summary>
    /// Built-in functions of calculator version 3 in canonical order (v1/v2 indices 0..17 preserved).
    /// </summary>
    public static IReadOnlyList<CalculatorFunctionInfo> Functions { get; } = new CalculatorFunctionInfo[]
    {
        new("abs", 1, 1, "abs(x)"),
        new("sign", 1, 1, "sign(x)"),
        new("min", 2, CalculatorLimits.Default.MaxFunctionArguments, "min(a, b, ...)", IsVariadic: true),
        new("max", 2, CalculatorLimits.Default.MaxFunctionArguments, "max(a, b, ...)", IsVariadic: true),
        new("clamp", 3, 3, "clamp(x, lo, hi)"),
        new("floor", 1, 1, "floor(x)"),
        new("ceil", 1, 1, "ceil(x)"),
        new("trunc", 1, 1, "trunc(x)"),
        new("round", 2, 2, "round(x, digits)"),
        new("change_ratio", 2, 2, "change_ratio(from, to)"),
        new("mod", 2, 2, "mod(a, b)"),
        new("sum", 2, CalculatorLimits.Default.MaxFunctionArguments, "sum(a, b, ...)", IsVariadic: true),
        new("avg", 2, CalculatorLimits.Default.MaxFunctionArguments, "avg(a, b, ...)", IsVariadic: true),
        new("frac", 1, 1, "frac(x)"),
        new("sqrt", 1, 1, "sqrt(x)"),
        new("ln", 1, 1, "ln(x)"),
        new("exp", 1, 1, "exp(x)"),
        new("pow", 2, 2, "pow(x, y)"),
        // Sprint D1: Phase 1 DSL Elementary, Trigonometric, Combinatorial, Geometric, Complex, Stats, Activations
        new("cbrt", 1, 1, "cbrt(x)"),
        new("log10", 1, 1, "log10(x)"),
        new("log2", 1, 1, "log2(x)"),
        new("log", 2, 2, "log(b, x)"),
        new("expm1", 1, 1, "expm1(x)"),
        new("log1p", 1, 1, "log1p(x)"),
        new("sin", 1, 1, "sin(x)"),
        new("cos", 1, 1, "cos(x)"),
        new("tan", 1, 1, "tan(x)"),
        new("sind", 1, 1, "sind(deg)"),
        new("cosd", 1, 1, "cosd(deg)"),
        new("tand", 1, 1, "tand(deg)"),
        new("asin", 1, 1, "asin(x)"),
        new("acos", 1, 1, "acos(x)"),
        new("atan", 1, 1, "atan(x)"),
        new("atan2", 2, 2, "atan2(y, x)"),
        new("sinh", 1, 1, "sinh(x)"),
        new("cosh", 1, 1, "cosh(x)"),
        new("tanh", 1, 1, "tanh(x)"),
        new("asinh", 1, 1, "asinh(x)"),
        new("acosh", 1, 1, "acosh(x)"),
        new("atanh", 1, 1, "atanh(x)"),
        new("sum_arith", 3, 3, "sum_arith(a, d, n)"),
        new("sum_geom", 3, 3, "sum_geom(a, r, n)"),
        new("fact", 1, 1, "fact(n)"),
        new("double_fact", 1, 1, "double_fact(n)"),
        new("perm", 2, 2, "perm(n, k)"),
        new("comb", 2, 2, "comb(n, k)"),
        new("lcomb", 2, 2, "lcomb(n, k)"),
        new("fib", 1, 1, "fib(n)"),
        new("lucas", 1, 1, "lucas(n)"),
        new("solve_linear", 2, 2, "solve_linear(a, b)"),
        new("quad_discriminant", 3, 3, "quad_discriminant(a, b, c)"),
        new("quad_root_min", 3, 3, "quad_root_min(a, b, c)"),
        new("quad_root_max", 3, 3, "quad_root_max(a, b, c)"),
        new("hypot", 2, 2, "hypot(x, y)"),
        new("dist2d", 4, 4, "dist2d(x1, y1, x2, y2)"),
        new("dist3d", 6, 6, "dist3d(x1, y1, z1, x2, y2, z2)"),
        new("tri_area_sss", 3, 3, "tri_area_sss(a, b, c)"),
        new("tri_inradius", 3, 3, "tri_inradius(a, b, c)"),
        new("tri_circumradius", 3, 3, "tri_circumradius(a, b, c)"),
        new("sphere_vol", 1, 1, "sphere_vol(r)"),
        new("cone_area", 2, 2, "cone_area(r, h)"),
        new("torus_vol", 2, 2, "torus_vol(R, r)"),
        new("c_abs", 2, 2, "c_abs(re, im)"),
        new("c_arg", 2, 2, "c_arg(re, im)"),
        new("c_exp_re", 2, 2, "c_exp_re(re, im)"),
        new("c_exp_im", 2, 2, "c_exp_im(re, im)"),
        new("bayes", 3, 3, "bayes(pA, pB_A, pB)"),
        new("var_p", 2, CalculatorLimits.Default.MaxStatisticalFunctionArguments, "var_p(a, b, ...)", IsVariadic: true, IsStatisticalVariadic: true),
        new("var_s", 2, CalculatorLimits.Default.MaxStatisticalFunctionArguments, "var_s(a, b, ...)", IsVariadic: true, IsStatisticalVariadic: true),
        new("stdev_p", 2, CalculatorLimits.Default.MaxStatisticalFunctionArguments, "stdev_p(a, b, ...)", IsVariadic: true, IsStatisticalVariadic: true),
        new("stdev_s", 2, CalculatorLimits.Default.MaxStatisticalFunctionArguments, "stdev_s(a, b, ...)", IsVariadic: true, IsStatisticalVariadic: true),
        new("median", 2, CalculatorLimits.Default.MaxStatisticalFunctionArguments, "median(a, b, ...)", IsVariadic: true, IsStatisticalVariadic: true),
        new("sigmoid", 1, 1, "sigmoid(x)"),
        new("relu", 1, 1, "relu(x)"),
        new("leaky_relu", 2, 2, "leaky_relu(x, alpha)"),
        new("elu", 2, 2, "elu(x, alpha)"),
        new("gelu", 1, 1, "gelu(x)"),
        new("silu", 1, 1, "silu(x)"),
        new("softplus", 1, 1, "softplus(x)"),
        new("swish", 2, 2, "swish(x, beta)"),
        // Sprint D2: Extended variadic statistics (DslStatisticsMath)
        new("geomean", 2, CalculatorLimits.Default.MaxStatisticalFunctionArguments, "geomean(a, b, ...)", IsVariadic: true, IsStatisticalVariadic: true),
        new("harmmean", 2, CalculatorLimits.Default.MaxStatisticalFunctionArguments, "harmmean(a, b, ...)", IsVariadic: true, IsStatisticalVariadic: true),
        new("skew", 3, CalculatorLimits.Default.MaxStatisticalFunctionArguments, "skew(a, b, c, ...)", IsVariadic: true, IsStatisticalVariadic: true),
        new("kurt", 4, CalculatorLimits.Default.MaxStatisticalFunctionArguments, "kurt(a, b, c, d, ...)", IsVariadic: true, IsStatisticalVariadic: true),
        new("quantile", 3, CalculatorLimits.Default.MaxStatisticalFunctionArguments, "quantile(p, a, b, ...)", IsVariadic: true, IsStatisticalVariadic: true),
        new("iqr", 2, CalculatorLimits.Default.MaxStatisticalFunctionArguments, "iqr(a, b, ...)", IsVariadic: true, IsStatisticalVariadic: true),
        new("mad", 2, CalculatorLimits.Default.MaxStatisticalFunctionArguments, "mad(a, b, ...)", IsVariadic: true, IsStatisticalVariadic: true),
        new("zscore", 3, CalculatorLimits.Default.MaxStatisticalFunctionArguments, "zscore(x, a, b, ...)", IsVariadic: true, IsStatisticalVariadic: true),
        new("softplus_lse", 2, CalculatorLimits.Default.MaxStatisticalFunctionArguments, "softplus_lse(a, b, ...)", IsVariadic: true, IsStatisticalVariadic: true),
        // Sprint D2: Special functions (23 groups)
        new("gamma", 1, 1, "gamma(x)"),
        new("lgamma", 1, 1, "lgamma(x)"),
        new("digamma", 1, 1, "digamma(x)"),
        new("inc_gamma_p", 2, 2, "inc_gamma_p(a, x)"),
        new("inc_gamma_q", 2, 2, "inc_gamma_q(a, x)"),
        new("beta", 2, 2, "beta(a, b)"),
        new("lbeta", 2, 2, "lbeta(a, b)"),
        new("inc_beta", 3, 3, "inc_beta(x, a, b)"),
        new("zeta", 1, 1, "zeta(s)"),
        new("hurwitz_zeta", 2, 2, "hurwitz_zeta(s, q)"),
        new("bessel_j", 2, 2, "bessel_j(nu, x)"),
        new("bessel_y", 2, 2, "bessel_y(nu, x)"),
        new("bessel_i", 2, 2, "bessel_i(nu, x)"),
        new("bessel_k", 2, 2, "bessel_k(nu, x)"),
        new("bessel_ie", 2, 2, "bessel_ie(nu, x)"),
        new("bessel_ke", 2, 2, "bessel_ke(nu, x)"),
        new("sph_bessel_j", 2, 2, "sph_bessel_j(n, x)"),
        new("sph_bessel_y", 2, 2, "sph_bessel_y(n, x)"),
        new("sph_bessel_i", 2, 2, "sph_bessel_i(n, x)"),
        new("sph_bessel_k", 2, 2, "sph_bessel_k(n, x)"),
        new("airy_ai", 1, 1, "airy_ai(x)"),
        new("airy_bi", 1, 1, "airy_bi(x)"),
        new("airy_aip", 1, 1, "airy_aip(x)"),
        new("airy_bip", 1, 1, "airy_bip(x)"),
        new("kelvin_ber", 1, 1, "kelvin_ber(x)"),
        new("kelvin_bei", 1, 1, "kelvin_bei(x)"),
        new("kelvin_ker", 1, 1, "kelvin_ker(x)"),
        new("kelvin_kei", 1, 1, "kelvin_kei(x)"),
        new("struve_h", 2, 2, "struve_h(nu, x)"),
        new("struve_l", 2, 2, "struve_l(nu, x)"),
        new("erf", 1, 1, "erf(x)"),
        new("erfc", 1, 1, "erfc(x)"),
        new("erfcx", 1, 1, "erfcx(x)"),
        new("erfi", 1, 1, "erfi(x)"),
        new("erfinv", 1, 1, "erfinv(y)"),
        new("erfcinv", 1, 1, "erfcinv(y)"),
        new("legendre_p", 2, 2, "legendre_p(l, x)"),
        new("legendre_q", 2, 2, "legendre_q(l, x)"),
        new("assoc_legendre", 3, 3, "assoc_legendre(l, m, x)"),
        new("bernoulli", 1, 1, "bernoulli(n)"),
        new("stirling1", 2, 2, "stirling1(n, k)"),
        new("stirling2", 2, 2, "stirling2(n, k)"),
        new("pochhammer", 2, 2, "pochhammer(x, n)"),
        new("brillouin", 2, 2, "brillouin(J, x)"),
        new("langevin", 1, 1, "langevin(x)"),
        new("expint_ei", 1, 1, "expint_ei(x)"),
        new("expint_e1", 1, 1, "expint_e1(x)"),
        new("sinint_si", 1, 1, "sinint_si(x)"),
        new("cosint_ci", 1, 1, "cosint_ci(x)"),
        new("logint_li", 1, 1, "logint_li(x)"),
        new("fresnel_s", 1, 1, "fresnel_s(x)"),
        new("fresnel_c", 1, 1, "fresnel_c(x)"),
        new("ellip_k", 1, 1, "ellip_k(k)"),
        new("ellip_e", 1, 1, "ellip_e(k)"),
        new("ellip_f", 2, 2, "ellip_f(phi, k)"),
        new("ellip_e_inc", 2, 2, "ellip_e_inc(phi, k)"),
        new("ellip_pi", 3, 3, "ellip_pi(n, phi, k)"),
        new("jacobi_sn", 2, 2, "jacobi_sn(u, m)"),
        new("jacobi_cn", 2, 2, "jacobi_cn(u, m)"),
        new("jacobi_dn", 2, 2, "jacobi_dn(u, m)"),
        new("chebyshev_t", 2, 2, "chebyshev_t(n, x)"),
        new("chebyshev_u", 2, 2, "chebyshev_u(n, x)"),
        new("hermite_h", 2, 2, "hermite_h(n, x)"),
        new("laguerre_l", 3, 3, "laguerre_l(n, alpha, x)"),
        new("jacobi_p", 4, 4, "jacobi_p(n, alpha, beta, x)"),
        new("hyp0f1", 2, 2, "hyp0f1(b, z)"),
        new("hyp1f1", 3, 3, "hyp1f1(a, b, z)"),
        new("hyp2f1", 4, 4, "hyp2f1(a, b, c, z)"),
        // Sprint D2: Probability distributions (26 distributions, 129 functions)
        new("norm_pdf", 1, 3, "norm_pdf(x[, mu, sigma])"),
        new("norm_logpdf", 1, 3, "norm_logpdf(x[, mu, sigma])"),
        new("norm_cdf", 1, 3, "norm_cdf(x[, mu, sigma])"),
        new("norm_sf", 1, 3, "norm_sf(x[, mu, sigma])"),
        new("norm_icdf", 1, 3, "norm_icdf(p[, mu, sigma])"),
        new("lognorm_pdf", 3, 3, "lognorm_pdf(x, mu, sigma)"),
        new("lognorm_logpdf", 3, 3, "lognorm_logpdf(x, mu, sigma)"),
        new("lognorm_cdf", 3, 3, "lognorm_cdf(x, mu, sigma)"),
        new("lognorm_sf", 3, 3, "lognorm_sf(x, mu, sigma)"),
        new("lognorm_icdf", 3, 3, "lognorm_icdf(p, mu, sigma)"),
        new("bvnorm_pdf", 3, 7, "bvnorm_pdf(x, y[, mu1, mu2, sigma1, sigma2], rho)"),
        new("bvnorm_logpdf", 3, 7, "bvnorm_logpdf(x, y[, mu1, mu2, sigma1, sigma2], rho)"),
        new("bvnorm_cdf", 3, 7, "bvnorm_cdf(x, y[, mu1, mu2, sigma1, sigma2], rho)"),
        new("bvnorm_sf", 3, 7, "bvnorm_sf(x, y[, mu1, mu2, sigma1, sigma2], rho)"),
        new("chi2_pdf", 2, 2, "chi2_pdf(x, nu)"),
        new("chi2_logpdf", 2, 2, "chi2_logpdf(x, nu)"),
        new("chi2_cdf", 2, 2, "chi2_cdf(x, nu)"),
        new("chi2_sf", 2, 2, "chi2_sf(x, nu)"),
        new("chi2_icdf", 2, 2, "chi2_icdf(p, nu)"),
        new("t_pdf", 2, 2, "t_pdf(x, nu)"),
        new("t_logpdf", 2, 2, "t_logpdf(x, nu)"),
        new("t_cdf", 2, 2, "t_cdf(x, nu)"),
        new("t_sf", 2, 2, "t_sf(x, nu)"),
        new("t_icdf", 2, 2, "t_icdf(p, nu)"),
        new("f_pdf", 3, 3, "f_pdf(x, d1, d2)"),
        new("f_logpdf", 3, 3, "f_logpdf(x, d1, d2)"),
        new("f_cdf", 3, 3, "f_cdf(x, d1, d2)"),
        new("f_sf", 3, 3, "f_sf(x, d1, d2)"),
        new("f_icdf", 3, 3, "f_icdf(p, d1, d2)"),
        new("ncchi2_pdf", 3, 3, "ncchi2_pdf(x, nu, lambda)"),
        new("ncchi2_logpdf", 3, 3, "ncchi2_logpdf(x, nu, lambda)"),
        new("ncchi2_cdf", 3, 3, "ncchi2_cdf(x, nu, lambda)"),
        new("ncchi2_sf", 3, 3, "ncchi2_sf(x, nu, lambda)"),
        new("ncchi2_icdf", 3, 3, "ncchi2_icdf(p, nu, lambda)"),
        new("nct_pdf", 3, 3, "nct_pdf(x, nu, delta)"),
        new("nct_logpdf", 3, 3, "nct_logpdf(x, nu, delta)"),
        new("nct_cdf", 3, 3, "nct_cdf(x, nu, delta)"),
        new("nct_sf", 3, 3, "nct_sf(x, nu, delta)"),
        new("nct_icdf", 3, 3, "nct_icdf(p, nu, delta)"),
        new("ncf_pdf", 4, 4, "ncf_pdf(x, d1, d2, lambda)"),
        new("ncf_logpdf", 4, 4, "ncf_logpdf(x, d1, d2, lambda)"),
        new("ncf_cdf", 4, 4, "ncf_cdf(x, d1, d2, lambda)"),
        new("ncf_sf", 4, 4, "ncf_sf(x, d1, d2, lambda)"),
        new("ncf_icdf", 4, 4, "ncf_icdf(p, d1, d2, lambda)"),
        new("expd_pdf", 2, 2, "expd_pdf(x, lambda)"),
        new("expd_logpdf", 2, 2, "expd_logpdf(x, lambda)"),
        new("expd_cdf", 2, 2, "expd_cdf(x, lambda)"),
        new("expd_sf", 2, 2, "expd_sf(x, lambda)"),
        new("expd_icdf", 2, 2, "expd_icdf(p, lambda)"),
        new("unif_pdf", 3, 3, "unif_pdf(x, a, b)"),
        new("unif_logpdf", 3, 3, "unif_logpdf(x, a, b)"),
        new("unif_cdf", 3, 3, "unif_cdf(x, a, b)"),
        new("unif_sf", 3, 3, "unif_sf(x, a, b)"),
        new("unif_icdf", 3, 3, "unif_icdf(p, a, b)"),
        new("weibull_pdf", 3, 3, "weibull_pdf(x, k, lambda)"),
        new("weibull_logpdf", 3, 3, "weibull_logpdf(x, k, lambda)"),
        new("weibull_cdf", 3, 3, "weibull_cdf(x, k, lambda)"),
        new("weibull_sf", 3, 3, "weibull_sf(x, k, lambda)"),
        new("weibull_icdf", 3, 3, "weibull_icdf(p, k, lambda)"),
        new("gamma_pdf", 3, 3, "gamma_pdf(x, alpha, beta_rate)"),
        new("gamma_logpdf", 3, 3, "gamma_logpdf(x, alpha, beta_rate)"),
        new("gamma_cdf", 3, 3, "gamma_cdf(x, alpha, beta_rate)"),
        new("gamma_sf", 3, 3, "gamma_sf(x, alpha, beta_rate)"),
        new("gamma_icdf", 3, 3, "gamma_icdf(p, alpha, beta_rate)"),
        new("beta_pdf", 3, 3, "beta_pdf(x, alpha, beta_param)"),
        new("beta_logpdf", 3, 3, "beta_logpdf(x, alpha, beta_param)"),
        new("beta_cdf", 3, 3, "beta_cdf(x, alpha, beta_param)"),
        new("beta_sf", 3, 3, "beta_sf(x, alpha, beta_param)"),
        new("beta_icdf", 3, 3, "beta_icdf(p, alpha, beta_param)"),
        new("laplace_pdf", 3, 3, "laplace_pdf(x, mu, b)"),
        new("laplace_logpdf", 3, 3, "laplace_logpdf(x, mu, b)"),
        new("laplace_cdf", 3, 3, "laplace_cdf(x, mu, b)"),
        new("laplace_sf", 3, 3, "laplace_sf(x, mu, b)"),
        new("laplace_icdf", 3, 3, "laplace_icdf(p, mu, b)"),
        new("cauchy_pdf", 3, 3, "cauchy_pdf(x, x0, gamma_scale)"),
        new("cauchy_logpdf", 3, 3, "cauchy_logpdf(x, x0, gamma_scale)"),
        new("cauchy_cdf", 3, 3, "cauchy_cdf(x, x0, gamma_scale)"),
        new("cauchy_sf", 3, 3, "cauchy_sf(x, x0, gamma_scale)"),
        new("cauchy_icdf", 3, 3, "cauchy_icdf(p, x0, gamma_scale)"),
        new("logistic_pdf", 3, 3, "logistic_pdf(x, mu, s)"),
        new("logistic_logpdf", 3, 3, "logistic_logpdf(x, mu, s)"),
        new("logistic_cdf", 3, 3, "logistic_cdf(x, mu, s)"),
        new("logistic_sf", 3, 3, "logistic_sf(x, mu, s)"),
        new("logistic_icdf", 3, 3, "logistic_icdf(p, mu, s)"),
        new("vonmises_pdf", 3, 3, "vonmises_pdf(x, mu, kappa)"),
        new("vonmises_logpdf", 3, 3, "vonmises_logpdf(x, mu, kappa)"),
        new("vonmises_cdf", 3, 3, "vonmises_cdf(x, mu, kappa)"),
        new("vonmises_sf", 3, 3, "vonmises_sf(x, mu, kappa)"),
        new("vonmises_icdf", 3, 3, "vonmises_icdf(p, mu, kappa)"),
        new("pareto_pdf", 3, 3, "pareto_pdf(x, xm, alpha)"),
        new("pareto_logpdf", 3, 3, "pareto_logpdf(x, xm, alpha)"),
        new("pareto_cdf", 3, 3, "pareto_cdf(x, xm, alpha)"),
        new("pareto_sf", 3, 3, "pareto_sf(x, xm, alpha)"),
        new("pareto_icdf", 3, 3, "pareto_icdf(p, xm, alpha)"),
        new("levy_pdf", 3, 3, "levy_pdf(x, mu, c)"),
        new("levy_logpdf", 3, 3, "levy_logpdf(x, mu, c)"),
        new("levy_cdf", 3, 3, "levy_cdf(x, mu, c)"),
        new("levy_sf", 3, 3, "levy_sf(x, mu, c)"),
        new("levy_icdf", 3, 3, "levy_icdf(p, mu, c)"),
        new("poisson_pmf", 2, 2, "poisson_pmf(k, lambda)"),
        new("poisson_logpmf", 2, 2, "poisson_logpmf(k, lambda)"),
        new("poisson_cdf", 2, 2, "poisson_cdf(k, lambda)"),
        new("poisson_sf", 2, 2, "poisson_sf(k, lambda)"),
        new("poisson_icdf", 2, 2, "poisson_icdf(p, lambda)"),
        new("binom_pmf", 3, 3, "binom_pmf(k, n, p_prob)"),
        new("binom_logpmf", 3, 3, "binom_logpmf(k, n, p_prob)"),
        new("binom_cdf", 3, 3, "binom_cdf(k, n, p_prob)"),
        new("binom_sf", 3, 3, "binom_sf(k, n, p_prob)"),
        new("binom_icdf", 3, 3, "binom_icdf(prob, n, p_prob)"),
        new("nbinom_pmf", 3, 3, "nbinom_pmf(k, r, p_prob)"),
        new("nbinom_logpmf", 3, 3, "nbinom_logpmf(k, r, p_prob)"),
        new("nbinom_cdf", 3, 3, "nbinom_cdf(k, r, p_prob)"),
        new("nbinom_sf", 3, 3, "nbinom_sf(k, r, p_prob)"),
        new("nbinom_icdf", 3, 3, "nbinom_icdf(prob, r, p_prob)"),
        new("geom_pmf", 2, 2, "geom_pmf(k, p_prob)"),
        new("geom_logpmf", 2, 2, "geom_logpmf(k, p_prob)"),
        new("geom_cdf", 2, 2, "geom_cdf(k, p_prob)"),
        new("geom_sf", 2, 2, "geom_sf(k, p_prob)"),
        new("geom_icdf", 2, 2, "geom_icdf(prob, p_prob)"),
        new("hypergeom_pmf", 4, 4, "hypergeom_pmf(k, N_pop, K_succ, n_draws)"),
        new("hypergeom_logpmf", 4, 4, "hypergeom_logpmf(k, N_pop, K_succ, n_draws)"),
        new("hypergeom_cdf", 4, 4, "hypergeom_cdf(k, N_pop, K_succ, n_draws)"),
        new("hypergeom_sf", 4, 4, "hypergeom_sf(k, N_pop, K_succ, n_draws)"),
        new("hypergeom_icdf", 4, 4, "hypergeom_icdf(prob, N_pop, K_succ, n_draws)"),
        new("tukey_q_pdf", 3, 3, "tukey_q_pdf(q, k_groups, nu)"),
        new("tukey_q_logpdf", 3, 3, "tukey_q_logpdf(q, k_groups, nu)"),
        new("tukey_q_cdf", 3, 3, "tukey_q_cdf(q, k_groups, nu)"),
        new("tukey_q_sf", 3, 3, "tukey_q_sf(q, k_groups, nu)"),
        new("tukey_q_icdf", 3, 3, "tukey_q_icdf(p, k_groups, nu)"),
        // Sprint D3: Scalar matrix projections (det2/3, Matrix-first solve2_x/y)
        new("det2", 4, 4, "det2(a11, a12, a21, a22)"),
        new("det3", 9, 9, "det3(a11, a12, a13, a21, a22, a23, a31, a32, a33)"),
        new("solve2_x", 6, 6, "solve2_x(a11, a12, a21, a22, b1, b2)"),
        new("solve2_y", 6, 6, "solve2_y(a11, a12, a21, a22, b1, b2)"),
        // Sprint D4: AST-bound 1D higher-order numerical analysis & quadrature functions
        new("diff_central", 3, 4, "diff_central(expr, var, x[, h])"),
        new("root_brent", 4, 5, "root_brent(expr, var, a, b[, tol])"),
        new("root_newton", 3, 4, "root_newton(expr, var, x0[, tol])"),
        new("root_secant", 4, 5, "root_secant(expr, var, x0, x1[, tol])"),
        new("quad_trapz", 5, 5, "quad_trapz(expr, var, a, b, n)"),
        new("quad_simpson", 5, 5, "quad_simpson(expr, var, a, b, n)"),
        new("quad_de", 4, 5, "quad_de(expr, var, a, b[, tol])"),
        new("quad_gauss_legendre", 5, 5, "quad_gauss_legendre(expr, var, a, b, n)"),
        new("quad_gk21", 4, 5, "quad_gk21(expr, var, a, b[, tol])"),
        new("quad_romberg", 4, 5, "quad_romberg(expr, var, a, b[, tol])"),
    };

    /// <summary>
    /// Evaluates one expression against <paramref name="names"/> (Ordinal lookup) using checked <see cref="decimal"/> arithmetic.
    /// Never throws for any non-null string input; throws only on null arguments or invalid <paramref name="limits"/>.
    /// </summary>
    public static CalculatorOutcome Evaluate(
        string expression,
        IReadOnlyDictionary<string, decimal> names,
        CalculatorLimits limits)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(limits);
        limits.Validate();

        if (expression.Length > limits.MaxExpressionChars)
        {
            return new CalculatorOutcome(
                null,
                new CalculatorError(
                    CalculatorErrorCode.ExpressionTooLong,
                    limits.MaxExpressionChars,
                    $"Expression exceeds {limits.MaxExpressionChars} characters."));
        }

        if (!CalculatorLexer.TryTokenize(expression, limits, out List<CalcToken> tokens, out CalculatorError? lexError))
        {
            return new CalculatorOutcome(null, lexError);
        }

        if (!CalculatorParser.TryParse(tokens, limits, out CalcNode? root, out CalculatorError? parseError))
        {
            return new CalculatorOutcome(null, parseError);
        }

        IReadOnlyDictionary<string, decimal> ordinalNames = ToOrdinalLookup(names);
        return CalculatorEvaluator.Evaluate(root!, ordinalNames, limits);
    }

    public static bool IsAsciiDigit(char ch) => ch is >= '0' and <= '9';

    public static bool IsIdentifierStart(char ch) => ch is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or '_';

    public static bool IsIdentifierPart(char ch) => IsIdentifierStart(ch) || IsAsciiDigit(ch);

    /// <summary>
    /// Validates whether <paramref name="name"/> satisfies the ASCII calculator identifier grammar
    /// (<c>[A-Za-z_][A-Za-z0-9_]{0,63}</c>).
    /// </summary>
    public static bool IsValidIdentifier(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > MaxIdentifierLength || !IsIdentifierStart(name[0]))
        {
            return false;
        }

        for (int i = 1; i < name.Length; i++)
        {
            if (!IsIdentifierPart(name[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static IReadOnlyDictionary<string, decimal> ToOrdinalLookup(IReadOnlyDictionary<string, decimal> names)
    {
        if (names.Count == 0)
        {
            return EmptyNames;
        }

        if (names is Dictionary<string, decimal> dict && ReferenceEquals(dict.Comparer, StringComparer.Ordinal))
        {
            return dict;
        }

        return new Dictionary<string, decimal>(names, StringComparer.Ordinal);
    }
}
