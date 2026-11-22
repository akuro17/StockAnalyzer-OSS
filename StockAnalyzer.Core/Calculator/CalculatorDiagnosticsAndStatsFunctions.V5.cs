using System;
using StockAnalyzer.Core.MathUtils;

namespace StockAnalyzer.Core.Calculator;

/// <summary>
/// DSL v5 dispatch of the bivariate statistics and tests (<c>cov_s</c> .. <c>ks_2samp</c>), 1D interpolation (<c>interp_*</c>), quantitative finance
/// and information theory (<c>bs_*</c>, <c>npv</c>, <c>irr</c>, <c>max_drawdown</c>, <c>cvar_emp</c>, <c>shannon_entropy</c>, <c>kl_divergence</c>) and spectral functions
/// (<c>goertzel</c>, <c>dft_mag</c>). Array arguments are converted to <see cref="double"/> into stack buffers (<c>&lt;= 512</c> elements); results are narrowed once.
/// <c>max_drawdown</c> and <c>cvar_emp</c> stay in <see cref="decimal"/>.
/// </summary>
internal static partial class CalculatorDiagnosticsAndStatsFunctions
{
    private static bool TryEvaluateV5Value(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        ref DslExecutionBudget budget,
        ref DslArrayArena arena,
        out DslEvalOutcome outcome)
    {
        switch (name)
        {
            case "cov_s":
            case "cov_p":
            case "corr_pearson":
            case "corr_spearman":
            case "corr_kendall":
                outcome = EvalPairStatistic(name, position, args, ref budget, in arena);
                return true;

            case "ccf":
                outcome = EvalCrossCorrelation(name, position, args, in arena);
                return true;

            case "ttest_1samp":
                outcome = EvalTTestOneSample(name, position, args, ref arena);
                return true;

            case "ttest_2samp":
            case "ks_2samp":
                outcome = EvalTwoSampleTest(name, position, args, ref arena);
                return true;

            case "jb_test":
            case "ljung_box":
                outcome = EvalSeriesTest(name, position, args, ref arena);
                return true;

            case "interp_linear":
            case "interp_cubic_spline":
            case "interp_pchip":
                outcome = EvalInterpolation(name, position, args, in arena);
                return true;

            case "bs_price":
            case "bs_delta":
            case "bs_theta":
            case "bs_gamma":
            case "bs_vega":
            case "bs_rho":
            case "bs_implied_vol":
                outcome = EvalBlackScholes(name, position, args, ref budget);
                return true;

            case "npv":
                outcome = EvalNpv(name, position, args, in arena);
                return true;

            case "irr":
                outcome = EvalIrr(name, position, args, ref budget, in arena);
                return true;

            case "max_drawdown":
                outcome = EvalMaxDrawdown(name, position, args, in arena);
                return true;

            case "cvar_emp":
                outcome = EvalEmpiricalCvar(name, position, args, in arena);
                return true;

            case "shannon_entropy":
            case "kl_divergence":
                outcome = EvalInformation(name, position, args, in arena);
                return true;

            case "goertzel":
                outcome = EvalGoertzel(name, position, args, in arena);
                return true;

            case "dft_mag":
                outcome = EvalDftMagnitude(name, position, args, ref budget, ref arena);
                return true;

            default:
                outcome = default;
                return false;
        }
    }

    private static DslEvalOutcome FailExpected(int position, string name, string expected)
        => DslEvalOutcome.Fail(CalculatorErrorCode.DomainError, position, $"'{name}' expects {expected}.", name);

    private static void ToDouble(scoped ReadOnlySpan<decimal> source, scoped Span<double> destination)
        => CalculatorMatrixFunctions.CopyToDouble(source, destination);

    // ---- Bivariate statistics ----

    private static DslEvalOutcome EvalPairStatistic(
        string name,
        int position,
        scoped ReadOnlySpan<DslValue> args,
        ref DslExecutionBudget budget,
        in DslArrayArena arena)
    {
        if (!args[0].IsArray || !args[1].IsArray)
        {
            return FailExpected(position, name, "(x, y) 1D arrays");
        }

        ReadOnlySpan<decimal> xs = arena.GetSlice(args[0].Array);
        ReadOnlySpan<decimal> ys = arena.GetSlice(args[1].Array);
        Span<double> x = stackalloc double[xs.Length];
        Span<double> y = stackalloc double[ys.Length];
        ToDouble(xs, x);
        ToDouble(ys, y);
        MathResult<double> result = name switch
        {
            "cov_s" => DslBivariateAndTestMath.Covariance(x, y, sample: true),
            "cov_p" => DslBivariateAndTestMath.Covariance(x, y, sample: false),
            "corr_pearson" => DslBivariateAndTestMath.CorrPearson(x, y),
            "corr_spearman" => DslBivariateAndTestMath.CorrSpearman(x, y),
            _ => DslBivariateAndTestMath.CorrKendall(x, y, ref budget),
        };
        return CalculatorMatrixFunctions.NarrowScalarResult(result, position, name);
    }

    private static DslEvalOutcome EvalCrossCorrelation(string name, int position, scoped ReadOnlySpan<DslValue> args, in DslArrayArena arena)
    {
        if (!args[0].IsArray || !args[1].IsArray || args[2].IsArray ||
            !DslMathValidation.TryReadInt32InRange(args[2].Scalar, -DslArrayArena.MaxArrayLength, DslArrayArena.MaxArrayLength, out int lag))
        {
            return FailExpected(position, name, "(x, y, lag) with 1D arrays and an integer lag");
        }

        ReadOnlySpan<decimal> xs = arena.GetSlice(args[0].Array);
        ReadOnlySpan<decimal> ys = arena.GetSlice(args[1].Array);
        Span<double> x = stackalloc double[xs.Length];
        Span<double> y = stackalloc double[ys.Length];
        ToDouble(xs, x);
        ToDouble(ys, y);
        return CalculatorMatrixFunctions.NarrowScalarResult(DslBivariateAndTestMath.CrossCorrelation(x, y, lag), position, name);
    }

    private static DslEvalOutcome EvalTTestOneSample(string name, int position, scoped ReadOnlySpan<DslValue> args, ref DslArrayArena arena)
    {
        if (!args[0].IsArray || args[1].IsArray)
        {
            return FailExpected(position, name, "(x, mu0) with a 1D array and a scalar");
        }

        ReadOnlySpan<decimal> xs = arena.GetSlice(args[0].Array);
        Span<double> x = stackalloc double[xs.Length];
        Span<double> result = stackalloc double[3];
        ToDouble(xs, x);
        MathErrorCode err = DslBivariateAndTestMath.TTest1Samp(x, (double)args[1].Scalar, result);
        return err != MathErrorCode.None
            ? CalculatorMatrixFunctions.FailMath(err, position, name)
            : CalculatorMatrixFunctions.NarrowArrayResult(result, position, name, ref arena);
    }

    /// <summary><c>ttest_2samp(x, y)</c> (<c>[t, p, df]</c>) and <c>ks_2samp(x, y)</c> (<c>[D, p]</c>).</summary>
    private static DslEvalOutcome EvalTwoSampleTest(string name, int position, scoped ReadOnlySpan<DslValue> args, ref DslArrayArena arena)
    {
        if (!args[0].IsArray || !args[1].IsArray)
        {
            return FailExpected(position, name, "(x, y) 1D arrays");
        }

        ReadOnlySpan<decimal> xs = arena.GetSlice(args[0].Array);
        ReadOnlySpan<decimal> ys = arena.GetSlice(args[1].Array);
        Span<double> x = stackalloc double[xs.Length];
        Span<double> y = stackalloc double[ys.Length];
        Span<double> result = stackalloc double[3];
        ToDouble(xs, x);
        ToDouble(ys, y);

        MathErrorCode err;
        int length;
        if (name == "ttest_2samp")
        {
            err = DslBivariateAndTestMath.TTest2SampWelch(x, y, result);
            length = 3;
        }
        else
        {
            err = DslBivariateAndTestMath.Ks2Samp(x, y, out double d, out double p);
            result[0] = d;
            result[1] = p;
            length = 2;
        }

        return err != MathErrorCode.None
            ? CalculatorMatrixFunctions.FailMath(err, position, name)
            : CalculatorMatrixFunctions.NarrowArrayResult(result.Slice(0, length), position, name, ref arena);
    }

    /// <summary><c>jb_test(x)</c> and <c>ljung_box(x, lags)</c>, both returning <c>[statistic, p-value]</c>.</summary>
    private static DslEvalOutcome EvalSeriesTest(string name, int position, scoped ReadOnlySpan<DslValue> args, ref DslArrayArena arena)
    {
        bool isLjungBox = name == "ljung_box";
        int lags = 0;
        if (!args[0].IsArray ||
            (isLjungBox && (args[1].IsArray || !DslMathValidation.TryReadInt32InRange(args[1].Scalar, 1, DslArrayArena.MaxArrayLength, out lags))))
        {
            return FailExpected(position, name, isLjungBox ? "(x, lags) with a 1D array and an integer lag count" : "(x) a 1D array");
        }

        ReadOnlySpan<decimal> xs = arena.GetSlice(args[0].Array);
        Span<double> x = stackalloc double[xs.Length];
        ToDouble(xs, x);
        MathErrorCode err = isLjungBox
            ? DslBivariateAndTestMath.LjungBox(x, lags, out double statistic, out double pValue)
            : DslBivariateAndTestMath.JarqueBera(x, out statistic, out pValue);
        if (err != MathErrorCode.None)
        {
            return CalculatorMatrixFunctions.FailMath(err, position, name);
        }

        Span<double> result = [statistic, pValue];
        return CalculatorMatrixFunctions.NarrowArrayResult(result, position, name, ref arena);
    }

    // ---- Interpolation ----

    private static DslEvalOutcome EvalInterpolation(string name, int position, scoped ReadOnlySpan<DslValue> args, in DslArrayArena arena)
    {
        if (!args[0].IsArray || !args[1].IsArray || args[2].IsArray)
        {
            return FailExpected(position, name, "(xs, ys, x) with two 1D arrays and a scalar");
        }

        ReadOnlySpan<decimal> xsDecimal = arena.GetSlice(args[0].Array);
        ReadOnlySpan<decimal> ysDecimal = arena.GetSlice(args[1].Array);
        Span<double> xs = stackalloc double[xsDecimal.Length];
        Span<double> ys = stackalloc double[ysDecimal.Length];
        ToDouble(xsDecimal, xs);
        ToDouble(ysDecimal, ys);
        double x = (double)args[2].Scalar;
        MathResult<double> result = name switch
        {
            "interp_linear" => DslInterpolationMath.InterpLinear(xs, ys, x),
            "interp_cubic_spline" => DslInterpolationMath.InterpCubicSpline(xs, ys, x),
            _ => DslInterpolationMath.InterpPchip(xs, ys, x),
        };
        return CalculatorMatrixFunctions.NarrowScalarResult(result, position, name);
    }

    // ---- Quantitative finance ----

    private static DslEvalOutcome EvalBlackScholes(string name, int position, scoped ReadOnlySpan<DslValue> args, ref DslExecutionBudget budget)
    {
        bool hasFlag = name is "bs_price" or "bs_delta" or "bs_theta" or "bs_rho" or "bs_implied_vol";
        int flagIndex = 5;
        int required = hasFlag ? 6 : 5;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].IsArray)
            {
                return FailExpected(position, name, "scalar arguments");
            }
        }

        bool isCall = false;
        if (hasFlag)
        {
            if (!DslMathValidation.TryReadInt32InRange(args[flagIndex].Scalar, 0, 1, out int flag))
            {
                return FailExpected(position, name, "isCall to be 0 (put) or 1 (call)");
            }

            isCall = flag == 1;
        }

        double q = args.Length > required ? (double)args[required].Scalar : 0.0;
        double a0 = (double)args[0].Scalar;
        double a1 = (double)args[1].Scalar;
        double a2 = (double)args[2].Scalar;
        double a3 = (double)args[3].Scalar;
        double a4 = (double)args[4].Scalar;
        MathResult<double> result = name switch
        {
            "bs_price" => DslQuantAndInfoMath.BsPrice(a0, a1, a2, a3, a4, isCall, q),
            "bs_delta" => DslQuantAndInfoMath.BsDelta(a0, a1, a2, a3, a4, isCall, q),
            "bs_theta" => DslQuantAndInfoMath.BsTheta(a0, a1, a2, a3, a4, isCall, q),
            "bs_gamma" => DslQuantAndInfoMath.BsGamma(a0, a1, a2, a3, a4, q),
            "bs_vega" => DslQuantAndInfoMath.BsVega(a0, a1, a2, a3, a4, q),
            "bs_rho" => DslQuantAndInfoMath.BsRho(a0, a1, a2, a3, a4, isCall, q),
            _ => DslQuantAndInfoMath.BsImpliedVol(a0, a1, a2, a3, a4, isCall, q, ref budget),
        };
        return CalculatorMatrixFunctions.NarrowScalarResult(result, position, name);
    }

    private static DslEvalOutcome EvalNpv(string name, int position, scoped ReadOnlySpan<DslValue> args, in DslArrayArena arena)
    {
        if (args[0].IsArray || !args[1].IsArray)
        {
            return FailExpected(position, name, "(rate, cf) with a scalar and a 1D array");
        }

        ReadOnlySpan<decimal> source = arena.GetSlice(args[1].Array);
        Span<double> cashFlows = stackalloc double[source.Length];
        ToDouble(source, cashFlows);
        return CalculatorMatrixFunctions.NarrowScalarResult(DslQuantAndInfoMath.Npv((double)args[0].Scalar, cashFlows), position, name);
    }

    private static DslEvalOutcome EvalIrr(string name, int position, scoped ReadOnlySpan<DslValue> args, ref DslExecutionBudget budget, in DslArrayArena arena)
    {
        if (!args[0].IsArray || (args.Length == 2 && args[1].IsArray))
        {
            return FailExpected(position, name, "(cf[, guess]) with a 1D array and an optional scalar");
        }

        ReadOnlySpan<decimal> source = arena.GetSlice(args[0].Array);
        Span<double> cashFlows = stackalloc double[source.Length];
        ToDouble(source, cashFlows);
        double guess = args.Length == 2 ? (double)args[1].Scalar : DslQuantAndInfoMath.IrrDefaultGuess;
        MathResult<double> result = DslQuantAndInfoMath.Irr(cashFlows, guess, ref budget);
        return CalculatorMatrixFunctions.NarrowScalarResult(result, position, name);
    }

    private static DslEvalOutcome EvalMaxDrawdown(string name, int position, scoped ReadOnlySpan<DslValue> args, in DslArrayArena arena)
    {
        if (!args[0].IsArray)
        {
            return FailExpected(position, name, "(equity) a 1D array");
        }

        return CalculatorArrayFunctions.ToEvalOutcome(
            CalculatorElementaryFunctions.FromMathResult(DslQuantAndInfoMath.MaxDrawdown(arena.GetSlice(args[0].Array)), position, name));
    }

    private static DslEvalOutcome EvalEmpiricalCvar(string name, int position, scoped ReadOnlySpan<DslValue> args, in DslArrayArena arena)
    {
        if (!args[0].IsArray || args[1].IsArray)
        {
            return FailExpected(position, name, "(returns, confidence) with a 1D array and a scalar");
        }

        return CalculatorArrayFunctions.ToEvalOutcome(
            CalculatorElementaryFunctions.FromMathResult(
                DslQuantAndInfoMath.EmpiricalCvar(arena.GetSlice(args[0].Array), args[1].Scalar),
                position,
                name));
    }

    // ---- Information theory & spectral ----

    private static DslEvalOutcome EvalInformation(string name, int position, scoped ReadOnlySpan<DslValue> args, in DslArrayArena arena)
    {
        bool isKl = name == "kl_divergence";
        if (!args[0].IsArray || (isKl && !args[1].IsArray))
        {
            return FailExpected(position, name, isKl ? "(p, q) 1D arrays" : "(probs) a 1D array");
        }

        ReadOnlySpan<decimal> pDecimal = arena.GetSlice(args[0].Array);
        Span<double> p = stackalloc double[pDecimal.Length];
        ToDouble(pDecimal, p);
        if (!isKl)
        {
            return CalculatorMatrixFunctions.NarrowScalarResult(DslQuantAndInfoMath.ShannonEntropy(p), position, name);
        }

        ReadOnlySpan<decimal> qDecimal = arena.GetSlice(args[1].Array);
        Span<double> q = stackalloc double[qDecimal.Length];
        ToDouble(qDecimal, q);
        return CalculatorMatrixFunctions.NarrowScalarResult(DslQuantAndInfoMath.KlDivergence(p, q), position, name);
    }

    private static DslEvalOutcome EvalGoertzel(string name, int position, scoped ReadOnlySpan<DslValue> args, in DslArrayArena arena)
    {
        if (!args[0].IsArray || args[1].IsArray ||
            !DslMathValidation.TryReadInt32InRange(args[1].Scalar, GoertzelMath.MinTargetPeriod, DslArrayArena.MaxArrayLength, out int period))
        {
            return FailExpected(position, name, $"(series, period) with a 1D array and an integer period in [{GoertzelMath.MinTargetPeriod}, {DslArrayArena.MaxArrayLength}]");
        }

        ReadOnlySpan<decimal> source = arena.GetSlice(args[0].Array);
        Span<double> series = stackalloc double[source.Length];
        ToDouble(source, series);
        return CalculatorMatrixFunctions.NarrowScalarResult(DslQuantAndInfoMath.Goertzel(series, period), position, name);
    }

    private static DslEvalOutcome EvalDftMagnitude(string name, int position, scoped ReadOnlySpan<DslValue> args, ref DslExecutionBudget budget, ref DslArrayArena arena)
    {
        if (!args[0].IsArray)
        {
            return FailExpected(position, name, "(series) a 1D array");
        }

        ReadOnlySpan<decimal> source = arena.GetSlice(args[0].Array);
        Span<double> series = stackalloc double[source.Length];
        Span<double> amplitudes = stackalloc double[(source.Length / 2) + 1];
        ToDouble(source, series);
        MathErrorCode err = DslQuantAndInfoMath.DftMagnitude(series, amplitudes, ref budget);
        return err != MathErrorCode.None
            ? CalculatorMatrixFunctions.FailMath(err, position, name)
            : CalculatorMatrixFunctions.NarrowArrayResult(amplitudes, position, name, ref arena);
    }
}
