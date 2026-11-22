#nullable enable
using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Calculator;
using Xunit;

namespace StockAnalyzer.Core.Tests.Calculator;

public sealed class CalculatorDslV6InverseAndRegressionTests
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
    public void DegreeInverseTrig_HasExactRationalBranchesAndAccurateValues()
    {
        Assert.Equal(0m, EvalScalar("asind(0)"));
        Assert.Equal(30m, EvalScalar("asind(0.5)"));
        Assert.Equal(-30m, EvalScalar("asind(-0.5)"));
        Assert.Equal(90m, EvalScalar("asind(1)"));
        Assert.Equal(-90m, EvalScalar("asind(-1)"));
        Assert.Equal(CalculatorErrorCode.DomainError, EvalError("asind(1.01)"));

        Assert.Equal(0m, EvalScalar("acosd(1)"));
        Assert.Equal(60m, EvalScalar("acosd(0.5)"));
        Assert.Equal(90m, EvalScalar("acosd(0)"));
        Assert.Equal(120m, EvalScalar("acosd(-0.5)"));
        Assert.Equal(180m, EvalScalar("acosd(-1)"));
        Assert.Equal(CalculatorErrorCode.DomainError, EvalError("acosd(-1.01)"));

        Assert.Equal(0m, EvalScalar("atand(0)"));
        Assert.Equal(45m, EvalScalar("atand(1)"));
        Assert.Equal(-45m, EvalScalar("atand(-1)"));
        Assert.InRange(EvalScalar("atand(sqrt(3))"), 59.999999m, 60.000001m);

        Assert.Equal(0m, EvalScalar("atan2d(0, 5)"));
        Assert.Equal(180m, EvalScalar("atan2d(0, -5)"));
        Assert.Equal(90m, EvalScalar("atan2d(5, 0)"));
        Assert.Equal(-90m, EvalScalar("atan2d(-5, 0)"));
        Assert.Equal(45m, EvalScalar("atan2d(3, 3)"));
        Assert.Equal(-45m, EvalScalar("atan2d(-3, 3)"));
        Assert.Equal(135m, EvalScalar("atan2d(3, -3)"));
        Assert.Equal(-135m, EvalScalar("atan2d(-3, -3)"));
        Assert.Equal(CalculatorErrorCode.DomainError, EvalError("atan2d(0, 0)"));
    }

    [Fact]
    public void InvFunc_InvertsMonotonicFunctionsAndSwapsReversedBrackets()
    {
        decimal root1 = EvalScalar("inv_func(x^2, x, 9, 0, 5)");
        Assert.InRange(root1, 2.999999999m, 3.000000001m);

        // Reversed bracket [5, 0] swaps automatically inside RootBrent (1-1=A).
        decimal root2 = EvalScalar("inv_func(x^3 + x, x, 10, 5, 0)");
        Assert.InRange(root2, 1.999999999m, 2.000000001m);

        decimal root3 = EvalScalar("inv_func(exp(x), x, 5, 1, 2, 1e-12)");
        Assert.InRange(root3, 1.609437912m, 1.609437913m);

        var captured = EvalBatchOne(
            "inv_func(sum(arr) * x, x, 12, 0, 5)",
            new Dictionary<string, IReadOnlyList<decimal>> { ["arr"] = [1m, 2m] });
        Assert.True(captured.IsOk);
        Assert.InRange(captured.ScalarValue, 3.999999999m, 4.000000001m);

        Assert.Equal(CalculatorErrorCode.DomainError, EvalError("inv_func(x^2, x, 4, 2, 2)"));
        Assert.Equal(CalculatorErrorCode.DomainError, EvalError("inv_func(x^2, x, 10, 0, 2)"));
        Assert.Equal(CalculatorErrorCode.DomainError, EvalError("inv_func(x, x, 1, 0, 2, 1e-13)"));
        Assert.Equal(CalculatorErrorCode.DomainError, EvalError("inv_func(ln(x), x, 0, -1, 2)"));
    }

    [Fact]
    public void WlsFitPredictAndStats_MatchOlsParityAndWeightScalingContracts()
    {
        var arrays = new Dictionary<string, IReadOnlyList<decimal>>
        {
            ["X"] = [1m, 0m, 2m, 1m, 3m, 0m, 4m, 2m, 5m, 1m, 6m, 3m],
            ["y"] = [4m, 7m, 8m, 12m, 13m, 17m],
            ["w1"] = [1m, 1m, 1m, 1m, 1m, 1m],
            ["wFreq"] = [1m, 2m, 1m, 3m, 1m, 1m],
            ["wFreqScaled"] = [100m, 200m, 100m, 300m, 100m, 100m],
            // Duplicate rows matching wFreq = [1, 2, 1, 3, 1, 1]
            ["XDup"] = [1m, 0m, 2m, 1m, 2m, 1m, 3m, 0m, 4m, 2m, 4m, 2m, 4m, 2m, 5m, 1m, 6m, 3m],
            ["yDup"] = [4m, 7m, 7m, 8m, 12m, 12m, 12m, 13m, 17m],
            ["yNoisy"] = [4.1m, 6.9m, 8.2m, 11.8m, 13.1m, 16.9m],
        };

        IReadOnlyList<ExpressionCalculator.CalculatorBatchResult> res = ExpressionCalculator.EvaluateBatch(
            Empty,
            arrays,
            new ExpressionCalculator.CalculatorBatchItem[]
            {
                new("olsB", "ols_fit(X, y)"),
                new("wlsB1", "wls_fit(X, y, w1)"),
                new("predWls", "wls_predict([7, 2], wlsB1)"),
                new("olsDup", "ols_fit(XDup, yDup)"),
                new("wlsFreq", "wls_fit(X, y, wFreq)"),
                new("wlsScaled", "wls_fit(X, y, wFreqScaled)"),
                new("stats1", "wls_stats(X, y, wlsFreq, wFreq)"),
                new("statsScaled", "wls_stats(X, y, wlsFreq, wFreqScaled)"),
                new("statsUnw", "wls_stats(X, y, olsB)"),
                new("olsStatsOrig", "ols_stats(X, yNoisy)"),
            },
            CalculatorLimits.Default);

        Assert.All(res, r => Assert.True(r.IsOk, $"{r.Name} failed: {r.Error?.Message}"));

        // Uniform weights match ols_fit and wls_predict matches ols_predict.
        for (int j = 0; j < 3; j++)
        {
            Assert.InRange(Math.Abs(res[0].ArrayValues![j] - res[1].ArrayValues![j]), 0m, 1e-12m);
        }

        Assert.InRange(res[2].ScalarValue, 17.999999m, 18.000001m);

        // Frequency weights match duplicated rows.
        for (int j = 0; j < 3; j++)
        {
            Assert.InRange(Math.Abs(res[3].ArrayValues![j] - res[4].ArrayValues![j]), 0m, 1e-12m);
            Assert.InRange(Math.Abs(res[4].ArrayValues![j] - res[5].ArrayValues![j]), 0m, 1e-12m);
        }

        // Scale invariance of r2, adj_r2, rmse, se_reg; sse scales by 100.
        for (int k = 0; k < 4; k++)
        {
            Assert.InRange(Math.Abs(res[6].ArrayValues![k] - res[7].ArrayValues![k]), 0m, 1e-12m);
        }

        Assert.InRange(Math.Abs((res[6].ArrayValues![4] * 100m) - res[7].ArrayValues![4]), 0m, 1e-10m);

        // Unweighted exact fit gives r2 = 1, adj_r2 = 1, rmse = 0, se_reg = 0, sse = 0.
        Assert.InRange(res[8].ArrayValues![0], 0.999999m, 1m);
        Assert.InRange(res[8].ArrayValues![2], 0m, 1e-12m);

        // Existing ols_stats(X, y) still raises DivisionByZero on exact fit.
        var olsExact = EvalBatchOne("ols_stats(X, y)", arrays);
        Assert.False(olsExact.IsOk);
        Assert.Equal(CalculatorErrorCode.DivisionByZero, olsExact.Error!.Code);

        // Arbitrary beta can produce negative r2.
        var badBeta = EvalBatchOne("wls_stats(X, y, [100, -5, -5])", arrays);
        Assert.True(badBeta.IsOk);
        Assert.True(badBeta.ArrayValues![0] < 0m);

        // Too few active rows with zero weights -> DomainError.
        var tooFewActive = EvalBatchOne("wls_fit(X, y, [1, 1, 1, 0, 0, 0])", arrays);
        Assert.False(tooFewActive.IsOk);
        Assert.Equal(CalculatorErrorCode.DomainError, tooFewActive.Error!.Code);
    }

    [Fact]
    public void NonlinearTransformedRegressions_RecoverSyntheticModelsAndValidateActiveRows()
    {
        // y = 2 * exp(0.5 * x) on x = 0..4, plus an inactive row with y = -5 and w = 0
        var expData = new Dictionary<string, IReadOnlyList<decimal>>
        {
            ["x"] = [0m, 1m, 2m, 3m, 4m, 5m],
            ["y"] = [
                2m,
                (decimal)(2.0 * Math.Exp(0.5)),
                (decimal)(2.0 * Math.Exp(1.0)),
                (decimal)(2.0 * Math.Exp(1.5)),
                (decimal)(2.0 * Math.Exp(2.0)),
                -5m,
            ],
            ["w"] = [1m, 1m, 1m, 1m, 1m, 0m],
        };

        var expFit = EvalBatchOne("reg_exp_fit(x, y, w)", expData);
        Assert.True(expFit.IsOk);
        Assert.InRange(expFit.ArrayValues![0], 1.999999m, 2.000001m);
        Assert.InRange(expFit.ArrayValues![1], 0.499999m, 0.500001m);
        Assert.InRange(expFit.ArrayValues![2], 0.999999m, 1m);

        // Without weights, y = -5 is active -> DomainError.
        var expErr = EvalBatchOne("reg_exp_fit(x, y)", expData);
        Assert.False(expErr.IsOk);
        Assert.Equal(CalculatorErrorCode.DomainError, expErr.Error!.Code);

        // y = 3 + 2 * ln(x)
        var logData = new Dictionary<string, IReadOnlyList<decimal>>
        {
            ["x"] = [1m, 2m, 4m, 8m],
            ["y"] = [
                3m,
                (decimal)(3.0 + 2.0 * Math.Log(2.0)),
                (decimal)(3.0 + 2.0 * Math.Log(4.0)),
                (decimal)(3.0 + 2.0 * Math.Log(8.0)),
            ],
        };
        var logFit = EvalBatchOne("reg_log_fit(x, y)", logData);
        Assert.True(logFit.IsOk);
        Assert.InRange(logFit.ArrayValues![0], 2.999999m, 3.000001m);
        Assert.InRange(logFit.ArrayValues![1], 1.999999m, 2.000001m);
        Assert.InRange(logFit.ArrayValues![2], 0.999999m, 1m);

        // y = 4 * x^1.5
        var powData = new Dictionary<string, IReadOnlyList<decimal>>
        {
            ["x"] = [1m, 2m, 4m, 9m],
            ["y"] = [4m, (decimal)(4.0 * Math.Pow(2.0, 1.5)), 32m, 108m],
        };
        var powFit = EvalBatchOne("reg_pow_fit(x, y)", powData);
        Assert.True(powFit.IsOk);
        Assert.InRange(powFit.ArrayValues![0], 3.999999m, 4.000001m);
        Assert.InRange(powFit.ArrayValues![1], 1.499999m, 1.500001m);
        Assert.InRange(powFit.ArrayValues![2], 0.999999m, 1m);
    }

    [Fact]
    public void LogisticRegression_ConvergesToMleAndDetectsSeparationOrDomainErrors()
    {
        var arrays = new Dictionary<string, IReadOnlyList<decimal>>
        {
            ["x"] = [-2m, -1m, -0.5m, 0.5m, 1m, 2m],
            ["xScaled"] = [-20000m, -10000m, -5000m, 5000m, 10000m, 20000m],
            ["y"] = [0m, 0m, 1m, 0m, 1m, 1m],
            ["ySep"] = [0m, 0m, 1m, 1m],
            ["xSep"] = [1m, 2m, 3m, 4m],
        };

        var fit = EvalBatchOne("reg_logistic_fit(x, y)", arrays);
        Assert.True(fit.IsOk);
        double b0 = (double)fit.ArrayValues![0];
        double b1 = (double)fit.ArrayValues![1];
        double ll = (double)fit.ArrayValues![2];
        Assert.InRange(b0, -1e-6, 1e-6); // Symmetric dataset around 0 -> b0 == 0
        Assert.True(b1 > 0.5 && b1 < 5.0);
        Assert.True(ll < 0.0);

        // Verify score equations sum(y - p) == 0 and sum((y - p) * x) == 0 at MLE.
        double grad0 = 0.0;
        double grad1 = 0.0;
        for (int i = 0; i < arrays["x"].Count; i++)
        {
            double xi = (double)arrays["x"][i];
            double yi = (double)arrays["y"][i];
            double pi = 1.0 / (1.0 + Math.Exp(-(b0 + b1 * xi)));
            grad0 += yi - pi;
            grad1 += (yi - pi) * xi;
        }

        Assert.InRange(Math.Abs(grad0), 0.0, 1e-6);
        Assert.InRange(Math.Abs(grad1), 0.0, 1e-6);

        // Large x scale invariance: b1 scales by 1e-4 and log_likelihood is identical.
        var fitScaled = EvalBatchOne("reg_logistic_fit(xScaled, y)", arrays);
        Assert.True(fitScaled.IsOk);
        Assert.InRange(Math.Abs((double)fitScaled.ArrayValues![1] * 10000.0 - b1), 0.0, 1e-6);
        Assert.InRange(Math.Abs((double)fitScaled.ArrayValues![2] - ll), 0.0, 1e-6);

        // Complete separation -> ConvergenceFailure.
        var sep = EvalBatchOne("reg_logistic_fit(xSep, ySep)", arrays);
        Assert.False(sep.IsOk);
        Assert.Equal(CalculatorErrorCode.ConvergenceFailure, sep.Error!.Code);

        // Domain errors.
        var badY = EvalBatchOne("reg_logistic_fit([1, 2, 3], [0, 1.2, 1])");
        Assert.False(badY.IsOk);
        Assert.Equal(CalculatorErrorCode.DomainError, badY.Error!.Code);
    }

    [Fact]
    public void WlsInactiveRowsNonlinearNegativeR2LogisticLseAndBudget_MatchContracts()
    {
        // Inactive row (u <= 1e-15) with extreme y=1e20 must pollute neither fit nor stats (mean, variance, raw sse)
        var tinyArrays = new Dictionary<string, IReadOnlyList<decimal>>
        {
            ["xActive"] = [1m, 2m, 3m, 4m, 5m],
            ["yActive"] = [3.1m, 4.9m, 7.2m, 8.8m, 11.1m],
            ["wActive"] = [2m, 2m, 2m, 2m, 2m],
            ["xWithDead"] = [1m, 2m, 3m, 4m, 5m, 999m],
            ["yWithDead"] = [3.1m, 4.9m, 7.2m, 8.8m, 11.1m, 100000000000000000000m],
            ["wWithDead"] = [2m, 2m, 2m, 2m, 2m, 0.0000000000000001m],
        };

        var fitClean = EvalBatchOne("wls_fit(xActive, yActive, wActive)", tinyArrays);
        var fitDead = EvalBatchOne("wls_fit(xWithDead, yWithDead, wWithDead)", tinyArrays);
        Assert.True(fitClean.IsOk && fitDead.IsOk);
        Assert.Equal(fitClean.ArrayValues![0], fitDead.ArrayValues![0]);
        Assert.Equal(fitClean.ArrayValues![1], fitDead.ArrayValues![1]);

        var statsClean = EvalBatchOne("wls_stats(xActive, yActive, wls_fit(xActive, yActive, wActive), wActive)", tinyArrays);
        var statsDead = EvalBatchOne("wls_stats(xWithDead, yWithDead, wls_fit(xWithDead, yWithDead, wWithDead), wWithDead)", tinyArrays);
        Assert.True(statsClean.IsOk && statsDead.IsOk);
        for (int k = 0; k < 5; k++)
        {
            Assert.InRange(Math.Abs(statsClean.ArrayValues![k] - statsDead.ArrayValues![k]), 0m, 1e-12m);
        }

        // Logistic log-sum-exp near saturation: verify exact MLE log-likelihood identity matches un-clamped logit sum
        var satData = new Dictionary<string, IReadOnlyList<decimal>>
        {
            ["x"] = [-5m, -3m, -1m, -0.1m, 0.1m, 1m, 3m, 5m],
            ["y"] = [0m, 0m, 0m, 1m, 0m, 1m, 1m, 1m],
        };
        var satFit = EvalBatchOne("reg_logistic_fit(x, y)", satData);
        Assert.True(satFit.IsOk);
        double b0 = (double)satFit.ArrayValues![0];
        double b1 = (double)satFit.ArrayValues![1];
        double expectedLl = 0.0;
        for (int i = 0; i < 8; i++)
        {
            double xi = (double)satData["x"][i];
            double yi = (double)satData["y"][i];
            double eta = b0 + (b1 * xi);
            expectedLl += (yi * eta) - (Math.Max(eta, 0.0) + Math.Log(1.0 + Math.Exp(-Math.Abs(eta))));
        }

        Assert.InRange(Math.Abs((double)satFit.ArrayValues![2] - expectedLl), 0.0, 1e-10);

        // inv_func budget exhaustion propagates BudgetExceeded
        CalculatorLimits tight = new() { MaxExecutionOps = 3 };
        CalculatorOutcome budgetFail = ExpressionCalculator.Evaluate("inv_func(x^3 + x, x, 10, 0, 5, 1e-12)", Empty, tight);
        Assert.NotNull(budgetFail.Error);
        Assert.Equal(CalculatorErrorCode.BudgetExceeded, budgetFail.Error!.Code);
    }
}
