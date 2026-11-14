using System;

namespace StockAnalyzer.Core.MathUtils;

/// <summary>Result of one GARCH(1,1) estimation window.</summary>
/// <param name="Mean">Window mean of the returns (mu); the model residual is epsilon = r - mu.</param>
/// <param name="Omega">Constant of the variance recursion, fixed by variance targeting: <c>s^2 x (1 - alpha - beta)</c>.</param>
/// <param name="Alpha">Weight of the squared residual.</param>
/// <param name="Beta">Weight of the previous variance.</param>
/// <param name="NextVariance">sigma^2 for the bar after the window's last return (the recursion state).</param>
/// <remarks>When the optimizer result is rejected (outside the constraints, non-finite or not converged) <c>Alpha</c> and <c>Beta</c> are exactly
/// <see cref="Garch11Math.InitialAlpha"/> and <see cref="Garch11Math.InitialBeta"/>.</remarks>
public readonly record struct Garch11Fit(double Mean, double Omega, double Alpha, double Beta, double NextVariance);

/// <summary>
/// GARCH(1,1) (Bollerslev 1986) on percent log returns, estimated by Gaussian quasi-maximum likelihood with variance targeting:
/// <c>epsilon_t = r_t - mu</c>, <c>sigma^2_(t+1) = omega + alpha x epsilon_t^2 + beta x sigma^2_t</c>, <c>omega = s^2 (1 - alpha - beta)</c> where mu and s^2 are the
/// window mean and (divisor N) variance. Only (alpha, beta) are optimised. Unit of the variance: percent squared (the returns are in percent).
/// </summary>
public static class Garch11Math
{
    /// <summary>Largest allowed alpha + beta (the persistence); a point above it is rejected by the objective.</summary>
    public const double MaxPersistence = 0.999;

    /// <summary>Start of the optimizer for alpha, and the value used when the optimizer result is rejected.</summary>
    public const double InitialAlpha = 0.10;

    /// <summary>Start of the optimizer for beta, and the value used when the optimizer result is rejected.</summary>
    public const double InitialBeta = 0.85;

    private const int ParameterCount = 2;
    private const double HalfFactor = 0.5;

    private static readonly double LogTwoPi = Math.Log(2.0 * Math.PI);

    /// <summary>One step of the recursion: <c>omega + alpha x epsilon^2 + beta x variance</c>.</summary>
    public static double NextVarianceStep(double omega, double alpha, double beta, double squaredResidual, double variance)
        => omega + alpha * squaredResidual + beta * variance;

    /// <summary>The constraints of the objective: alpha &gt;= 0, beta &gt;= 0 and alpha + beta &lt;= <see cref="MaxPersistence"/> (all finite).</summary>
    public static bool SatisfiesConstraints(double alpha, double beta)
        => double.IsFinite(alpha) && double.IsFinite(beta) && alpha >= 0.0 && beta >= 0.0 && alpha + beta <= MaxPersistence;

    /// <summary>
    /// Runs the recursion over <paramref name="returns"/>[<paramref name="start"/> .. <paramref name="start"/> + <paramref name="length"/> - 1] starting from
    /// <c>sigma^2 = s2</c> and returns the negative Gaussian log-likelihood <c>sum 0.5 (ln 2 pi + ln sigma^2_t + epsilon_t^2 / sigma^2_t)</c>.
    /// <paramref name="nextVariance"/> receives sigma^2 for the bar after the window. Returns <see cref="double.MaxValue"/> (and a non-finite
    /// <paramref name="nextVariance"/>) for a point outside the constraints or when a variance is not positive and finite on the way.
    /// </summary>
    public static double NegativeLogLikelihood(double[] returns, int start, int length, double mean, double s2, double alpha, double beta, out double nextVariance)
    {
        nextVariance = double.NaN;
        if (!SatisfiesConstraints(alpha, beta))
        {
            return double.MaxValue;
        }

        double omega = s2 * (1.0 - alpha - beta);
        double variance = s2;
        double sum = 0.0;
        for (int t = start; t < start + length; t++)
        {
            if (!(variance > 0.0) || !double.IsFinite(variance))
            {
                return double.MaxValue;
            }

            double residual = returns[t] - mean;
            double squared = residual * residual;
            sum += HalfFactor * (LogTwoPi + Math.Log(variance) + squared / variance);
            variance = NextVarianceStep(omega, alpha, beta, squared, variance);
        }

        if (!(variance > 0.0) || !double.IsFinite(variance) || !double.IsFinite(sum))
        {
            return double.MaxValue;
        }

        nextVariance = variance;
        return sum;
    }

    /// <summary>
    /// Estimates the model on a window of <paramref name="length"/> returns beginning at <paramref name="start"/>.
    /// Returns false (no estimate) when a return of the window is not finite, or when the window variance s^2 is not positive.
    /// Otherwise (alpha, beta) are found with <see cref="NelderMeadOptimizer"/> from (<see cref="InitialAlpha"/>, <see cref="InitialBeta"/>);
    /// a result that violates the constraints, is non-finite or did not converge is replaced by the initial point.
    /// </summary>
    public static bool TryFit(double[] returns, int start, int length, int maxIterations, out Garch11Fit fit)
    {
        fit = default;
        if (length < 1)
        {
            return false;
        }

        double sum = 0.0;
        for (int t = start; t < start + length; t++)
        {
            if (!double.IsFinite(returns[t]))
            {
                return false;
            }

            sum += returns[t];
        }

        double mean = sum / length;
        double squares = 0.0;
        for (int t = start; t < start + length; t++)
        {
            double deviation = returns[t] - mean;
            squares += deviation * deviation;
        }

        double s2 = squares / length;
        if (!(s2 > 0.0) || !double.IsFinite(s2))
        {
            return false;
        }

        Span<double> x = stackalloc double[ParameterCount];
        x[0] = InitialAlpha;
        x[1] = InitialBeta;
        NelderMeadResult result = NelderMeadOptimizer.Minimize(
            p => NegativeLogLikelihood(returns, start, length, mean, s2, p[0], p[1], out _),
            x,
            NelderMeadOptions.Default with { MaxIterations = maxIterations });

        double alpha = x[0];
        double beta = x[1];
        if (!result.Converged || !double.IsFinite(result.Value) || result.Value >= double.MaxValue || !SatisfiesConstraints(alpha, beta))
        {
            alpha = InitialAlpha;
            beta = InitialBeta;
        }

        double objective = NegativeLogLikelihood(returns, start, length, mean, s2, alpha, beta, out double nextVariance);
        if (objective >= double.MaxValue)
        {
            return false;
        }

        fit = new Garch11Fit(mean, s2 * (1.0 - alpha - beta), alpha, beta, nextVariance);
        return true;
    }
}
