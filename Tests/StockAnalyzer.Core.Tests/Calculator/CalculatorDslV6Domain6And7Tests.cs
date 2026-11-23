#nullable enable
using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Calculator;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models.Indicators;
using Xunit;

namespace StockAnalyzer.Core.Tests.Calculator;

public sealed class CalculatorDslV6Domain6And7Tests
{
    private static readonly IReadOnlyDictionary<string, decimal> EmptyScalars = new Dictionary<string, decimal>(StringComparer.Ordinal);

    private static ExpressionCalculator.CalculatorBatchResult EvalBatchOne(
        string expr,
        IReadOnlyDictionary<string, IReadOnlyList<decimal>>? arrays = null)
    {
        IReadOnlyList<ExpressionCalculator.CalculatorBatchResult> results = ExpressionCalculator.EvaluateBatch(
            EmptyScalars,
            arrays,
            new[] { new ExpressionCalculator.CalculatorBatchItem("r", expr) },
            CalculatorLimits.Default);
        return results[0];
    }

    // ---- Registry Check ----

    [Fact]
    public void FunctionRegistry_ContainsExpectedV6Functions()
    {
        // Exact total registry count (760 in v8) is asserted centrally in CalculatorContractTests.
        Assert.Contains(ExpressionCalculator.Functions, f => f.Name == "rand_unif");
        Assert.Contains(ExpressionCalculator.Functions, f => f.Name == "bond_price");
    }

    // ---- Domain 6a: Deterministic PRNG ----

    [Fact]
    public void RandUnifAndNorm_AreDeterministicAndReject3Args()
    {
        var u1 = EvalBatchOne("rand_unif(5, 42, -2, 3)");
        var u2 = EvalBatchOne("rand_unif(5, 42, -2, 3)");
        Assert.True(u1.IsOk);
        Assert.Equal(u1.ArrayValues, u2.ArrayValues);
        Assert.Equal(5, u1.ArrayValues!.Count);
        Assert.All(u1.ArrayValues, v => Assert.InRange(v, -2m, 3m));

        var emptyU = EvalBatchOne("rand_unif(0, 1)");
        Assert.True(emptyU.IsOk);
        Assert.Empty(emptyU.ArrayValues!);

        var badArityU = EvalBatchOne("rand_unif(5, 42, 1)");
        Assert.False(badArityU.IsOk);
        Assert.Equal(CalculatorErrorCode.WrongArgumentCount, badArityU.Error!.Code);

        var badArityN = EvalBatchOne("rand_norm(5, 42, 1)");
        Assert.False(badArityN.IsOk);
        Assert.Equal(CalculatorErrorCode.WrongArgumentCount, badArityN.Error!.Code);

        var badSigma = EvalBatchOne("rand_norm(4, 1, 0, 0)");
        Assert.False(badSigma.IsOk);
        Assert.Equal(CalculatorErrorCode.DomainError, badSigma.Error!.Code);

        var badSeed = EvalBatchOne("rand_unif(4, -1)");
        Assert.False(badSeed.IsOk);
        Assert.Equal(CalculatorErrorCode.DomainError, badSeed.Error!.Code);
    }

    [Fact]
    public void RandIntAndArrShuffle_RespectBoundsAndExactElements()
    {
        var ri = EvalBatchOne("rand_int(20, 10, 12, 99)");
        Assert.True(ri.IsOk);
        Assert.Equal(20, ri.ArrayValues!.Count);
        Assert.All(ri.ArrayValues, v =>
        {
            Assert.Equal(v, decimal.Truncate(v));
            Assert.InRange(v, 10m, 12m);
        });

        var sh = EvalBatchOne("arr_shuffle([10, 20, 30, 40, 50], 7)");
        var shSorted = EvalBatchOne("arr_sort(arr_shuffle([10, 20, 30, 40, 50], 7))");
        Assert.True(sh.IsOk);
        Assert.True(shSorted.IsOk);
        Assert.Equal(new[] { 10m, 20m, 30m, 40m, 50m }, shSorted.ArrayValues);

        var emptySh = EvalBatchOne("arr_shuffle([], 7)");
        Assert.True(emptySh.IsOk);
        Assert.Empty(emptySh.ArrayValues!);
    }

    // ---- Domain 6b: Statistical Tests & OLS Diagnostics ----

    [Fact]
    public void Anova1Way_ComputesExactFAndGuardsFlatWithinOrOverall()
    {
        var ok = EvalBatchOne("anova_1way([10, 12, 11, 20, 22, 21], [-5, -5, -5, 1000000, 1000000, 1000000])");
        Assert.True(ok.IsOk);
        Assert.Equal(4, ok.ArrayValues!.Count);
        // Group means 11 and 21 -> SS_B = 3*25 + 3*25 = 150, df1 = 1; SS_W = 2 + 2 = 4, df2 = 4 -> F = 150 / 1 = 150.
        Assert.InRange(ok.ArrayValues[0], 149.9999m, 150.0001m);
        Assert.InRange(ok.ArrayValues[1], 0.0001m, 0.0004m);
        Assert.Equal(1m, ok.ArrayValues[2]);
        Assert.Equal(4m, ok.ArrayValues[3]);

        // Constant within every group -> DIVISION_BY_ZERO
        var flatWithin = EvalBatchOne("anova_1way([10, 10, 20, 20], [1, 1, 2, 2])");
        Assert.False(flatWithin.IsOk);
        Assert.Equal(CalculatorErrorCode.DivisionByZero, flatWithin.Error!.Code);

        // Overall flat -> DIVISION_BY_ZERO
        var flatAll = EvalBatchOne("anova_1way([5, 5, 5, 5], [1, 1, 2, 2])");
        Assert.False(flatAll.IsOk);
        Assert.Equal(CalculatorErrorCode.DivisionByZero, flatAll.Error!.Code);

        // Fractional / out-of-range groupId -> DOMAIN_ERROR
        var badFrac = EvalBatchOne("anova_1way([1, 2, 3, 4], [1.5, 1.5, 2, 2])");
        Assert.False(badFrac.IsOk);
        Assert.Equal(CalculatorErrorCode.DomainError, badFrac.Error!.Code);

        var mismatch = EvalBatchOne("anova_1way([1, 2, 3], [1, 2])");
        Assert.False(mismatch.IsOk);
        Assert.Equal(CalculatorErrorCode.DimensionMismatch, mismatch.Error!.Code);
    }

    [Fact]
    public void Chi2GofAndMannWhitneyU_ComputeExpectedStatistics()
    {
        var chi = EvalBatchOne("chi2_gof([16, 18, 16, 14, 12, 12], [16, 16, 16, 16, 16, 16])");
        Assert.True(chi.IsOk);
        Assert.Equal(3, chi.ArrayValues!.Count);
        // chi2 = (0 + 4 + 0 + 4 + 16 + 16) / 16 = 40 / 16 = 2.5, df = 5
        Assert.InRange(chi.ArrayValues[0], 2.4999m, 2.5001m);
        Assert.InRange(chi.ArrayValues[1], 0.77m, 0.78m);
        Assert.Equal(5m, chi.ArrayValues[2]);

        var mwu = EvalBatchOne("mann_whitney_u([1, 2, 3, 4], [5, 6, 7, 8])");
        Assert.True(mwu.IsOk);
        Assert.Equal(2, mwu.ArrayValues!.Count);
        Assert.Equal(0m, mwu.ArrayValues[0]);
        Assert.InRange(mwu.ArrayValues[1], 1m / 35m - 1e-12m, 1m / 35m + 1e-12m);

        // Exact DP small sample checks: n1+n2=4 -> 2/C(4,2)=1/3, n1+n2=6 -> 2/C(6,3)=0.1, boundary n1+n2=20 -> 2/C(20,10)=2/184756
        var mwu4 = EvalBatchOne("mann_whitney_u([1, 2], [3, 4])");
        Assert.True(mwu4.IsOk);
        Assert.InRange(mwu4.ArrayValues![1], 1m / 3m - 1e-12m, 1m / 3m + 1e-12m);

        var mwu6 = EvalBatchOne("mann_whitney_u([1, 2, 3], [4, 5, 6])");
        Assert.True(mwu6.IsOk);
        Assert.InRange(mwu6.ArrayValues![1], 0.1m - 1e-12m, 0.1m + 1e-12m);

        var mwu20 = EvalBatchOne("mann_whitney_u([1,2,3,4,5,6,7,8,9,10], [11,12,13,14,15,16,17,18,19,20])");
        Assert.True(mwu20.IsOk);
        Assert.InRange(mwu20.ArrayValues![1], 2m / 184756m - 1e-14m, 2m / 184756m + 1e-14m);

        // Normal approximation branch at n1+n2=21 and tie-adjusted small sample
        var mwu21 = EvalBatchOne("mann_whitney_u([1,2,3,4,5,6,7,8,9,10], [11,12,13,14,15,16,17,18,19,20,21])");
        Assert.True(mwu21.IsOk);
        Assert.InRange(mwu21.ArrayValues![1], 0.00001m, 0.0002m);

        var mwuTieBranch = EvalBatchOne("mann_whitney_u([1, 2, 2, 3], [4, 5, 6, 7])");
        Assert.True(mwuTieBranch.IsOk);
        Assert.InRange(mwuTieBranch.ArrayValues![1], 0.015m, 0.04m);

        var mwuTied = EvalBatchOne("mann_whitney_u([5, 5, 5], [5, 5, 5])");
        Assert.False(mwuTied.IsOk);
        Assert.Equal(CalculatorErrorCode.DivisionByZero, mwuTied.Error!.Code);
    }

    [Fact]
    public void OlsStats_MatchesHandCalculatedAndGuardsPerfectFit()
    {
        // y = [1, 3, 2, 5, 4], X = [1, 2, 3, 4, 5] (5 obs, 1 feature)
        var ols = EvalBatchOne("ols_stats([1, 2, 3, 4, 5], [1, 3, 2, 5, 4])");
        Assert.True(ols.IsOk);
        Assert.Equal(5, ols.ArrayValues!.Count);
        // mean(x)=3, mean(y)=3, Sxx=10, Sxy=8, beta1=0.8, beta0=0.6
        // yHat = [1.4, 2.2, 3.0, 3.8, 4.6], res = [-0.4, 0.8, -1.0, 1.2, -0.6], SS_res = 3.6, SS_tot = 10
        // r2 = 0.64, adjR2 = 1 - (3.6/3)/(10/4) = 0.52, F = (6.4/1)/(3.6/3) = 5.333333..., rmse = sqrt(3.6/5) = sqrt(0.72)
        Assert.InRange(ols.ArrayValues[0], 0.63999m, 0.64001m);
        Assert.InRange(ols.ArrayValues[1], 0.51999m, 0.52001m);
        Assert.InRange(ols.ArrayValues[2], 5.3333m, 5.3334m);
        Assert.InRange(ols.ArrayValues[3], 0.104m, 0.105m);
        Assert.InRange(ols.ArrayValues[4], 0.84852m, 0.84854m);

        // Exact perfect fit -> DIVISION_BY_ZERO
        var perfect = EvalBatchOne("ols_stats([1, 2, 3, 4, 5], [3, 5, 7, 9, 11])");
        Assert.False(perfect.IsOk);
        Assert.Equal(CalculatorErrorCode.DivisionByZero, perfect.Error!.Code);

        // Collinear X (2 features identical) -> SINGULAR_MATRIX
        var collinear = EvalBatchOne("ols_stats([1, 2, 2, 4, 3, 6, 4, 8, 5, 10], [1, 3, 2, 5, 4])");
        Assert.False(collinear.IsOk);
        Assert.Equal(CalculatorErrorCode.SingularMatrix, collinear.Error!.Code);

        // Too few observations (n = 2 < p + 2 = 3) -> DOMAIN_ERROR
        var shortSample = EvalBatchOne("ols_stats([1, 2], [1, 3])");
        Assert.False(shortSample.IsOk);
        Assert.Equal(CalculatorErrorCode.DomainError, shortSample.Error!.Code);
    }

    // ---- Domain 6c: GEV Distribution ----

    [Fact]
    public void GevDistribution_GumbelFrechetWeibullRoundTripAndSupportBounds()
    {
        // Gumbel (xi = 0)
        var gPdf = EvalBatchOne("gev_pdf(0, 0, 1, 0)");
        var gCdf = EvalBatchOne("gev_cdf(0, 0, 1, 0)");
        var gIcdf = EvalBatchOne("gev_icdf(gev_cdf(1.25, 2, 1.5, 0), 2, 1.5, 0)");
        Assert.True(gPdf.IsOk);
        Assert.True(gCdf.IsOk);
        Assert.True(gIcdf.IsOk);
        Assert.InRange(gPdf.ScalarValue, 0.3678m, 0.3679m); // exp(-1)
        Assert.InRange(gCdf.ScalarValue, 0.3678m, 0.3679m);
        Assert.InRange(gIcdf.ScalarValue, 1.249999m, 1.250001m);

        // Frechet (xi > 0) & Weibull (xi < 0) round-trip + outside support
        var fIcdf = EvalBatchOne("gev_icdf(gev_cdf(3.5, 1, 2, 0.25), 1, 2, 0.25)");
        Assert.True(fIcdf.IsOk);
        Assert.InRange(fIcdf.ScalarValue, 3.499999m, 3.500001m);

        var wOutPdf = EvalBatchOne("gev_pdf(6, 0, 1, -0.5)"); // 1 + (-0.5)*6 = -2 <= 0
        var wOutCdf = EvalBatchOne("gev_cdf(6, 0, 1, -0.5)");
        Assert.True(wOutPdf.IsOk);
        Assert.True(wOutCdf.IsOk);
        Assert.Equal(0m, wOutPdf.ScalarValue);
        Assert.Equal(1m, wOutCdf.ScalarValue);
    }

    // ---- Domain 6d: Core Time-Series Kernels ----

    [Fact]
    public void TimeSeriesKernels_MatchCoreMathAndEnforceFlatSeriesGuard()
    {
        decimal[] series =
        {
            100m, 102m, 101m, 103m, 104m, 102.5m, 105m, 106m,
            104.5m, 107m, 108m, 106.5m, 109m, 110m, 108.5m, 111m,
            112m, 110.5m, 113m, 114m, 112.5m, 115m, 116m, 114.5m,
        };
        var dict = new Dictionary<string, IReadOnlyList<decimal>>(StringComparer.Ordinal) { ["s"] = series };

        var adf = EvalBatchOne("adf_test(s, 1)", dict);
        Assert.True(adf.IsOk);
        Assert.Equal(4, adf.ArrayValues!.Count);

        var kpss = EvalBatchOne("kpss_stat(s)", dict);
        var hRs = EvalBatchOne("hurst_rs(s)", dict);
        var hDfa = EvalBatchOne("hurst_dfa(s)", dict);
        var garch = EvalBatchOne("garch11_fit(s)", dict);
        Assert.True(kpss.IsOk);
        Assert.True(hRs.IsOk);
        Assert.True(hDfa.IsOk);
        Assert.True(garch.IsOk);
        Assert.Equal(5, garch.ArrayValues!.Count);

        // Compare kpss_stat directly against ArimaMath.ComputeKpssStatistic
        double[] win = Array.ConvertAll(series, d => (double)d);
        decimal expectedKpss = IndicatorCalculationHelper.ToDecimalOrNull(ArimaMath.ComputeKpssStatistic(win))!.Value;
        Assert.Equal(expectedKpss, kpss.ScalarValue);

        // Flat series -> DIVISION_BY_ZERO uniformly across all four
        var flat20 = new Dictionary<string, IReadOnlyList<decimal>>(StringComparer.Ordinal)
        {
            ["f"] = new[] { 10m, 10m, 10m, 10m, 10m, 10m, 10m, 10m, 10m, 10m, 10m, 10m, 10m, 10m, 10m, 10m, 10m, 10m, 10m, 10m },
        };
        Assert.Equal(CalculatorErrorCode.DivisionByZero, EvalBatchOne("kpss_stat(f)", flat20).Error!.Code);
        Assert.Equal(CalculatorErrorCode.DivisionByZero, EvalBatchOne("hurst_rs(f)", flat20).Error!.Code);
        Assert.Equal(CalculatorErrorCode.DivisionByZero, EvalBatchOne("hurst_dfa(f)", flat20).Error!.Code);
        Assert.Equal(CalculatorErrorCode.DivisionByZero, EvalBatchOne("garch11_fit(f)", flat20).Error!.Code);

        // Too-short series / invalid lagOrder -> DOMAIN_ERROR
        Assert.Equal(CalculatorErrorCode.DomainError, EvalBatchOne("kpss_stat([1, 2, 3, 4])").Error!.Code);
        Assert.Equal(CalculatorErrorCode.DomainError, EvalBatchOne("hurst_rs([1, 2, 3, 4])").Error!.Code);
        Assert.Equal(CalculatorErrorCode.DomainError, EvalBatchOne("adf_test(s, 11)", dict).Error!.Code);
    }

    // ---- Domain 7: Financial Dates & Irregular Cash Flows ----

    [Fact]
    public void DateSerialDaysBetweenAndYearFrac_EnforceExactConventions()
    {
        Assert.Equal(0m, EvalBatchOne("date_serial(1970, 1, 1)").ScalarValue);
        Assert.Equal(-719162m, EvalBatchOne("date_serial(1, 1, 1)").ScalarValue);
        Assert.Equal(2932896m, EvalBatchOne("date_serial(9999, 12, 31)").ScalarValue);
        Assert.Equal(18321m, EvalBatchOne("date_serial(2020, 2, 29)").ScalarValue);
        Assert.Equal(CalculatorErrorCode.DomainError, EvalBatchOne("date_serial(2021, 2, 29)").Error!.Code);

        Assert.Equal(-31m, EvalBatchOne("days_between(date_serial(2024, 2, 1), date_serial(2024, 1, 1))").ScalarValue);

        // d1 == d2 -> exact 0 across bases 0..4
        for (int b = 0; b <= 4; b++)
        {
            var same = EvalBatchOne($"yearfrac(date_serial(2024, 6, 15), date_serial(2024, 6, 15), {b})");
            Assert.True(same.IsOk);
            Assert.Equal(0m, same.ScalarValue);
        }

        // d2 < d1 -> DOMAIN_ERROR
        Assert.Equal(CalculatorErrorCode.DomainError, EvalBatchOne("yearfrac(100, 99, 3)").Error!.Code);
        Assert.Equal(CalculatorErrorCode.DomainError, EvalBatchOne("yearfrac(100, 99, 4)").Error!.Code);
        // Basis 4 = European 30E/360 (rounds day 31 -> 30 without Feb adjustment: 2020-02-29 to 2020-03-31 -> 31/360)
        var eur30 = EvalBatchOne("yearfrac(date_serial(2020, 2, 29), date_serial(2020, 3, 31), 4)");
        Assert.True(eur30.IsOk, eur30.Error?.Message);
        Assert.InRange(Math.Abs(eur30.ScalarValue - (31m / 360m)), 0m, 1e-12m);
        // Out-of-range basis -> DOMAIN_ERROR
        Assert.Equal(CalculatorErrorCode.DomainError, EvalBatchOne("yearfrac(100, 200, -1)").Error!.Code);
        Assert.Equal(CalculatorErrorCode.DomainError, EvalBatchOne("yearfrac(100, 200, 5)").Error!.Code);

        // US 30/360 NASD Feb end-of-month rule: 2020-02-29 to 2020-03-31 -> d1=30, d2=30 -> 30/360 = 1/12
        var nasd = EvalBatchOne("yearfrac(date_serial(2020, 2, 29), date_serial(2020, 3, 31), 0)");
        Assert.True(nasd.IsOk);
        Assert.InRange(nasd.ScalarValue, 1m / 12m - 1e-12m, 1m / 12m + 1e-12m);

        // ACT/ACT ISDA: full leap year 2020-01-01 to 2021-01-01 = 1
        var isda = EvalBatchOne("yearfrac(date_serial(2020, 1, 1), date_serial(2021, 1, 1), 1)");
        Assert.True(isda.IsOk);
        Assert.Equal(1m, isda.ScalarValue);
    }

    [Fact]
    public void XnpvAndXirr_RoundTripAndRespectScaleInvariance()
    {
        // Annual spacing (0, 365, 730) matches npv/irr
        var npvMatch = EvalBatchOne("xnpv(0.1, [-1000, 600, 660], [0, 365, 730]) - npv(0.1, [-1000, 600, 660])");
        Assert.True(npvMatch.IsOk);
        Assert.InRange(Math.Abs(npvMatch.ScalarValue), 0m, 1e-10m);

        var irr = EvalBatchOne("xirr([-1000, 300, 420, 520], [18262, 18627, 18992, 19357])");
        var checkZero = EvalBatchOne("xnpv(xirr([-1000, 300, 420, 520], [18262, 18627, 18992, 19357]), [-1000, 300, 420, 520], [18262, 18627, 18992, 19357])");
        var scaledIrr = EvalBatchOne("xirr([-1000000, 300000, 420000, 520000], [18262, 18627, 18992, 19357])");
        Assert.True(irr.IsOk);
        Assert.True(checkZero.IsOk);
        Assert.True(scaledIrr.IsOk);
        Assert.InRange(Math.Abs(checkZero.ScalarValue), 0m, 1e-9m);
        Assert.InRange(Math.Abs(irr.ScalarValue - scaledIrr.ScalarValue), 0m, 1e-9m);

        // Short-horizon +2% over 1 day -> annualized r = (1.02)^365 - 1 ~= 1376.41
        var oneDay = EvalBatchOne("xirr([-100, 102], [0, 1])");
        Assert.True(oneDay.IsOk, oneDay.Error?.Message);
        Assert.InRange(oneDay.ScalarValue, 1376.3m, 1376.5m);

        // Multi-root cash flows [-100, 230, -132] at [0, 365, 730] have roots r=0.1 and r=0.2; guess picks nearest bracket in ln(1+r)
        var rootLow = EvalBatchOne("xirr([-100, 230, -132], [0, 365, 730], 0.05)");
        var rootHigh = EvalBatchOne("xirr([-100, 230, -132], [0, 365, 730], 0.25)");
        Assert.True(rootLow.IsOk, rootLow.Error?.Message);
        Assert.True(rootHigh.IsOk, rootHigh.Error?.Message);
        Assert.InRange(rootLow.ScalarValue, 0.099999m, 0.100001m);
        Assert.InRange(rootHigh.ScalarValue, 0.199999m, 0.200001m);

        // Single-sign / equal-dates / guess <= -1 / date before dates[0] -> DOMAIN_ERROR
        Assert.Equal(CalculatorErrorCode.DomainError, EvalBatchOne("xirr([100, 200], [0, 365])").Error!.Code);
        Assert.Equal(CalculatorErrorCode.DomainError, EvalBatchOne("xirr([-100, 200], [100, 100])").Error!.Code);
        Assert.Equal(CalculatorErrorCode.DomainError, EvalBatchOne("xirr([-100, 200], [0, 365], -1)").Error!.Code);
        Assert.Equal(CalculatorErrorCode.DomainError, EvalBatchOne("xnpv(0.1, [-100, 200], [100, 90])").Error!.Code);
    }
}
