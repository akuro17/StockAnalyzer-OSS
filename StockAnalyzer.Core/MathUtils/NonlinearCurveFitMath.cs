using StockAnalyzer.Core.Calculator;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.MathUtils;

/// <summary>
/// A fitted nonlinear curve of a price window with x = 1 .. n (the one definition the Nonlinear Regression indicator and the NonlinearRegressionCurve drawing tool share).
/// Exponential <c>A exp(B x)</c> and Power <c>A x^B</c> (fitted on the logarithm), Logarithmic <c>A + B ln(x)</c>, Logistic: the window was mapped linearly into [eps, 1 - eps],
/// fitted by a logistic regression (<c>A</c> = intercept b0, <c>B</c> = slope b1 in original-x units) and mapped back: <c>Min + ((1 / (1 + exp(-(A + B x))) - eps) / (1 - 2 eps)) Range</c>.
/// </summary>
public readonly record struct NonlinearCurveFit(NonlinearRegressionModel Model, double A, double B, double Min, double Range)
{
    /// <summary>The curve in price at <paramref name="x"/>.</summary>
    public double ValueAt(double x)
    {
        switch (Model)
        {
            case NonlinearRegressionModel.Exponential:
                return A * Math.Exp(B * x);
            case NonlinearRegressionModel.Power:
                return A * Math.Pow(x, B);
            case NonlinearRegressionModel.Logarithmic:
                return A + (B * Math.Log(x));
            case NonlinearRegressionModel.Logistic:
            {
                const double epsilon = IndicatorDefaultConstants.NonlinearLogisticEpsilon;
                const double span = 1.0 - (2.0 * epsilon);
                double probability = 1.0 / (1.0 + Math.Exp(-(A + (B * x))));
                return Min + (((probability - epsilon) / span) * Range);
            }

            default:
                return double.NaN;
        }
    }
}

/// <summary>Fits <see cref="NonlinearCurveFit"/> curves with the DSL regression kernels (<see cref="DslRegressionMath"/>, unchanged).</summary>
public static class NonlinearCurveFitMath
{
    private const int FitResultLength = 3;

    /// <summary>
    /// Fits <paramref name="model"/> to <paramref name="y"/> (x = <paramref name="x"/>, normally 1 .. n). <paramref name="mapped"/> is scratch of the length of <paramref name="y"/> (used by the logistic model).
    /// <paramref name="fitted"/> receives the curve at every x; <paramref name="fittedLast"/> is its last value, <paramref name="rSquared"/> is <c>1 - SSE / SST</c> on the PRICE scale
    /// (not clipped; NaN where the window has no variance). Returns false when the model cannot be fitted (a price not above zero for Exponential and Power, a window of equal prices for Logistic,
    /// a kernel error, a non-finite curve value or a non-finite shape parameter). Every call gets a fresh execution budget.
    /// </summary>
    public static bool TryFit(
        NonlinearRegressionModel model,
        double[] x,
        ReadOnlySpan<double> y,
        double[] fitted,
        double[] mapped,
        out NonlinearCurveFit fit,
        out double fittedLast,
        out double rSquared)
    {
        fit = default;
        fittedLast = double.NaN;
        rSquared = double.NaN;
        int n = y.Length;
        Span<double> result = stackalloc double[FitResultLength];
        var budget = DslExecutionBudget.Create(CalculatorLimits.Default.MaxExecutionOps);

        switch (model)
        {
            case NonlinearRegressionModel.Exponential:
            case NonlinearRegressionModel.Power:
            {
                for (int j = 0; j < n; j++)
                {
                    if (!(y[j] > 0.0))
                    {
                        return false;
                    }
                }

                bool exponential = model == NonlinearRegressionModel.Exponential;
                MathErrorCode error = exponential
                    ? DslRegressionMath.RegExpFit(x, y, ReadOnlySpan<double>.Empty, false, result, ref budget)
                    : DslRegressionMath.RegPowFit(x, y, ReadOnlySpan<double>.Empty, false, result, ref budget);
                if (error != MathErrorCode.None)
                {
                    return false;
                }

                fit = new NonlinearCurveFit(model, result[0], result[1], 0.0, 0.0);
                break;
            }

            case NonlinearRegressionModel.Logarithmic:
            {
                MathErrorCode error = DslRegressionMath.RegLogFit(x, y, ReadOnlySpan<double>.Empty, false, result, ref budget);
                if (error != MathErrorCode.None)
                {
                    return false;
                }

                fit = new NonlinearCurveFit(model, result[0], result[1], 0.0, 0.0);
                break;
            }

            case NonlinearRegressionModel.Logistic:
            {
                double min = y[0];
                double max = y[0];
                for (int j = 1; j < n; j++)
                {
                    min = Math.Min(min, y[j]);
                    max = Math.Max(max, y[j]);
                }

                double range = max - min;
                if (!(range > 0.0))
                {
                    return false;
                }

                const double epsilon = IndicatorDefaultConstants.NonlinearLogisticEpsilon;
                const double span = 1.0 - (2.0 * epsilon);
                for (int j = 0; j < n; j++)
                {
                    mapped[j] = epsilon + (span * ((y[j] - min) / range));
                }

                MathErrorCode error = DslRegressionMath.RegLogisticFit(x, mapped, DslTolerance.DefaultTol, result, ref budget);
                if (error != MathErrorCode.None)
                {
                    return false;
                }

                fit = new NonlinearCurveFit(model, result[0], result[1], min, range);
                break;
            }

            default:
                return false;
        }

        double mean = 0.0;
        for (int j = 0; j < n; j++)
        {
            fitted[j] = fit.ValueAt(x[j]);
            if (!double.IsFinite(fitted[j]))
            {
                return false;
            }

            mean += y[j];
        }

        mean /= n;
        double sse = 0.0;
        double sst = 0.0;
        for (int j = 0; j < n; j++)
        {
            sse += (y[j] - fitted[j]) * (y[j] - fitted[j]);
            sst += (y[j] - mean) * (y[j] - mean);
        }

        fittedLast = fitted[n - 1];
        rSquared = sst > 0.0 ? 1.0 - (sse / sst) : double.NaN;
        return double.IsFinite(fit.B);
    }
}
