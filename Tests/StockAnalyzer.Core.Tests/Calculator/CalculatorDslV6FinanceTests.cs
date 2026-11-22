#nullable enable
using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Calculator;
using StockAnalyzer.Core.MathUtils;
using Xunit;

namespace StockAnalyzer.Core.Tests.Calculator;

public sealed class CalculatorDslV6FinanceTests
{
    private static readonly IReadOnlyDictionary<string, decimal> Empty = new Dictionary<string, decimal>(StringComparer.Ordinal);

    private static decimal EvalScalar(string expr)
    {
        CalculatorOutcome outcome = ExpressionCalculator.Evaluate(expr, Empty, CalculatorLimits.Default);
        Assert.Null(outcome.Error);
        Assert.NotNull(outcome.Value);
        return outcome.Value!.Value;
    }

    private static CalculatorErrorCode EvalError(string expr)
    {
        CalculatorOutcome outcome = ExpressionCalculator.Evaluate(expr, Empty, CalculatorLimits.Default);
        Assert.NotNull(outcome.Error);
        return outcome.Error!.Code;
    }

    private static ExpressionCalculator.CalculatorBatchResult EvalBatchOne(
        string expr,
        IReadOnlyDictionary<string, IReadOnlyList<decimal>>? arrays = null)
    {
        IReadOnlyList<ExpressionCalculator.CalculatorBatchResult> results = ExpressionCalculator.EvaluateBatch(
            Empty,
            arrays,
            new[] { new ExpressionCalculator.CalculatorBatchItem("r", expr) },
            CalculatorLimits.Default);
        return results[0];
    }

    [Fact]
    public void SavingsAndInterest_ComputeAccurateValuesAndRoundTripRates()
    {
        Assert.Equal(150m, EvalScalar("simple_interest(1000, 0.05, 3)"));
        Assert.Equal(CalculatorErrorCode.DomainError, EvalError("simple_interest(-1, 0.05, 3)"));
        Assert.Equal(CalculatorErrorCode.DomainError, EvalError("simple_interest(100, 0.05, -1)"));

        Assert.InRange(EvalScalar("compound_amount(1000, 0.06, 2, 12)"), 1127.1597m, 1127.1598m);
        Assert.InRange(EvalScalar("compound_interest(1000, 0.06, 2, 12)"), 127.1597m, 127.1598m);
        Assert.Equal(1000m, EvalScalar("compound_amount(1000, 0, 5, 4)"));
        Assert.Equal(0m, EvalScalar("compound_interest(1000, 0, 5, 4)"));

        Assert.InRange(EvalScalar("continuous_compound(1000, 0.05, 2)"), 1105.1709m, 1105.1710m);

        decimal eff = EvalScalar("eff_rate(0.06, 12)");
        Assert.InRange(eff, 0.0616778m, 0.0616779m);
        decimal nom = EvalScalar($"nom_rate({eff}, 12)");
        Assert.InRange(Math.Abs(nom - 0.06m), 0m, 1e-12m);

        Assert.Equal(CalculatorErrorCode.DomainError, EvalError("eff_rate(0.06, 0)"));
        Assert.Equal(CalculatorErrorCode.DomainError, EvalError("eff_rate(0.06, 1.5)"));
        Assert.Equal(CalculatorErrorCode.DomainError, EvalError("nom_rate(-1, 12)"));
    }

    [Fact]
    public void Tvm_RoundTripsFiveWayAndHandlesZeroRateAndTypeOne()
    {
        // Zero-rate edge cases
        Assert.Equal(-1500m, EvalScalar("tvm_fv(0, 5, 100, 1000)"));
        Assert.Equal(1000m, EvalScalar("tvm_pv(0, 5, 100, -1500)"));
        Assert.Equal(-200m, EvalScalar("tvm_pmt(0, 5, 1000, 0)"));
        Assert.Equal(5m, EvalScalar("tvm_nper(0, -200, 1000, 0)"));

        // Non-zero rate, type=0 and type=1, fractional nper
        decimal fv0 = EvalScalar("tvm_fv(0.05, 4.5, -100, 1000, 0)");
        decimal pv0 = EvalScalar($"tvm_pv(0.05, 4.5, -100, {fv0}, 0)");
        Assert.InRange(Math.Abs(pv0 - 1000m), 0m, 1e-10m);

        decimal pmt1 = EvalScalar("tvm_pmt(0.01, 12, 10000, 0, 1)");
        Assert.InRange(pmt1, -879.70m, -879.69m);
        decimal nper1 = EvalScalar($"tvm_nper(0.01, {pmt1}, 10000, 0, 1)");
        Assert.InRange(Math.Abs(nper1 - 12m), 0m, 1e-10m);
        decimal rate1 = EvalScalar($"tvm_rate(12, {pmt1}, 10000, 0, 1)");
        Assert.InRange(Math.Abs(rate1 - 0.01m), 0m, 1e-10m);

        // Negative nper guards
        Assert.Equal(CalculatorErrorCode.DomainError, EvalError("tvm_fv(0.05, -1, 100)"));
        Assert.Equal(CalculatorErrorCode.DomainError, EvalError("tvm_pv(0.05, -1, 100)"));
        Assert.Equal(CalculatorErrorCode.DomainError, EvalError("tvm_pmt(0.05, -1, 100)"));
        Assert.Equal(CalculatorErrorCode.DivisionByZero, EvalError("tvm_pmt(0.05, 0, 100)"));
        Assert.Equal(CalculatorErrorCode.DomainError, EvalError("tvm_fv(0.05, 5, 100, 0, 2)"));
        Assert.Equal(CalculatorErrorCode.DomainError, EvalError("tvm_rate(5, 100, 1000, 0)")); // All same sign
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void LoanAmortization_PreservesPrincipalBalanceAndPmtIdentity(int type)
    {
        const int nper = 6;
        decimal pmt = EvalScalar($"tvm_pmt(0.01, {nper}, 12000, 0, {type})");
        decimal sumPpmt = 0m;
        decimal prevBalance = 12000m;

        for (int per = 1; per <= nper; per++)
        {
            decimal ipmt = EvalScalar($"loan_ipmt(0.01, {per}, {nper}, 12000, 0, {type})");
            decimal ppmt = EvalScalar($"loan_ppmt(0.01, {per}, {nper}, 12000, 0, {type})");
            decimal bal = EvalScalar($"loan_balance(0.01, {per}, {nper}, 12000, 0, {type})");

            if (type == 1 && per == 1)
            {
                Assert.Equal(0m, ipmt);
            }

            Assert.InRange(Math.Abs((ipmt + ppmt) - pmt), 0m, 1e-10m);
            Assert.InRange(Math.Abs((prevBalance + ppmt) - bal), 0m, 1e-9m);
            sumPpmt += ppmt;
            prevBalance = bal;
        }

        Assert.InRange(Math.Abs(sumPpmt + 12000m), 0m, 1e-9m);
        Assert.InRange(Math.Abs(prevBalance), 0m, 1e-9m);
        Assert.Equal(12000m, EvalScalar($"loan_balance(0.01, 0, {nper}, 12000, 0, {type})"));
    }

    [Fact]
    public void LoanEqualPrincipalPmt_ComputesNonNegativeScheduleInDecimal()
    {
        var first = EvalBatchOne("loan_equal_principal_pmt(120000, 0.01, 1, 12)");
        Assert.True(first.IsOk);
        Assert.Equal([11200m, 10000m, 1200m, 110000m], first.ArrayValues!);

        var last = EvalBatchOne("loan_equal_principal_pmt(120000, 0.01, 12, 12)");
        Assert.True(last.IsOk);
        Assert.Equal([10100m, 10000m, 100m, 0m], last.ArrayValues!);

        var bad = EvalBatchOne("loan_equal_principal_pmt(120000, 0.01, 13, 12)");
        Assert.False(bad.IsOk);
        Assert.Equal(CalculatorErrorCode.DomainError, bad.Error!.Code);
    }

    [Fact]
    public void EquitiesAndPerformance_ComputeExactRatiosAndReuseAsymmetricRiskMath()
    {
        Assert.Equal(15m, EvalScalar("per(3000, 200)"));
        Assert.Equal(1.5m, EvalScalar("pbr(3000, 2000)"));
        Assert.Equal(0.12m, EvalScalar("roe(120, 1000)"));
        Assert.Equal(0.06m, EvalScalar("roa(120, 2000)"));
        Assert.Equal(0.025m, EvalScalar("div_yield(50, 2000)"));
        Assert.Equal(2000m, EvalScalar("ddm_gordon(100, 0.08, 0.03)"));
        Assert.Equal(0.092m, EvalScalar("capm_er(0.02, 1.2, 0.08)"));
        Assert.Equal(0.072m, EvalScalar("wacc(600, 400, 0.10, 0.05, 0.4)"));
        Assert.InRange(EvalScalar("cagr(100, 121, 2)"), 0.099999999m, 0.100000001m);

        Assert.Equal(CalculatorErrorCode.DivisionByZero, EvalError("per(100, 0)"));
        Assert.Equal(CalculatorErrorCode.DomainError, EvalError("roa(100, -10)"));
        Assert.Equal(CalculatorErrorCode.DomainError, EvalError("ddm_gordon(100, 0.05, 0.05)"));
        Assert.Equal(CalculatorErrorCode.DomainError, EvalError("wacc(100, 100, 0.1, 0.05, 1.1)"));

        var arrays = new Dictionary<string, IReadOnlyList<decimal>>
        {
            ["bench"] = [0.01m, -0.02m, 0.03m, 0.00m, 0.02m],
            // Exact CAPM relation: asset = 0.001 + 1.5 * (bench - 0.001) -> beta = 1.5, alpha = 0
            ["asset"] = [0.0145m, -0.0305m, 0.0445m, -0.0005m, 0.0295m],
            ["flat"] = [0.01m, 0.01m, 0.01m, 0.01m, 0.01m],
        };

        var sharpe = EvalBatchOne("sharpe_ratio(bench, 0.001, 252)", arrays);
        Assert.True(sharpe.IsOk);
        Assert.True(sharpe.ScalarValue > 0m);

        // Verify sortino_ratio delegates to AsymmetricRiskMath.TryComputeDownsideStatistics
        decimal[] benchArr = [0.01m, -0.02m, 0.03m, 0.00m, 0.02m];
        Assert.True(AsymmetricRiskMath.TryComputeDownsideStatistics(benchArr, 0.001m, out DownsideStatistics ds));
        var sortino1 = EvalBatchOne("sortino_ratio(bench, 0.001, 1)", arrays);
        Assert.True(sortino1.IsOk);
        Assert.Equal(ds.RollingSortino!.Value, sortino1.ScalarValue);

        var beta = EvalBatchOne("beta_ols(asset, bench)", arrays);
        Assert.True(beta.IsOk);
        Assert.InRange(beta.ScalarValue, 1.499999999m, 1.500000001m);

        var alpha = EvalBatchOne("alpha_jensen(asset, bench, 0.001, 252)", arrays);
        Assert.True(alpha.IsOk);
        Assert.InRange(Math.Abs(alpha.ScalarValue), 0m, 1e-12m);

        var treynor = EvalBatchOne("treynor_ratio(asset, bench, 0.001, 1)", arrays);
        Assert.True(treynor.IsOk);
        Assert.InRange(Math.Abs(treynor.ScalarValue - 0.007m), 0m, 1e-12m); // mean(bench) - rf = 0.008 - 0.001

        var ir = EvalBatchOne("info_ratio(asset, bench, 252)", arrays);
        Assert.True(ir.IsOk);
        Assert.True(ir.ScalarValue > 0m);

        var flatBeta = EvalBatchOne("beta_ols(asset, flat)", arrays);
        Assert.False(flatBeta.IsOk);
        Assert.Equal(CalculatorErrorCode.DivisionByZero, flatBeta.Error!.Code);

        var flatSharpe = EvalBatchOne("sharpe_ratio(flat)", arrays);
        Assert.False(flatSharpe.IsOk);
        Assert.Equal(CalculatorErrorCode.DivisionByZero, flatSharpe.Error!.Code);
    }

    [Fact]
    public void Bonds_PriceYtmDurationConvexityAndLongDatedOverflowGuard()
    {
        // Par bond: coupon rate == yield -> price == face
        decimal parPrice = EvalScalar("bond_price(1000, 0.05, 0.05, 10, 2)");
        Assert.InRange(Math.Abs(parPrice - 1000m), 0m, 1e-10m);

        // Zero-yield bond: price = face * (1 + cRate * years)
        decimal zeroYtmPrice = EvalScalar("bond_price(1000, 0.04, 0, 5, 2)");
        Assert.InRange(Math.Abs(zeroYtmPrice - 1200m), 0m, 1e-10m);

        // Discount bond round-trip price <-> ytm
        decimal price = EvalScalar("bond_price(1000, 0.04, 0.06, 5, 2)");
        Assert.InRange(price, 914m, 915m);
        decimal ytm = EvalScalar($"bond_ytm({price}, 1000, 0.04, 5, 2)");
        Assert.InRange(Math.Abs(ytm - 0.06m), 0m, 1e-9m);

        // 100-year monthly bond (n = 1200): verifies safe y_min lower bound never overflows to Infinity
        decimal longPrice = EvalScalar("bond_price(1000, 0.04, 0.05, 100, 12)");
        decimal longYtm = EvalScalar($"bond_ytm({longPrice}, 1000, 0.04, 100, 12)");
        Assert.InRange(Math.Abs(longYtm - 0.05m), 0m, 1e-9m);

        // Current yield & accrued interest (exact decimal)
        Assert.InRange(Math.Abs(EvalScalar("bond_current_yield(1000, 0.06, 1200)") - 0.05m), 0m, 1e-15m);
        Assert.Equal(CalculatorErrorCode.DivisionByZero, EvalError("bond_current_yield(1000, 0.06, 0)"));
        Assert.Equal(5m, EvalScalar("bond_accrued_interest(1000, 0.06, 30, 180, 2)"));
        Assert.Equal(CalculatorErrorCode.DomainError, EvalError("bond_accrued_interest(1000, 0.06, 181, 180, 2)"));

        // Duration, convexity, and DV01 vs finite difference
        decimal mac = EvalScalar("bond_mac_duration(1000, 0.05, 0.05, 5, 2)");
        decimal mod = EvalScalar("bond_mod_duration(1000, 0.05, 0.05, 5, 2)");
        decimal conv = EvalScalar("bond_convexity(1000, 0.05, 0.05, 5, 2)");
        decimal dv01 = EvalScalar("bond_dv01(1000, 0.05, 0.05, 5, 2)");
        Assert.InRange(Math.Abs((mac / 1.025m) - mod), 0m, 1e-10m);
        Assert.True(conv > 0m);

        decimal pDown = EvalScalar("bond_price(1000, 0.05, 0.0499, 5, 2)");
        decimal pUp = EvalScalar("bond_price(1000, 0.05, 0.0501, 5, 2)");
        decimal fdDv01 = (pDown - pUp) / 2m;
        Assert.InRange(Math.Abs(dv01 - fdDv01), 0m, 1e-5m);

        // Domain checks
        Assert.Equal(CalculatorErrorCode.DomainError, EvalError("bond_price(1000, 0.05, 0.05, 5, 3)"));
        Assert.Equal(CalculatorErrorCode.DomainError, EvalError("bond_price(1000, 0.05, 0.05, 100.1, 2)"));
    }

    [Fact]
    public void Fx_CrossForwardSwapMarginAndStopout_MatchFormulas()
    {
        Assert.Equal(150m, EvalScalar("fx_cross_rate(1.25, 120)"));
        Assert.Equal(1.25m, EvalScalar("fx_cross_rate(150, 120, 1)"));
        Assert.Equal(CalculatorErrorCode.DomainError, EvalError("fx_cross_rate(1.25, 120, 2)"));

        decimal fwd = EvalScalar("fx_forward_rate(150, 0.05, 0.01, 90, 360)");
        decimal expectedFwd = 150m * (1.0125m / 1.0025m);
        Assert.InRange(Math.Abs(fwd - expectedFwd), 0m, 1e-10m);

        decimal swapPts = EvalScalar("fx_swap_points(150, 0.05, 0.01, 90, 360, 0.01)");
        Assert.InRange(Math.Abs(swapPts - ((expectedFwd - 150m) / 0.01m)), 0m, 1e-8m);

        Assert.Equal(1000m, EvalScalar("fx_pip_value(0.01, 100000, 1)"));
        Assert.Equal(6000m, EvalScalar("fx_required_margin(100000, 1.5, 25)"));
        Assert.Equal(10m, EvalScalar("fx_effective_leverage(100000, 10000)"));
        Assert.Equal(CalculatorErrorCode.DivisionByZero, EvalError("fx_effective_leverage(1000, 0)"));

        // Long stopout: (10000*150 - 50000) / (10000 * (1 - 0.5*0.04)) = 1450000 / 9800
        decimal stopLong = EvalScalar("fx_stopout_rate(150, 50000, 10000, 1, 0.04, 0.5)");
        Assert.InRange(Math.Abs(stopLong - (1450000m / 9800m)), 0m, 1e-12m);
        decimal stopShort = EvalScalar("fx_stopout_rate(150, 50000, 10000, 0, 0.04, 0.5)");
        Assert.InRange(Math.Abs(stopShort - (1550000m / 10200m)), 0m, 1e-12m);
        Assert.Equal(CalculatorErrorCode.DivisionByZero, EvalError("fx_stopout_rate(150, 50000, 10000, 1, 1, 1)"));
        Assert.Equal(CalculatorErrorCode.DomainError, EvalError("fx_stopout_rate(100, 200000, 1000, 1, 0.04, 0.5)"));
    }

    [Fact]
    public void Portfolio_VarianceVolRiskContributionAndMinVarWeights_HoldIdentities()
    {
        var arrays = new Dictionary<string, IReadOnlyList<decimal>>
        {
            ["w"] = [0.6m, 0.4m],
            ["r"] = [0.10m, 0.05m],
            // Diagonal covariance: var = [0.04, 0.09], cov12 = 0
            ["covDiag"] = [0.04m, 0m, 0m, 0.09m],
            // Correlated covariance: [0.04, 0.01; 0.01, 0.09]
            ["covCorr"] = [0.04m, 0.01m, 0.01m, 0.09m],
            ["covSingular"] = [0.04m, 0.04m, 0.04m, 0.04m],
            ["pRet"] = [0.02m, 0.03m, 0.01m, 0.04m],
            ["bRetSame"] = [0.01m, 0.02m, 0.00m, 0.03m],
            ["bRetDiff"] = [0.01m, 0.01m, 0.02m, 0.02m],
        };

        Assert.Equal(0.08m, EvalBatchOne("port_return(w, r)", arrays).ScalarValue);

        // Variance: 0.36*0.04 + 0.16*0.09 + 2*0.24*0.01 = 0.0144 + 0.0144 + 0.0048 = 0.0336
        var pVar = EvalBatchOne("port_variance(w, covCorr)", arrays);
        Assert.True(pVar.IsOk);
        Assert.InRange(Math.Abs(pVar.ScalarValue - 0.0336m), 0m, 1e-12m);

        var pVol = EvalBatchOne("port_vol(w, covCorr, 1)", arrays);
        Assert.True(pVol.IsOk);
        Assert.InRange(Math.Abs((double)pVol.ScalarValue - Math.Sqrt(0.0336)), 0.0, 1e-12);

        // Euler risk decomposition: sum(port_crc) == port_vol
        var mrc = EvalBatchOne("port_mrc(w, covCorr)", arrays);
        var crc = EvalBatchOne("port_crc(w, covCorr)", arrays);
        Assert.True(mrc.IsOk && mrc.ArrayValues!.Count == 2);
        Assert.True(crc.IsOk && crc.ArrayValues!.Count == 2);
        decimal crcSum = crc.ArrayValues![0] + crc.ArrayValues![1];
        Assert.InRange(Math.Abs(crcSum - pVol.ScalarValue), 0m, 1e-12m);
        Assert.InRange(Math.Abs((0.6m * mrc.ArrayValues![0]) - crc.ArrayValues![0]), 0m, 1e-12m);

        // Tracking error: 0 on constant spread, > 0 on non-flat spread
        var teFlat = EvalBatchOne("tracking_error(pRet, bRetSame, 252)", arrays);
        Assert.True(teFlat.IsOk);
        Assert.Equal(0m, teFlat.ScalarValue);

        var teNonFlat = EvalBatchOne("tracking_error(pRet, bRetDiff, 1)", arrays);
        Assert.True(teNonFlat.IsOk);
        Assert.True(teNonFlat.ScalarValue > 0m);

        // Kelly fraction: 0.6 - 0.4/2 = 0.4
        Assert.Equal(0.4m, EvalScalar("kelly_fraction(0.6, 2)"));

        // Global minimum variance weights for diagonal [0.04, 0.09]: proportional to [25, 11.111...] -> [9/13, 4/13]
        var minVar = EvalBatchOne("port_min_var_weights(covDiag)", arrays);
        Assert.True(minVar.IsOk && minVar.ArrayValues!.Count == 2);
        Assert.InRange(Math.Abs(minVar.ArrayValues![0] - (9m / 13m)), 0m, 1e-12m);
        Assert.InRange(Math.Abs(minVar.ArrayValues![1] - (4m / 13m)), 0m, 1e-12m);

        var singular = EvalBatchOne("port_min_var_weights(covSingular)", arrays);
        Assert.False(singular.IsOk);
        Assert.Equal(CalculatorErrorCode.SingularMatrix, singular.Error!.Code);
    }

    [Fact]
    public void Derivatives_RhoBinomialFuturesParityAndIrs_MatchTheory()
    {
        // bs_rho vs central finite difference of bs_price w.r.t. r
        decimal rhoCall = EvalScalar("bs_rho(100, 100, 1, 0.05, 0.2, 1, 0.01)");
        decimal cPlus = EvalScalar("bs_price(100, 100, 1, 0.0501, 0.2, 1, 0.01)");
        decimal cMinus = EvalScalar("bs_price(100, 100, 1, 0.0499, 0.2, 1, 0.01)");
        Assert.InRange(Math.Abs(rhoCall - ((cPlus - cMinus) / 0.0002m)), 0m, 1e-4m);

        decimal rhoPut = EvalScalar("bs_rho(100, 100, 1, 0.05, 0.2, 0, 0.01)");
        Assert.True(rhoCall > 0m && rhoPut < 0m);

        // Binomial European converges toward Black-Scholes (q = 0)
        decimal bsCall = EvalScalar("bs_price(100, 100, 1, 0.05, 0.2, 1, 0)");
        decimal binEurCall = EvalScalar("binomial_option_price(100, 100, 1, 0.05, 0.2, 1, 0, 200)");
        Assert.InRange(Math.Abs(binEurCall - bsCall), 0m, 0.05m);

        // American put >= European put
        decimal binEurPut = EvalScalar("binomial_option_price(100, 110, 1, 0.05, 0.2, 0, 0, 100)");
        decimal binAmPut = EvalScalar("binomial_option_price(100, 110, 1, 0.05, 0.2, 0, 1, 100)");
        Assert.True(binAmPut >= binEurPut);
        Assert.Equal(CalculatorErrorCode.DomainError, EvalError("binomial_option_price(100, 100, 1, 0.05, 0.2, 1, 0, 257)"));

        // Futures & Put-Call Parity identity
        decimal fut = EvalScalar("futures_fair_value(100, 0.05, 0.02, 2)");
        Assert.InRange(Math.Abs((double)fut - (100.0 * Math.Exp(0.06))), 0.0, 1e-10);

        decimal bsPut = EvalScalar("bs_price(100, 105, 0.5, 0.04, 0.25, 0, 0.01)");
        decimal bsCallDiv = EvalScalar("bs_price(100, 105, 0.5, 0.04, 0.25, 1, 0.01)");
        decimal parityPut = EvalScalar($"put_call_parity_put({bsCallDiv}, 100, 105, 0.5, 0.04, 0.01)");
        decimal parityCall = EvalScalar($"put_call_parity_call({bsPut}, 100, 105, 0.5, 0.04, 0.01)");
        Assert.InRange(Math.Abs(parityPut - bsPut), 0m, 1e-10m);
        Assert.InRange(Math.Abs(parityCall - bsCallDiv), 0m, 1e-10m);

        // IRS par fixed rate
        var arrays = new Dictionary<string, IReadOnlyList<decimal>>
        {
            ["df"] = [0.98m, 0.95m],
            ["alpha"] = [0.5m, 0.5m],
        };
        var irs = EvalBatchOne("irs_fixed_rate(df, alpha)", arrays);
        Assert.True(irs.IsOk);
        decimal expectedIrs = (1m - 0.95m) / ((0.5m * 0.98m) + (0.5m * 0.95m));
        Assert.Equal(expectedIrs, irs.ScalarValue);
    }

    [Fact]
    public void TvmContinuityHybridRateAndLoanFinalRounding_SatisfyHardenedContracts()
    {
        // High-period tiny-rate continuity across TVM suite (including tvm_nper Log1pDouble cancellation-free path)
        decimal fvHugeN = EvalScalar("tvm_fv(1e-12, 1000000, 0, 1000)");
        Assert.InRange(Math.Abs(fvHugeN - (-1000.0010000005m)), 0m, 1e-8m);
        decimal pvHugeN = EvalScalar($"tvm_pv(1e-12, 1000000, 0, {fvHugeN})");
        Assert.InRange(Math.Abs(pvHugeN - 1000m), 0m, 1e-6m);
        decimal nperHugeN = EvalScalar("tvm_nper(1e-12, -100, 100000, 0)");
        Assert.InRange(Math.Abs(nperHugeN - 1000.00000050025m), 0m, 1e-9m);
        Assert.InRange(Math.Abs(EvalScalar($"tvm_fv(1e-12, {nperHugeN}, -100, 100000)")), 0m, 1e-9m);

        // Large-span InfoRatio & TrackingError exercise ArrayPool fallback above MathBufferLimits.StackAllocThreshold (512)
        double[] portLarge = new double[1024];
        double[] benchLarge = new double[1024];
        for (int i = 0; i < 1024; i++)
        {
            portLarge[i] = 0.001 * ((i % 5) - 1);
            benchLarge[i] = 0.0005 * ((i % 3) - 1);
        }

        MathResult<double> irLarge = DslFinanceMath.InfoRatio(portLarge, benchLarge, 252.0);
        MathResult<double> teLarge = DslFinanceMath.TrackingError(portLarge, benchLarge, 252.0);
        Assert.True(irLarge.IsOk && teLarge.IsOk && irLarge.Value > 0.0 && teLarge.Value > 0.0);

        // Tiny rate recovery via smooth Taylor/derivative in TvmResidualAndDerivative
        decimal fvTiny = EvalScalar("tvm_fv(5e-13, 10, 0, 1000)");
        decimal rTiny = EvalScalar($"tvm_rate(10, 0, 1000, {fvTiny})");
        Assert.InRange(Math.Abs(rTiny - 5e-13m), 0m, 1e-15m);

        // High-n positive rate (PV form, full scan range) and negative rate (FV form, no reciprocal overflow)
        decimal rHighPos = EvalScalar("tvm_rate(1000, -50, 1000, 0, 0, 0.05)");
        Assert.InRange(Math.Abs(rHighPos - 0.05m), 0m, 1e-10m);
        decimal rHighNeg = EvalScalar("tvm_rate(1000, -500, 1000, 1000, 0, -0.4)");
        Assert.InRange(Math.Abs(rHighNeg - (-0.5m)), 0m, 1e-10m);
        Assert.Equal(1000m, EvalScalar("tvm_pv(0.5, 2000, -500, -1000)"));
        Assert.Equal(-500m, EvalScalar("tvm_pmt(0.5, 2000, 1000, -1000)"));
        Assert.Equal(1000m, EvalScalar("loan_balance(0.5, 1, 2000, 1000)"));

        // Two-root TVM: extreme guesses step out of bounds in Newton and fall back to the 64-interval log-midpoint selector
        decimal rootNeg = EvalScalar("tvm_rate(10, -301, 1000, 2000, 0, -0.9)");
        decimal rootPos = EvalScalar("tvm_rate(10, -301, 1000, 2000, 0, 9)");
        Assert.True(rootNeg < 0m && rootPos > 0m);

        // Non-divisible equal-principal loan: final period exact accounting identity & sum(prin) == principal
        decimal sumPrin = 0m;
        for (int per = 1; per <= 3; per++)
        {
            var pmtRes = EvalBatchOne($"loan_equal_principal_pmt(100, 0.05, {per}, 3)");
            Assert.True(pmtRes.IsOk);
            IReadOnlyList<decimal> row = pmtRes.ArrayValues!;
            Assert.Equal(row[1] + row[2], row[0]); // total_pmt == prin_pmt + int_pmt
            sumPrin += row[1];
            if (per == 3)
            {
                Assert.Equal(0m, row[3]); // remAfter == 0m
            }
        }

        Assert.Equal(100m, sumPrin);

        // Bond price O(1) zero-yield continuity around 1e-8
        decimal bp0 = EvalScalar("bond_price(1000, 0.04, 0, 50, 2)");
        decimal bp1 = EvalScalar("bond_price(1000, 0.04, 1e-12, 50, 2)");
        decimal bp2 = EvalScalar("bond_price(1000, 0.04, 1e-8, 50, 2)");
        decimal bp3 = EvalScalar("bond_price(1000, 0.04, 2e-8, 50, 2)");
        Assert.Equal(3000m, bp0);
        Assert.True(bp0 > bp1 && bp1 > bp2 && bp2 > bp3);
    }

    [Fact]
    public void Portfolio_ZeroAndTinyIndefiniteCovarianceAndBudgetExhaustion_MatchHardenedPsdContract()
    {
        var arrays = new Dictionary<string, IReadOnlyList<decimal>>
        {
            ["wPos"] = [0.5m, 0.5m],
            ["covZero"] = [0m, 0m, 0m, 0m],
            ["covTinyIndef"] = [0.000001m, 0.000002m, 0.000002m, 0.000001m],
            ["cov3"] = [0.04m, 0.01m, 0m, 0.01m, 0.09m, 0.02m, 0m, 0.02m, 0.16m],
        };

        // Zero covariance matrix is trivially PSD
        var zeroVar = EvalBatchOne("port_variance(wPos, covZero)", arrays);
        Assert.True(zeroVar.IsOk);
        Assert.Equal(0m, zeroVar.ScalarValue);

        Assert.Equal(CalculatorErrorCode.DivisionByZero, EvalBatchOne("port_mrc(wPos, covZero)", arrays).Error!.Code);
        Assert.Equal(CalculatorErrorCode.SingularMatrix, EvalBatchOne("port_min_var_weights(covZero)", arrays).Error!.Code);

        // Cholesky forward/back substitution GMVP satisfies sum(w) == 1 and marginal risk (Sigma * w)_i == const
        var gmvp3 = EvalBatchOne("port_min_var_weights(cov3)", arrays);
        Assert.True(gmvp3.IsOk && gmvp3.ArrayValues!.Count == 3);
        IReadOnlyList<decimal> w3 = gmvp3.ArrayValues!;
        Assert.InRange(Math.Abs((w3[0] + w3[1] + w3[2]) - 1m), 0m, 1e-12m);
        decimal mr0 = (0.04m * w3[0]) + (0.01m * w3[1]);
        decimal mr1 = (0.01m * w3[0]) + (0.09m * w3[1]) + (0.02m * w3[2]);
        decimal mr2 = (0.02m * w3[1]) + (0.16m * w3[2]);
        Assert.InRange(Math.Abs(mr0 - mr1), 0m, 1e-12m);
        Assert.InRange(Math.Abs(mr1 - mr2), 0m, 1e-12m);

        // Tiny indefinite covariance (w^T cov w > 0, min_eval < 0) must still fail globally even when normInf << 1
        Assert.Equal(CalculatorErrorCode.DomainError, EvalBatchOne("port_variance(wPos, covTinyIndef)", arrays).Error!.Code);
        Assert.Equal(CalculatorErrorCode.DomainError, EvalBatchOne("port_vol(wPos, covTinyIndef, 252)", arrays).Error!.Code);
        Assert.Equal(CalculatorErrorCode.DomainError, EvalBatchOne("port_mrc(wPos, covTinyIndef)", arrays).Error!.Code);
        Assert.Equal(CalculatorErrorCode.DomainError, EvalBatchOne("port_crc(wPos, covTinyIndef)", arrays).Error!.Code);

        // Budget exhaustion propagates cleanly from binomial_option_price
        CalculatorLimits tight = new() { MaxExecutionOps = 5 };
        CalculatorOutcome tightOpt = ExpressionCalculator.Evaluate(
            "binomial_option_price(100, 100, 1, 0.05, 0.2, 1, 0, 200)",
            Empty,
            tight);
        Assert.NotNull(tightOpt.Error);
        Assert.Equal(CalculatorErrorCode.BudgetExceeded, tightOpt.Error!.Code);
    }

    [Fact]
    public void LoanAmortization_HighPeriodPositiveRate_AvoidsOverflowAndCancellation()
    {
        // Previously 1.5^1000 catastrophic cancellation produced 0, and 1.5^1999 overflowed to NaN -> DomainError
        Assert.Equal(100000m, EvalScalar("loan_balance(0.5, 1000, 2000, 100000)"));
        Assert.Equal(-50000m, EvalScalar("loan_ipmt(0.5, 1000, 2000, 100000)"));
        Assert.Equal(0m, EvalScalar("loan_ppmt(0.5, 1000, 2000, 100000)"));
        Assert.Equal(0m, EvalScalar("loan_balance(0.5, 2000, 2000, 100000)"));

        // Accounting identities hold across tail periods (per >= 1750 where (1+r)^per overflows binary64)
        foreach (int type in new[] { 0, 1 })
        {
            foreach (decimal fv in new[] { 0m, -40000m })
            {
                decimal pmt = EvalScalar($"tvm_pmt(0.5, 2000, 100000, {fv}, {type})");
                decimal prevBal = EvalScalar($"loan_balance(0.5, 1997, 2000, 100000, {fv}, {type})");
                for (int per = 1998; per <= 2000; per++)
                {
                    decimal ipmt = EvalScalar($"loan_ipmt(0.5, {per}, 2000, 100000, {fv}, {type})");
                    decimal ppmt = EvalScalar($"loan_ppmt(0.5, {per}, 2000, 100000, {fv}, {type})");
                    decimal bal = EvalScalar($"loan_balance(0.5, {per}, 2000, 100000, {fv}, {type})");

                    Assert.InRange(Math.Abs((ipmt + ppmt) - pmt), 0m, 1e-8m);
                    Assert.InRange(Math.Abs((prevBal + ppmt) - bal), 0m, 1e-8m);
                    prevBal = bal;
                }

                decimal expectedFinalBal = type == 1 ? -fv / 1.5m : -fv;
                Assert.InRange(Math.Abs(prevBal - expectedFinalBal), 0m, 1e-8m);
            }
        }

        // Negative rate continues using non-overflowing retrospective FV form
        decimal negPmt = EvalScalar("tvm_pmt(-0.25, 200, 1000, -1000)");
        Assert.InRange(Math.Abs(negPmt - 250m), 0m, 1e-8m);
        decimal negMidBal = EvalScalar("loan_balance(-0.25, 100, 200, 1000, -1000)");
        Assert.InRange(Math.Abs(negMidBal - 1000m), 0m, 1e-8m);
    }
}
