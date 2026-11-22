#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Calculator;
using Xunit;

namespace StockAnalyzer.Core.Tests.Calculator;

public sealed class CalculatorContractTests
{
    private static readonly IReadOnlyDictionary<string, decimal> Empty = new Dictionary<string, decimal>(StringComparer.Ordinal);

    [Fact]
    public void FunctionsTableMatchesFrozenContractOrderAndArities()
    {
        var expected = new (string Name, int Min, int Max, string Signature, bool IsVariadic)[]
        {
            ("abs", 1, 1, "abs(x)", false),
            ("sign", 1, 1, "sign(x)", false),
            ("min", 2, 8, "min(a, b, ...)", true),
            ("max", 2, 8, "max(a, b, ...)", true),
            ("clamp", 3, 3, "clamp(x, lo, hi)", false),
            ("floor", 1, 1, "floor(x)", false),
            ("ceil", 1, 1, "ceil(x)", false),
            ("trunc", 1, 1, "trunc(x)", false),
            ("round", 2, 2, "round(x, digits)", false),
            ("change_ratio", 2, 2, "change_ratio(from, to)", false),
            ("mod", 2, 2, "mod(a, b)", false),
            ("sum", 2, 8, "sum(a, b, ...)", true),
            ("avg", 2, 8, "avg(a, b, ...)", true),
            ("frac", 1, 1, "frac(x)", false),
            ("sqrt", 1, 1, "sqrt(x)", false),
            ("ln", 1, 1, "ln(x)", false),
            ("exp", 1, 1, "exp(x)", false),
            ("pow", 2, 2, "pow(x, y)", false),
            // Sprint D1 Phase-1 functions (appended after frozen v1/v2 indices 0..17)
            ("cbrt", 1, 1, "cbrt(x)", false),
            ("log10", 1, 1, "log10(x)", false),
            ("log2", 1, 1, "log2(x)", false),
            ("log", 2, 2, "log(b, x)", false),
            ("expm1", 1, 1, "expm1(x)", false),
            ("log1p", 1, 1, "log1p(x)", false),
            ("sin", 1, 1, "sin(x)", false),
            ("cos", 1, 1, "cos(x)", false),
            ("tan", 1, 1, "tan(x)", false),
            ("sind", 1, 1, "sind(deg)", false),
            ("cosd", 1, 1, "cosd(deg)", false),
            ("tand", 1, 1, "tand(deg)", false),
            ("asin", 1, 1, "asin(x)", false),
            ("acos", 1, 1, "acos(x)", false),
            ("atan", 1, 1, "atan(x)", false),
            ("atan2", 2, 2, "atan2(y, x)", false),
            ("sinh", 1, 1, "sinh(x)", false),
            ("cosh", 1, 1, "cosh(x)", false),
            ("tanh", 1, 1, "tanh(x)", false),
            ("asinh", 1, 1, "asinh(x)", false),
            ("acosh", 1, 1, "acosh(x)", false),
            ("atanh", 1, 1, "atanh(x)", false),
            ("sum_arith", 3, 3, "sum_arith(a, d, n)", false),
            ("sum_geom", 3, 3, "sum_geom(a, r, n)", false),
            ("fact", 1, 1, "fact(n)", false),
            ("double_fact", 1, 1, "double_fact(n)", false),
            ("perm", 2, 2, "perm(n, k)", false),
            ("comb", 2, 2, "comb(n, k)", false),
            ("lcomb", 2, 2, "lcomb(n, k)", false),
            ("fib", 1, 1, "fib(n)", false),
            ("lucas", 1, 1, "lucas(n)", false),
            ("solve_linear", 2, 2, "solve_linear(a, b)", false),
            ("quad_discriminant", 3, 3, "quad_discriminant(a, b, c)", false),
            ("quad_root_min", 3, 3, "quad_root_min(a, b, c)", false),
            ("quad_root_max", 3, 3, "quad_root_max(a, b, c)", false),
            ("hypot", 2, 2, "hypot(x, y)", false),
            ("dist2d", 4, 4, "dist2d(x1, y1, x2, y2)", false),
            ("dist3d", 6, 6, "dist3d(x1, y1, z1, x2, y2, z2)", false),
            ("tri_area_sss", 3, 3, "tri_area_sss(a, b, c)", false),
            ("tri_inradius", 3, 3, "tri_inradius(a, b, c)", false),
            ("tri_circumradius", 3, 3, "tri_circumradius(a, b, c)", false),
            ("sphere_vol", 1, 1, "sphere_vol(r)", false),
            ("cone_area", 2, 2, "cone_area(r, h)", false),
            ("torus_vol", 2, 2, "torus_vol(R, r)", false),
            ("c_abs", 2, 2, "c_abs(re, im)", false),
            ("c_arg", 2, 2, "c_arg(re, im)", false),
            ("c_exp_re", 2, 2, "c_exp_re(re, im)", false),
            ("c_exp_im", 2, 2, "c_exp_im(re, im)", false),
            ("bayes", 3, 3, "bayes(pA, pB_A, pB)", false),
            ("var_p", 2, 32, "var_p(a, b, ...)", true),
            ("var_s", 2, 32, "var_s(a, b, ...)", true),
            ("stdev_p", 2, 32, "stdev_p(a, b, ...)", true),
            ("stdev_s", 2, 32, "stdev_s(a, b, ...)", true),
            ("median", 2, 32, "median(a, b, ...)", true),
            ("sigmoid", 1, 1, "sigmoid(x)", false),
            ("relu", 1, 1, "relu(x)", false),
            ("leaky_relu", 2, 2, "leaky_relu(x, alpha)", false),
            ("elu", 2, 2, "elu(x, alpha)", false),
            ("gelu", 1, 1, "gelu(x)", false),
            ("silu", 1, 1, "silu(x)", false),
            ("softplus", 1, 1, "softplus(x)", false),
            ("swish", 2, 2, "swish(x, beta)", false),
            // Sprint D2: Extended variadic statistics
            ("geomean", 2, 32, "geomean(a, b, ...)", true),
            ("harmmean", 2, 32, "harmmean(a, b, ...)", true),
            ("skew", 3, 32, "skew(a, b, c, ...)", true),
            ("kurt", 4, 32, "kurt(a, b, c, d, ...)", true),
            ("quantile", 3, 32, "quantile(p, a, b, ...)", true),
            ("iqr", 2, 32, "iqr(a, b, ...)", true),
            ("mad", 2, 32, "mad(a, b, ...)", true),
            ("zscore", 3, 32, "zscore(x, a, b, ...)", true),
            ("softplus_lse", 2, 32, "softplus_lse(a, b, ...)", true),
            // Sprint D2: Special functions
            ("gamma", 1, 1, "gamma(x)", false),
            ("lgamma", 1, 1, "lgamma(x)", false),
            ("digamma", 1, 1, "digamma(x)", false),
            ("inc_gamma_p", 2, 2, "inc_gamma_p(a, x)", false),
            ("inc_gamma_q", 2, 2, "inc_gamma_q(a, x)", false),
            ("beta", 2, 2, "beta(a, b)", false),
            ("lbeta", 2, 2, "lbeta(a, b)", false),
            ("inc_beta", 3, 3, "inc_beta(x, a, b)", false),
            ("zeta", 1, 1, "zeta(s)", false),
            ("hurwitz_zeta", 2, 2, "hurwitz_zeta(s, q)", false),
            ("bessel_j", 2, 2, "bessel_j(nu, x)", false),
            ("bessel_y", 2, 2, "bessel_y(nu, x)", false),
            ("bessel_i", 2, 2, "bessel_i(nu, x)", false),
            ("bessel_k", 2, 2, "bessel_k(nu, x)", false),
            ("bessel_ie", 2, 2, "bessel_ie(nu, x)", false),
            ("bessel_ke", 2, 2, "bessel_ke(nu, x)", false),
            ("sph_bessel_j", 2, 2, "sph_bessel_j(n, x)", false),
            ("sph_bessel_y", 2, 2, "sph_bessel_y(n, x)", false),
            ("sph_bessel_i", 2, 2, "sph_bessel_i(n, x)", false),
            ("sph_bessel_k", 2, 2, "sph_bessel_k(n, x)", false),
            ("airy_ai", 1, 1, "airy_ai(x)", false),
            ("airy_bi", 1, 1, "airy_bi(x)", false),
            ("airy_aip", 1, 1, "airy_aip(x)", false),
            ("airy_bip", 1, 1, "airy_bip(x)", false),
            ("kelvin_ber", 1, 1, "kelvin_ber(x)", false),
            ("kelvin_bei", 1, 1, "kelvin_bei(x)", false),
            ("kelvin_ker", 1, 1, "kelvin_ker(x)", false),
            ("kelvin_kei", 1, 1, "kelvin_kei(x)", false),
            ("struve_h", 2, 2, "struve_h(nu, x)", false),
            ("struve_l", 2, 2, "struve_l(nu, x)", false),
            ("erf", 1, 1, "erf(x)", false),
            ("erfc", 1, 1, "erfc(x)", false),
            ("erfcx", 1, 1, "erfcx(x)", false),
            ("erfi", 1, 1, "erfi(x)", false),
            ("erfinv", 1, 1, "erfinv(y)", false),
            ("erfcinv", 1, 1, "erfcinv(y)", false),
            ("legendre_p", 2, 2, "legendre_p(l, x)", false),
            ("legendre_q", 2, 2, "legendre_q(l, x)", false),
            ("assoc_legendre", 3, 3, "assoc_legendre(l, m, x)", false),
            ("bernoulli", 1, 1, "bernoulli(n)", false),
            ("stirling1", 2, 2, "stirling1(n, k)", false),
            ("stirling2", 2, 2, "stirling2(n, k)", false),
            ("pochhammer", 2, 2, "pochhammer(x, n)", false),
            ("brillouin", 2, 2, "brillouin(J, x)", false),
            ("langevin", 1, 1, "langevin(x)", false),
            ("expint_ei", 1, 1, "expint_ei(x)", false),
            ("expint_e1", 1, 1, "expint_e1(x)", false),
            ("sinint_si", 1, 1, "sinint_si(x)", false),
            ("cosint_ci", 1, 1, "cosint_ci(x)", false),
            ("logint_li", 1, 1, "logint_li(x)", false),
            ("fresnel_s", 1, 1, "fresnel_s(x)", false),
            ("fresnel_c", 1, 1, "fresnel_c(x)", false),
            ("ellip_k", 1, 1, "ellip_k(k)", false),
            ("ellip_e", 1, 1, "ellip_e(k)", false),
            ("ellip_f", 2, 2, "ellip_f(phi, k)", false),
            ("ellip_e_inc", 2, 2, "ellip_e_inc(phi, k)", false),
            ("ellip_pi", 3, 3, "ellip_pi(n, phi, k)", false),
            ("jacobi_sn", 2, 2, "jacobi_sn(u, m)", false),
            ("jacobi_cn", 2, 2, "jacobi_cn(u, m)", false),
            ("jacobi_dn", 2, 2, "jacobi_dn(u, m)", false),
            ("chebyshev_t", 2, 2, "chebyshev_t(n, x)", false),
            ("chebyshev_u", 2, 2, "chebyshev_u(n, x)", false),
            ("hermite_h", 2, 2, "hermite_h(n, x)", false),
            ("laguerre_l", 3, 3, "laguerre_l(n, alpha, x)", false),
            ("jacobi_p", 4, 4, "jacobi_p(n, alpha, beta, x)", false),
            ("hyp0f1", 2, 2, "hyp0f1(b, z)", false),
            ("hyp1f1", 3, 3, "hyp1f1(a, b, z)", false),
            ("hyp2f1", 4, 4, "hyp2f1(a, b, c, z)", false),
            // Sprint D2: Probability distributions
            ("norm_pdf", 1, 3, "norm_pdf(x[, mu, sigma])", false),
            ("norm_logpdf", 1, 3, "norm_logpdf(x[, mu, sigma])", false),
            ("norm_cdf", 1, 3, "norm_cdf(x[, mu, sigma])", false),
            ("norm_sf", 1, 3, "norm_sf(x[, mu, sigma])", false),
            ("norm_icdf", 1, 3, "norm_icdf(p[, mu, sigma])", false),
            ("lognorm_pdf", 3, 3, "lognorm_pdf(x, mu, sigma)", false),
            ("lognorm_logpdf", 3, 3, "lognorm_logpdf(x, mu, sigma)", false),
            ("lognorm_cdf", 3, 3, "lognorm_cdf(x, mu, sigma)", false),
            ("lognorm_sf", 3, 3, "lognorm_sf(x, mu, sigma)", false),
            ("lognorm_icdf", 3, 3, "lognorm_icdf(p, mu, sigma)", false),
            ("bvnorm_pdf", 3, 7, "bvnorm_pdf(x, y[, mu1, mu2, sigma1, sigma2], rho)", false),
            ("bvnorm_logpdf", 3, 7, "bvnorm_logpdf(x, y[, mu1, mu2, sigma1, sigma2], rho)", false),
            ("bvnorm_cdf", 3, 7, "bvnorm_cdf(x, y[, mu1, mu2, sigma1, sigma2], rho)", false),
            ("bvnorm_sf", 3, 7, "bvnorm_sf(x, y[, mu1, mu2, sigma1, sigma2], rho)", false),
            ("chi2_pdf", 2, 2, "chi2_pdf(x, nu)", false),
            ("chi2_logpdf", 2, 2, "chi2_logpdf(x, nu)", false),
            ("chi2_cdf", 2, 2, "chi2_cdf(x, nu)", false),
            ("chi2_sf", 2, 2, "chi2_sf(x, nu)", false),
            ("chi2_icdf", 2, 2, "chi2_icdf(p, nu)", false),
            ("t_pdf", 2, 2, "t_pdf(x, nu)", false),
            ("t_logpdf", 2, 2, "t_logpdf(x, nu)", false),
            ("t_cdf", 2, 2, "t_cdf(x, nu)", false),
            ("t_sf", 2, 2, "t_sf(x, nu)", false),
            ("t_icdf", 2, 2, "t_icdf(p, nu)", false),
            ("f_pdf", 3, 3, "f_pdf(x, d1, d2)", false),
            ("f_logpdf", 3, 3, "f_logpdf(x, d1, d2)", false),
            ("f_cdf", 3, 3, "f_cdf(x, d1, d2)", false),
            ("f_sf", 3, 3, "f_sf(x, d1, d2)", false),
            ("f_icdf", 3, 3, "f_icdf(p, d1, d2)", false),
            ("ncchi2_pdf", 3, 3, "ncchi2_pdf(x, nu, lambda)", false),
            ("ncchi2_logpdf", 3, 3, "ncchi2_logpdf(x, nu, lambda)", false),
            ("ncchi2_cdf", 3, 3, "ncchi2_cdf(x, nu, lambda)", false),
            ("ncchi2_sf", 3, 3, "ncchi2_sf(x, nu, lambda)", false),
            ("ncchi2_icdf", 3, 3, "ncchi2_icdf(p, nu, lambda)", false),
            ("nct_pdf", 3, 3, "nct_pdf(x, nu, delta)", false),
            ("nct_logpdf", 3, 3, "nct_logpdf(x, nu, delta)", false),
            ("nct_cdf", 3, 3, "nct_cdf(x, nu, delta)", false),
            ("nct_sf", 3, 3, "nct_sf(x, nu, delta)", false),
            ("nct_icdf", 3, 3, "nct_icdf(p, nu, delta)", false),
            ("ncf_pdf", 4, 4, "ncf_pdf(x, d1, d2, lambda)", false),
            ("ncf_logpdf", 4, 4, "ncf_logpdf(x, d1, d2, lambda)", false),
            ("ncf_cdf", 4, 4, "ncf_cdf(x, d1, d2, lambda)", false),
            ("ncf_sf", 4, 4, "ncf_sf(x, d1, d2, lambda)", false),
            ("ncf_icdf", 4, 4, "ncf_icdf(p, d1, d2, lambda)", false),
            ("expd_pdf", 2, 2, "expd_pdf(x, lambda)", false),
            ("expd_logpdf", 2, 2, "expd_logpdf(x, lambda)", false),
            ("expd_cdf", 2, 2, "expd_cdf(x, lambda)", false),
            ("expd_sf", 2, 2, "expd_sf(x, lambda)", false),
            ("expd_icdf", 2, 2, "expd_icdf(p, lambda)", false),
            ("unif_pdf", 3, 3, "unif_pdf(x, a, b)", false),
            ("unif_logpdf", 3, 3, "unif_logpdf(x, a, b)", false),
            ("unif_cdf", 3, 3, "unif_cdf(x, a, b)", false),
            ("unif_sf", 3, 3, "unif_sf(x, a, b)", false),
            ("unif_icdf", 3, 3, "unif_icdf(p, a, b)", false),
            ("weibull_pdf", 3, 3, "weibull_pdf(x, k, lambda)", false),
            ("weibull_logpdf", 3, 3, "weibull_logpdf(x, k, lambda)", false),
            ("weibull_cdf", 3, 3, "weibull_cdf(x, k, lambda)", false),
            ("weibull_sf", 3, 3, "weibull_sf(x, k, lambda)", false),
            ("weibull_icdf", 3, 3, "weibull_icdf(p, k, lambda)", false),
            ("gamma_pdf", 3, 3, "gamma_pdf(x, alpha, beta_rate)", false),
            ("gamma_logpdf", 3, 3, "gamma_logpdf(x, alpha, beta_rate)", false),
            ("gamma_cdf", 3, 3, "gamma_cdf(x, alpha, beta_rate)", false),
            ("gamma_sf", 3, 3, "gamma_sf(x, alpha, beta_rate)", false),
            ("gamma_icdf", 3, 3, "gamma_icdf(p, alpha, beta_rate)", false),
            ("beta_pdf", 3, 3, "beta_pdf(x, alpha, beta_param)", false),
            ("beta_logpdf", 3, 3, "beta_logpdf(x, alpha, beta_param)", false),
            ("beta_cdf", 3, 3, "beta_cdf(x, alpha, beta_param)", false),
            ("beta_sf", 3, 3, "beta_sf(x, alpha, beta_param)", false),
            ("beta_icdf", 3, 3, "beta_icdf(p, alpha, beta_param)", false),
            ("laplace_pdf", 3, 3, "laplace_pdf(x, mu, b)", false),
            ("laplace_logpdf", 3, 3, "laplace_logpdf(x, mu, b)", false),
            ("laplace_cdf", 3, 3, "laplace_cdf(x, mu, b)", false),
            ("laplace_sf", 3, 3, "laplace_sf(x, mu, b)", false),
            ("laplace_icdf", 3, 3, "laplace_icdf(p, mu, b)", false),
            ("cauchy_pdf", 3, 3, "cauchy_pdf(x, x0, gamma_scale)", false),
            ("cauchy_logpdf", 3, 3, "cauchy_logpdf(x, x0, gamma_scale)", false),
            ("cauchy_cdf", 3, 3, "cauchy_cdf(x, x0, gamma_scale)", false),
            ("cauchy_sf", 3, 3, "cauchy_sf(x, x0, gamma_scale)", false),
            ("cauchy_icdf", 3, 3, "cauchy_icdf(p, x0, gamma_scale)", false),
            ("logistic_pdf", 3, 3, "logistic_pdf(x, mu, s)", false),
            ("logistic_logpdf", 3, 3, "logistic_logpdf(x, mu, s)", false),
            ("logistic_cdf", 3, 3, "logistic_cdf(x, mu, s)", false),
            ("logistic_sf", 3, 3, "logistic_sf(x, mu, s)", false),
            ("logistic_icdf", 3, 3, "logistic_icdf(p, mu, s)", false),
            ("vonmises_pdf", 3, 3, "vonmises_pdf(x, mu, kappa)", false),
            ("vonmises_logpdf", 3, 3, "vonmises_logpdf(x, mu, kappa)", false),
            ("vonmises_cdf", 3, 3, "vonmises_cdf(x, mu, kappa)", false),
            ("vonmises_sf", 3, 3, "vonmises_sf(x, mu, kappa)", false),
            ("vonmises_icdf", 3, 3, "vonmises_icdf(p, mu, kappa)", false),
            ("pareto_pdf", 3, 3, "pareto_pdf(x, xm, alpha)", false),
            ("pareto_logpdf", 3, 3, "pareto_logpdf(x, xm, alpha)", false),
            ("pareto_cdf", 3, 3, "pareto_cdf(x, xm, alpha)", false),
            ("pareto_sf", 3, 3, "pareto_sf(x, xm, alpha)", false),
            ("pareto_icdf", 3, 3, "pareto_icdf(p, xm, alpha)", false),
            ("levy_pdf", 3, 3, "levy_pdf(x, mu, c)", false),
            ("levy_logpdf", 3, 3, "levy_logpdf(x, mu, c)", false),
            ("levy_cdf", 3, 3, "levy_cdf(x, mu, c)", false),
            ("levy_sf", 3, 3, "levy_sf(x, mu, c)", false),
            ("levy_icdf", 3, 3, "levy_icdf(p, mu, c)", false),
            ("poisson_pmf", 2, 2, "poisson_pmf(k, lambda)", false),
            ("poisson_logpmf", 2, 2, "poisson_logpmf(k, lambda)", false),
            ("poisson_cdf", 2, 2, "poisson_cdf(k, lambda)", false),
            ("poisson_sf", 2, 2, "poisson_sf(k, lambda)", false),
            ("poisson_icdf", 2, 2, "poisson_icdf(p, lambda)", false),
            ("binom_pmf", 3, 3, "binom_pmf(k, n, p_prob)", false),
            ("binom_logpmf", 3, 3, "binom_logpmf(k, n, p_prob)", false),
            ("binom_cdf", 3, 3, "binom_cdf(k, n, p_prob)", false),
            ("binom_sf", 3, 3, "binom_sf(k, n, p_prob)", false),
            ("binom_icdf", 3, 3, "binom_icdf(prob, n, p_prob)", false),
            ("nbinom_pmf", 3, 3, "nbinom_pmf(k, r, p_prob)", false),
            ("nbinom_logpmf", 3, 3, "nbinom_logpmf(k, r, p_prob)", false),
            ("nbinom_cdf", 3, 3, "nbinom_cdf(k, r, p_prob)", false),
            ("nbinom_sf", 3, 3, "nbinom_sf(k, r, p_prob)", false),
            ("nbinom_icdf", 3, 3, "nbinom_icdf(prob, r, p_prob)", false),
            ("geom_pmf", 2, 2, "geom_pmf(k, p_prob)", false),
            ("geom_logpmf", 2, 2, "geom_logpmf(k, p_prob)", false),
            ("geom_cdf", 2, 2, "geom_cdf(k, p_prob)", false),
            ("geom_sf", 2, 2, "geom_sf(k, p_prob)", false),
            ("geom_icdf", 2, 2, "geom_icdf(prob, p_prob)", false),
            ("hypergeom_pmf", 4, 4, "hypergeom_pmf(k, N_pop, K_succ, n_draws)", false),
            ("hypergeom_logpmf", 4, 4, "hypergeom_logpmf(k, N_pop, K_succ, n_draws)", false),
            ("hypergeom_cdf", 4, 4, "hypergeom_cdf(k, N_pop, K_succ, n_draws)", false),
            ("hypergeom_sf", 4, 4, "hypergeom_sf(k, N_pop, K_succ, n_draws)", false),
            ("hypergeom_icdf", 4, 4, "hypergeom_icdf(prob, N_pop, K_succ, n_draws)", false),
            ("tukey_q_pdf", 3, 3, "tukey_q_pdf(q, k_groups, nu)", false),
            ("tukey_q_logpdf", 3, 3, "tukey_q_logpdf(q, k_groups, nu)", false),
            ("tukey_q_cdf", 3, 3, "tukey_q_cdf(q, k_groups, nu)", false),
            ("tukey_q_sf", 3, 3, "tukey_q_sf(q, k_groups, nu)", false),
            ("tukey_q_icdf", 3, 3, "tukey_q_icdf(p, k_groups, nu)", false),
            // Sprint D3: Scalar matrix projections
            ("det2", 4, 4, "det2(a11, a12, a21, a22)", false),
            ("det3", 9, 9, "det3(a11, a12, a13, a21, a22, a23, a31, a32, a33)", false),
            ("solve2_x", 6, 6, "solve2_x(a11, a12, a21, a22, b1, b2)", false),
            ("solve2_y", 6, 6, "solve2_y(a11, a12, a21, a22, b1, b2)", false),
            // Sprint D4: AST-bound 1D higher-order numerical analysis & quadrature functions
            ("diff_central", 3, 4, "diff_central(expr, var, x[, h])", false),
            ("root_brent", 4, 5, "root_brent(expr, var, a, b[, tol])", false),
            ("root_newton", 3, 4, "root_newton(expr, var, x0[, tol])", false),
            ("root_secant", 4, 5, "root_secant(expr, var, x0, x1[, tol])", false),
            ("quad_trapz", 5, 5, "quad_trapz(expr, var, a, b, n)", false),
            ("quad_simpson", 5, 5, "quad_simpson(expr, var, a, b, n)", false),
            ("quad_de", 4, 5, "quad_de(expr, var, a, b[, tol])", false),
            ("quad_gauss_legendre", 5, 5, "quad_gauss_legendre(expr, var, a, b, n)", false),
            ("quad_gk21", 4, 5, "quad_gk21(expr, var, a, b[, tol])", false),
            ("quad_romberg", 4, 5, "quad_romberg(expr, var, a, b[, tol])", false),
            ("arr_len", 1, 1, "arr_len(a)", false),
            ("arr_get", 2, 2, "arr_get(a, idx)", false),
            ("arr_slice", 3, 3, "arr_slice(a, start, len)", false),
            ("arr_window", 2, 2, "arr_window(series, period)", false),
            ("arr_zip_add", 2, 2, "arr_zip_add(a, b)", false),
            ("arr_zip_mul", 2, 2, "arr_zip_mul(a, b)", false),
            ("has_flag", 2, 2, "has_flag(flags, bitIndex)", false),
            ("mat_cond", 3, 4, "mat_cond(A, rows, cols[, normKind])", false),
            ("mat_rcond", 3, 4, "mat_rcond(A, rows, cols[, normKind])", false),
            ("mat_validate_corr", 1, 2, "mat_validate_corr(R[, tol])", false),
            ("mat_is_corr", 1, 3, "mat_is_corr(R[, tol, requirePd])", false),
            ("mat_nearest_corr", 1, 2, "mat_nearest_corr(R[, tol])", false),
            ("mat_validate_cov", 1, 3, "mat_validate_cov(S[, requirePd, tol])", false),
            ("mat_cov_to_corr", 1, 1, "mat_cov_to_corr(S)", false),
            ("weighted_mean", 2, 2, "weighted_mean(w, x)", false),
            ("weighted_var", 2, 2, "weighted_var(w, x)", false),
            ("mahalanobis", 3, 3, "mahalanobis(x, mu, covInv)", false),
            ("ols_fit", 2, 2, "ols_fit(X, y)", false),
            ("ols_predict", 2, 2, "ols_predict(xNew, beta)", false),
            ("ridge_fit", 3, 3, "ridge_fit(X, y, lambda)", false),
            ("poly_project", 3, 3, "poly_project(series, deg, horizon)", false),
            ("pca_project", 4, 4, "pca_project(X, nRows, nCols, k)", false),
            ("mad_scaled", 1, 2, "mad_scaled(x[, scale])", false),
            ("trimmed_mean", 2, 2, "trimmed_mean(x, alpha)", false),
            ("winsor_mean", 2, 2, "winsor_mean(x, alpha)", false),
            ("huber_loc", 1, 2, "huber_loc(x[, k])", false),
            ("biweight_loc", 1, 2, "biweight_loc(x[, c])", false),
            ("theil_sen", 1, 2, "theil_sen(yOrX[, y])", false),
            ("siegel_slope", 1, 2, "siegel_slope(yOrX[, y])", false),
            ("acf", 2, 2, "acf(series, lag)", false),
            ("acf_series", 2, 2, "acf_series(series, maxLag)", false),
            ("pacf", 2, 2, "pacf(series, lag)", false),
            ("pacf_series", 2, 2, "pacf_series(series, maxLag)", false),
            ("p_adjust", 2, 2, "p_adjust(pValues, method)", false),
            ("diag_residual_norm", 3, 3, "diag_residual_norm(A, x, b)", false),
            ("diag_rel_err", 2, 2, "diag_rel_err(approx, exact)", false),
            ("diag_ulp_diff", 2, 2, "diag_ulp_diff(a, b)", false),
            ("diag_kahan_sum", 1, 1, "diag_kahan_sum(arr)", false),
            ("diag_kahan_err", 1, 1, "diag_kahan_err(arr)", false),
            ("diag_cancellation_bits", 2, 2, "diag_cancellation_bits(a, b)", false),
            ("boot_ci", 6, 7, "boot_ci(series, statKind, nResamples, blockLen, alpha, seed[, blockMode])", false),
            ("mc_integrate", 6, 7, "mc_integrate(expr, var, a, b, nSamples, seed[, mode])", false),
            ("mc_integrate_se", 6, 7, "mc_integrate_se(expr, var, a, b, nSamples, seed[, mode])", false),
            ("mc_integrate_nd", 6, 7, "mc_integrate_nd(expr, vars, lower, upper, nSamples, seed[, mode])", false),
            ("mc_integrate_nd_se", 6, 7, "mc_integrate_nd_se(expr, vars, lower, upper, nSamples, seed[, mode])", false),
            ("diff_lag", 1, 3, "diff_lag(series[, k, order])", false),
            ("diff_lag_series", 1, 3, "diff_lag_series(series[, k, order])", false),
            ("diff_pct", 1, 2, "diff_pct(series[, k])", false),
            ("fd_forward", 4, 4, "fd_forward(expr, var, x, h)", false),
            ("fd_backward", 4, 4, "fd_backward(expr, var, x, h)", false),
            ("fd_central", 3, 4, "fd_central(expr, var, x[, h])", false),
            ("fd_second", 4, 4, "fd_second(expr, var, x, h)", false),
            ("fd_richardson", 4, 5, "fd_richardson(expr, var, x, h[, order])", false),
            ("fd_pde_1d", 5, 7, "fd_pde_1d(u0, alpha, dx, dt, nSteps[, scheme, bcType])", false),
            ("vec_dot", 2, 2, "vec_dot(a, b)", false),
            ("vec_norm", 1, 1, "vec_norm(v)", false),
            ("vec_normalize", 1, 1, "vec_normalize(v)", false),
            ("cross3d", 2, 2, "cross3d(u, v)", false),
            ("mat_transpose", 3, 3, "mat_transpose(A, rows, cols)", false),
            ("mat_mul", 6, 6, "mat_mul(A, rA, cA, B, rB, cB)", false),
            ("mat_det", 2, 2, "mat_det(A, n)", false),
            ("mat_solve", 3, 3, "mat_solve(A, n, b)", false),
            ("mat_inv", 2, 2, "mat_inv(A, n)", false),
            ("mat_lstsq", 4, 4, "mat_lstsq(A, rows, cols, b)", false),
            ("mat_chol", 2, 2, "mat_chol(A, n)", false),
            ("mat_qr_q", 3, 3, "mat_qr_q(A, rows, cols)", false),
            ("mat_qr_r", 3, 3, "mat_qr_r(A, rows, cols)", false),
            ("mat_eig_sym_vals", 2, 2, "mat_eig_sym_vals(A, n)", false),
            ("mat_eig_sym_vecs", 2, 2, "mat_eig_sym_vecs(A, n)", false),
            ("mat_svd_s", 3, 3, "mat_svd_s(A, rows, cols)", false),
            ("mat_svd_u", 3, 3, "mat_svd_u(A, rows, cols)", false),
            ("mat_svd_vt", 3, 3, "mat_svd_vt(A, rows, cols)", false),
            ("mat_rot2d", 1, 1, "mat_rot2d(angleRad)", false),
            ("quad_gauss_chebyshev", 4, 4, "quad_gauss_chebyshev(expr, var, kind, n)", false),
            ("quad_gauss_laguerre", 4, 4, "quad_gauss_laguerre(expr, var, alpha, n)", false),
            ("quad_gauss_hermite", 3, 3, "quad_gauss_hermite(expr, var, n)", false),
            ("quad_gauss_jacobi", 5, 5, "quad_gauss_jacobi(expr, var, alpha, beta, n)", false),
            ("quad_gauss_lobatto", 5, 5, "quad_gauss_lobatto(expr, var, a, b, n)", false),
            ("ode_rk4", 7, 7, "ode_rk4(rhsExprs, tVar, yVars, t0, t1, y0, nSteps)", false),
            ("ode_rk45", 6, 7, "ode_rk45(rhsExprs, tVar, yVars, t0, t1, y0[, tol])", false),
            ("solve_cubic", 4, 4, "solve_cubic(a, b, c, d)", false),
            ("solve_quartic", 5, 5, "solve_quartic(a, b, c, d, e)", false),
            ("c_mul_re", 4, 4, "c_mul_re(aRe, aIm, bRe, bIm)", false),
            ("c_mul_im", 4, 4, "c_mul_im(aRe, aIm, bRe, bIm)", false),
            ("c_div_re", 4, 4, "c_div_re(aRe, aIm, bRe, bIm)", false),
            ("c_div_im", 4, 4, "c_div_im(aRe, aIm, bRe, bIm)", false),
            ("c_ln_re", 2, 2, "c_ln_re(re, im)", false),
            ("c_ln_im", 2, 2, "c_ln_im(re, im)", false),
            ("c_sqrt_re", 2, 2, "c_sqrt_re(re, im)", false),
            ("c_sqrt_im", 2, 2, "c_sqrt_im(re, im)", false),
            ("c_pow_re", 4, 4, "c_pow_re(zRe, zIm, wRe, wIm)", false),
            ("c_pow_im", 4, 4, "c_pow_im(zRe, zIm, wRe, wIm)", false),
            ("opt_brent", 4, 5, "opt_brent(expr, var, a, b[, tol])", false),
            ("opt_golden", 4, 5, "opt_golden(expr, var, a, b[, tol])", false),
            ("opt_nelder_mead", 3, 4, "opt_nelder_mead(expr, vars, x0[, tol])", false),
            ("opt_bfgs", 3, 4, "opt_bfgs(expr, vars, x0[, tol])", false),
            ("cov_s", 2, 2, "cov_s(x, y)", false),
            ("cov_p", 2, 2, "cov_p(x, y)", false),
            ("corr_pearson", 2, 2, "corr_pearson(x, y)", false),
            ("corr_spearman", 2, 2, "corr_spearman(x, y)", false),
            ("corr_kendall", 2, 2, "corr_kendall(x, y)", false),
            ("ccf", 3, 3, "ccf(x, y, lag)", false),
            ("ttest_1samp", 2, 2, "ttest_1samp(x, mu0)", false),
            ("ttest_2samp", 2, 2, "ttest_2samp(x, y)", false),
            ("jb_test", 1, 1, "jb_test(x)", false),
            ("ljung_box", 2, 2, "ljung_box(x, lags)", false),
            ("ks_2samp", 2, 2, "ks_2samp(x, y)", false),
            ("if_gt", 4, 4, "if_gt(a, b, whenTrue, whenFalse)", false),
            ("if_ge", 4, 4, "if_ge(a, b, whenTrue, whenFalse)", false),
            ("if_eq", 4, 5, "if_eq(a, b, whenTrue, whenFalse[, eps])", false),
            ("step", 1, 1, "step(x)", false),
            ("arr_zip_sub", 2, 2, "arr_zip_sub(a, b)", false),
            ("arr_zip_div", 2, 2, "arr_zip_div(a, b)", false),
            ("arr_scale", 2, 2, "arr_scale(a, c)", false),
            ("arr_cumsum", 1, 1, "arr_cumsum(a)", false),
            ("arr_cumprod", 1, 1, "arr_cumprod(a)", false),
            ("arr_sort", 1, 2, "arr_sort(a[, order])", false),
            ("arr_rank", 1, 2, "arr_rank(a[, order])", false),
            ("arr_argmax", 1, 1, "arr_argmax(a)", false),
            ("arr_argmin", 1, 1, "arr_argmin(a)", false),
            ("interp_linear", 3, 3, "interp_linear(xs, ys, x)", false),
            ("interp_cubic_spline", 3, 3, "interp_cubic_spline(xs, ys, x)", false),
            ("interp_pchip", 3, 3, "interp_pchip(xs, ys, x)", false),
            ("bs_price", 6, 7, "bs_price(S, K, T, r, sigma, isCall[, q])", false),
            ("bs_delta", 6, 7, "bs_delta(S, K, T, r, sigma, isCall[, q])", false),
            ("bs_gamma", 5, 6, "bs_gamma(S, K, T, r, sigma[, q])", false),
            ("bs_vega", 5, 6, "bs_vega(S, K, T, r, sigma[, q])", false),
            ("bs_theta", 6, 7, "bs_theta(S, K, T, r, sigma, isCall[, q])", false),
            ("bs_implied_vol", 6, 7, "bs_implied_vol(price, S, K, T, r, isCall[, q])", false),
            ("npv", 2, 2, "npv(rate, cf)", false),
            ("irr", 1, 2, "irr(cf[, guess])", false),
            ("max_drawdown", 1, 1, "max_drawdown(equity)", false),
            ("cvar_emp", 2, 2, "cvar_emp(returns, confidence)", false),
            ("shannon_entropy", 1, 1, "shannon_entropy(probs)", false),
            ("kl_divergence", 2, 2, "kl_divergence(p, q)", false),
            ("goertzel", 2, 2, "goertzel(series, period)", false),
            ("dft_mag", 1, 1, "dft_mag(series)", false),
        };

        // V1-V5 prefix of 437 entries remains frozen in order; DSL v6 appended 95 entries (total 532).
        Assert.Equal(437, expected.Length);
        Assert.Equal(532, ExpressionCalculator.Functions.Count);
        for (int i = 0; i < expected.Length; i++)
        {
            CalculatorFunctionInfo actual = ExpressionCalculator.Functions[i];
            Assert.Equal(expected[i].Name, actual.Name);
            Assert.Equal(expected[i].Min, actual.MinArguments);
            Assert.Equal(expected[i].Max, actual.MaxArguments);
            Assert.Equal(expected[i].Signature, actual.Signature);
            Assert.Equal(expected[i].IsVariadic, actual.IsVariadic);
            Assert.Equal(
                actual.Name is "var_p" or "var_s" or "stdev_p" or "stdev_s" or "median"
                    or "geomean" or "harmmean" or "skew" or "kurt" or "quantile"
                    or "iqr" or "mad" or "zscore" or "softplus_lse",
                actual.IsStatisticalVariadic);
        }
    }

    [Fact]
    public void ErrorCodeEnumOrderAndWireNamesMatchFrozenContract()
    {
        var expected = new (CalculatorErrorCode Code, string WireName)[]
        {
            (CalculatorErrorCode.SyntaxError, "SYNTAX_ERROR"),
            (CalculatorErrorCode.UnexpectedCharacter, "UNEXPECTED_CHARACTER"),
            (CalculatorErrorCode.NumberOutOfRange, "NUMBER_OUT_OF_RANGE"),
            (CalculatorErrorCode.ExpressionTooLong, "EXPRESSION_TOO_LONG"),
            (CalculatorErrorCode.TooManyTokens, "TOO_MANY_TOKENS"),
            (CalculatorErrorCode.TooDeep, "TOO_DEEP"),
            (CalculatorErrorCode.UndefinedName, "UNDEFINED_NAME"),
            (CalculatorErrorCode.UnknownFunction, "UNKNOWN_FUNCTION"),
            (CalculatorErrorCode.WrongArgumentCount, "WRONG_ARGUMENT_COUNT"),
            (CalculatorErrorCode.DivisionByZero, "DIVISION_BY_ZERO"),
            (CalculatorErrorCode.ArithmeticOverflow, "ARITHMETIC_OVERFLOW"),
            (CalculatorErrorCode.NonIntegerExponent, "NON_INTEGER_EXPONENT"),
            (CalculatorErrorCode.ExponentOutOfRange, "EXPONENT_OUT_OF_RANGE"),
            (CalculatorErrorCode.DomainError, "DOMAIN_ERROR"),
            (CalculatorErrorCode.ConvergenceFailure, "CONVERGENCE_FAILURE"),
            (CalculatorErrorCode.SingularMatrix, "SINGULAR_MATRIX"),
            (CalculatorErrorCode.DimensionMismatch, "DIMENSION_MISMATCH"),
            (CalculatorErrorCode.BudgetExceeded, "BUDGET_EXCEEDED"),
            (CalculatorErrorCode.NonFiniteResult, "NON_FINITE_RESULT"),
        };

        CalculatorErrorCode[] actualValues = Enum.GetValues<CalculatorErrorCode>();
        Assert.Equal(expected.Select(x => x.Code).ToArray(), actualValues);

        foreach ((CalculatorErrorCode code, string wire) in expected)
        {
            Assert.Equal(wire, code.ToWireName());
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => ((CalculatorErrorCode)999).ToWireName());
    }

    [Fact]
    public void DefaultLimitsMatchFrozenContract()
    {
        CalculatorLimits defaults = CalculatorLimits.Default;
        Assert.Equal(512, defaults.MaxExpressionChars);
        Assert.Equal(256, defaults.MaxTokens);
        Assert.Equal(16, defaults.MaxNestingDepth);
        Assert.Equal(64, defaults.MaxIntegerExponent);
        Assert.Equal(8, defaults.MaxFunctionArguments);
        Assert.Equal(32, defaults.MaxStatisticalFunctionArguments);
        Assert.Equal(64, defaults.MaxVariadicFunctionArguments);
        Assert.Equal(512, defaults.MaxArrayLiteralElements);
        Assert.Equal(10_000, defaults.MaxExecutionOps);
        Assert.Equal(10, ExpressionCalculator.MaxRoundDigits);
        Assert.Equal(64, ExpressionCalculator.MaxIdentifierLength);
        defaults.Validate();
    }

    [Fact]
    public void IsValidIdentifierEnforcesAsciiGrammarAndLengthBounds()
    {
        Assert.True(ExpressionCalculator.IsValidIdentifier("a"));
        Assert.True(ExpressionCalculator.IsValidIdentifier("_b0"));
        Assert.True(ExpressionCalculator.IsValidIdentifier(new string('a', ExpressionCalculator.MaxIdentifierLength)));
        Assert.False(ExpressionCalculator.IsValidIdentifier(new string('a', ExpressionCalculator.MaxIdentifierLength + 1)));
        Assert.False(ExpressionCalculator.IsValidIdentifier(null));
        Assert.False(ExpressionCalculator.IsValidIdentifier(string.Empty));
        Assert.False(ExpressionCalculator.IsValidIdentifier("1abc"));
        Assert.False(ExpressionCalculator.IsValidIdentifier("a-b"));
        Assert.False(ExpressionCalculator.IsValidIdentifier("\u4FA1\u683C"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(CalculatorLimits.MaxAllowedExpressionChars + 1)]
    public void ValidateRejectsOutOfRangeMaxExpressionChars(int value)
    {
        var limits = new CalculatorLimits { MaxExpressionChars = value };
        Assert.Throws<ArgumentOutOfRangeException>(() => limits.Validate());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(CalculatorLimits.MaxAllowedTokens + 1)]
    public void ValidateRejectsOutOfRangeMaxTokens(int value)
    {
        var limits = new CalculatorLimits { MaxTokens = value };
        Assert.Throws<ArgumentOutOfRangeException>(() => limits.Validate());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(CalculatorLimits.MaxAllowedNestingDepth + 1)]
    public void ValidateRejectsOutOfRangeMaxNestingDepth(int value)
    {
        var limits = new CalculatorLimits { MaxNestingDepth = value };
        Assert.Throws<ArgumentOutOfRangeException>(() => limits.Validate());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(CalculatorLimits.MaxAllowedIntegerExponent + 1)]
    public void ValidateRejectsOutOfRangeMaxIntegerExponent(int value)
    {
        var limits = new CalculatorLimits { MaxIntegerExponent = value };
        Assert.Throws<ArgumentOutOfRangeException>(() => limits.Validate());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(CalculatorLimits.MaxAllowedFunctionArguments + 1)]
    public void ValidateRejectsOutOfRangeMaxFunctionArguments(int value)
    {
        var limits = new CalculatorLimits { MaxFunctionArguments = value };
        Assert.Throws<ArgumentOutOfRangeException>(() => limits.Validate());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(CalculatorLimits.MaxAllowedFunctionArguments + 1)]
    public void ValidateRejectsOutOfRangeMaxStatisticalFunctionArguments(int value)
    {
        var limits = new CalculatorLimits { MaxStatisticalFunctionArguments = value };
        Assert.Throws<ArgumentOutOfRangeException>(() => limits.Validate());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(CalculatorLimits.MaxAllowedExecutionOps + 1)]
    public void ValidateRejectsOutOfRangeMaxExecutionOps(int value)
    {
        var limits = new CalculatorLimits { MaxExecutionOps = value };
        Assert.Throws<ArgumentOutOfRangeException>(() => limits.Validate());
    }

    [Fact]
    public void EvaluateThrowsArgumentNullExceptionForNullInputs()
    {
        Assert.Throws<ArgumentNullException>(() => ExpressionCalculator.Evaluate(null!, Empty, CalculatorLimits.Default));
        Assert.Throws<ArgumentNullException>(() => ExpressionCalculator.Evaluate("1", null!, CalculatorLimits.Default));
        Assert.Throws<ArgumentNullException>(() => ExpressionCalculator.Evaluate("1", Empty, null!));
    }
}
