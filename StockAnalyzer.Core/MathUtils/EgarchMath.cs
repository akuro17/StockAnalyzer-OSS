using System;
using System.Buffers;

namespace StockAnalyzer.Core.MathUtils;

/// <summary>Tunables of <see cref="EgarchMath.Fit"/> (numerical-analysis settings, not domain values).</summary>
/// <param name="MaxIterations">Simplex iteration cap of each optimizer run.</param>
/// <param name="MaxRestarts">Extra optimizer runs started from the previous optimum (a simplex can stall before the true minimum).</param>
/// <param name="FunctionTolerance">Absolute objective tolerance of one run.</param>
/// <param name="ParameterTolerance">Absolute parameter tolerance of one run.</param>
public readonly record struct EgarchFitOptions(
    int MaxIterations = StockAnalyzer.Core.Models.IndicatorDefaultConstants.EgarchDefaultMaxIterations,
    int MaxRestarts = 2,
    double FunctionTolerance = 1e-10,
    double ParameterTolerance = 1e-8)
{
    /// <summary>The documented defaults (a parameterless <c>new EgarchFitOptions()</c> is all zeros, so use this).</summary>
    public static EgarchFitOptions Default { get; } = new(MaxIterations: StockAnalyzer.Core.Models.IndicatorDefaultConstants.EgarchDefaultMaxIterations);
}

/// <summary>Quantities fixed once from the estimation sample and shared by the likelihood and the forward filter.</summary>
/// <param name="LogBackcast">ln of the pre-sample variance: exponentially weighted (0.94) mean of the first ≤75 squared residuals.</param>
/// <param name="LogMeanSquare">ln of the mean squared residual (centre of the ω bounds and of the starting values).</param>
/// <param name="MinVariance">Lower guard for σ² (numerical safety only).</param>
/// <param name="MaxVariance">Upper guard for σ² (numerical safety only).</param>
public readonly record struct EgarchInitialization(double LogBackcast, double LogMeanSquare, double MinVariance, double MaxVariance);

/// <param name="Success">A feasible optimum with a finite likelihood was found (θ is usable).</param>
/// <param name="Converged">The optimizer met its tolerances (false = iteration cap reached; θ is still the best point found).</param>
/// <param name="LogLikelihood">Gaussian quasi-log-likelihood at θ.</param>
/// <param name="Initialization">Initialization to pass to <see cref="EgarchMath.FilterOneStepAhead"/> with θ.</param>
public readonly record struct EgarchFitResult(bool Success, bool Converged, double LogLikelihood, EgarchInitialization Initialization);

/// <summary>
/// EGARCH(P, O=P, Q) with a constant mean, estimated by Gaussian quasi-maximum likelihood.
/// <code>
/// r_t = μ + ε_t,  ε_t = σ_t·z_t
/// ln σ²_t = ω + Σ_i α_i(|z_{t-i}| − √(2/π)) + Σ_i γ_i·z_{t-i} + Σ_j β_j·ln σ²_{t-j}
/// </code>
/// Source: Nelson (1991), Econometrica 59(2), 347-370, in the parametrization and initialization of arch 8.0.0
/// (<c>arch.univariate.EGARCH</c> / <c>egarch_recursion</c>): pre-sample innovation terms contribute 0, pre-sample ln σ² is the log backcast.
/// Parameter vector θ = [μ, ω, α_1..α_P, γ_1..γ_P, β_1..β_Q]. Units: returns are in percent per bar, so σ is percent per bar.
/// </summary>
public static class EgarchMath
{
    /// <summary>E|z| for z ~ N(0,1), the centring constant of the magnitude term.</summary>
    public static readonly double ExpectedAbsStandardNormal = Math.Sqrt(2.0 / Math.PI);

    /// <summary>EWMA decay of the backcast weights (arch default).</summary>
    public const double BackcastDecay = 0.94;

    /// <summary>Number of leading observations used by the backcast (arch default).</summary>
    public const int BackcastMaxObservations = 75;

    /// <summary>ln σ² is capped here to keep exp() finite (arch: ln(DBL_MAX) − 0.1).</summary>
    public static readonly double LogVarianceCeiling = Math.Log(double.MaxValue) - 0.1;

    /// <summary>ω is searched within ± this (in ln units, = ln 10⁴) of the log mean square (arch bound).</summary>
    public static readonly double OmegaLogRange = Math.Log(10000.0);

    /// <summary>σ² lower guard = mean square / this (arch).</summary>
    public const double MinVarianceDivisor = 1e8;

    /// <summary>σ² upper guard = this × (1 + max ε²) (arch).</summary>
    public const double MaxVarianceMultiplier = 1e7;

    // Starting-value grid of arch's EGARCH.starting_values.
    private static readonly double[] StartAlphas = { 0.01, 0.05, 0.1, 0.2 };
    private static readonly double[] StartGammas = { -0.1, 0.0, 0.1 };
    private static readonly double[] StartBetas = { 0.5, 0.7, 0.9, 0.98 };

    public static int ParameterCount(int p, int q) => 2 + 2 * p + q;

    /// <summary>Upper bound of each β_j: arch bounds every β_j to [0, q] (the tighter Σβ ≤ 1 constraint is what limits the sum).</summary>
    private static double BetaUpperBound(int q) => q;

    /// <summary>Continuously compounded return in percent: 100·ln(P_t / P_{t−1}); NaN when either price is not positive and finite.</summary>
    public static double PercentLogReturn(double previousPrice, double price)
        => previousPrice > 0.0 && price > 0.0 && double.IsFinite(previousPrice) && double.IsFinite(price)
            ? 100.0 * Math.Log(price / previousPrice)
            : double.NaN;

    /// <summary>
    /// Whether a conditional σ lies inside the plausibility band [<paramref name="minRatio"/>, <paramref name="maxRatio"/>] × the estimation window's
    /// root mean square return (√(mean ε²), the centre used by the ω bounds). NaN is never plausible. A ratio of 0 for the minimum switches the lower
    /// limit off. Depends on σ_t only, so applying it per bar keeps the indicator causal.
    /// </summary>
    public static bool IsSigmaPlausible(double sigma, in EgarchInitialization initialization, double minRatio, double maxRatio)
    {
        double rootMeanSquare = Math.Exp(0.5 * initialization.LogMeanSquare);
        return sigma >= minRatio * rootMeanSquare && sigma <= maxRatio * rootMeanSquare;
    }

    /// <summary>Builds the shared initialization from the estimation sample; false when the sample has no usable variance.</summary>
    public static bool TryCreateInitialization(ReadOnlySpan<double> returns, out EgarchInitialization initialization)
    {
        initialization = default;
        int n = returns.Length;
        if (n < 2)
        {
            return false;
        }

        double mean = 0.0;
        for (int t = 0; t < n; t++)
        {
            if (!double.IsFinite(returns[t]))
            {
                return false;
            }
            mean += returns[t];
        }
        mean /= n;

        double sumSquares = 0.0;
        double maxSquare = 0.0;
        for (int t = 0; t < n; t++)
        {
            double square = (returns[t] - mean) * (returns[t] - mean);
            sumSquares += square;
            maxSquare = Math.Max(maxSquare, square);
        }
        double meanSquare = sumSquares / n;
        if (!(meanSquare > 0.0) || !double.IsFinite(meanSquare))
        {
            return false;
        }

        int tau = Math.Min(BackcastMaxObservations, n);
        double weight = 1.0;
        double weightSum = 0.0;
        double weighted = 0.0;
        for (int t = 0; t < tau; t++)
        {
            double square = (returns[t] - mean) * (returns[t] - mean);
            weighted += weight * square;
            weightSum += weight;
            weight *= BackcastDecay;
        }
        double backcast = weighted / weightSum;
        if (!(backcast > 0.0))
        {
            return false;
        }

        initialization = new EgarchInitialization(
            Math.Log(backcast),
            Math.Log(meanSquare),
            meanSquare / MinVarianceDivisor,
            MaxVarianceMultiplier * (1.0 + maxSquare));
        return true;
    }

    /// <summary>Gaussian quasi-log-likelihood of <paramref name="returns"/> at θ; −∞ when the recursion is not finite.</summary>
    public static double LogLikelihood(ReadOnlySpan<double> returns, ReadOnlySpan<double> theta, int p, int q, in EgarchInitialization initialization)
    {
        int n = returns.Length;
        double[] rented = ArrayPool<double>.Shared.Rent(2 * n);
        try
        {
            return Recurse(returns, theta, p, q, initialization, rented.AsSpan(0, n), rented.AsSpan(n, n), default, int.MaxValue, out _);
        }
        finally
        {
            ArrayPool<double>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Conditional standard deviations σ_0..σ_n (n = <c>returns.Length</c>) at fixed θ. σ_t depends on returns before t only, so σ_n is the
    /// one-step-ahead value for the bar after the sample. <paramref name="sigma"/> must have length ≥ n + 1.
    /// </summary>
    public static void FilterOneStepAhead(ReadOnlySpan<double> returns, ReadOnlySpan<double> theta, int p, int q, in EgarchInitialization initialization, Span<double> sigma)
        => FilterOneStepAhead(returns, theta, p, q, initialization, sigma, int.MaxValue, out _);

    /// <summary>
    /// As <see cref="FilterOneStepAhead(ReadOnlySpan{double}, ReadOnlySpan{double}, int, int, in EgarchInitialization, Span{double})"/>, and reports in
    /// <paramref name="firstGuardedIndex"/> the first index t (guardCheckFrom &lt;= t &lt;= n) whose variance was held by a numerical guard (σ²_t at or
    /// below <see cref="EgarchInitialization.MinVariance"/> or above <see cref="EgarchInitialization.MaxVariance"/>), or −1 when none was. A path that
    /// reaches a guard has diverged (variance collapse followed by run-away), so σ from that step on is not a usable volatility estimate. The index
    /// depends only on returns before it (causal), so a caller can blank exactly the steps from the first guard on.
    /// </summary>
    public static void FilterOneStepAhead(ReadOnlySpan<double> returns, ReadOnlySpan<double> theta, int p, int q, in EgarchInitialization initialization, Span<double> sigma, int guardCheckFrom, out int firstGuardedIndex)
    {
        int n = returns.Length;
        if (sigma.Length < n + 1)
        {
            throw new ArgumentException("sigma must have length >= returns.Length + 1.", nameof(sigma));
        }

        double[] rented = ArrayPool<double>.Shared.Rent(2 * n + 1);
        try
        {
            Span<double> lnSigma2 = rented.AsSpan(0, n + 1);
            Span<double> z = rented.AsSpan(n + 1, n);
            Recurse(returns, theta, p, q, initialization, lnSigma2, z, sigma, guardCheckFrom, out firstGuardedIndex);
        }
        finally
        {
            ArrayPool<double>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Estimates θ by QMLE. <paramref name="theta"/> (length <see cref="ParameterCount"/>) receives the optimum. Starts from the best of the
    /// arch starting-value grid, then runs Nelder-Mead (with restarts) under arch's constraints: β_j ≥ 0, Σβ ≤ 1, ω within ln 10⁴ of the log mean square.
    /// </summary>
    public static EgarchFitResult Fit(ReadOnlySpan<double> returns, int p, int q, Span<double> theta, EgarchFitOptions? options = null)
    {
        if (p < 1 || q < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(p), "EGARCH needs P >= 1 and Q >= 1.");
        }

        int k = ParameterCount(p, q);
        if (theta.Length != k)
        {
            throw new ArgumentException($"theta must have length {k}.", nameof(theta));
        }

        var opts = options ?? EgarchFitOptions.Default;
        if (!TryCreateInitialization(returns, out var init))
        {
            return new EgarchFitResult(false, false, double.NegativeInfinity, default);
        }

        int n = returns.Length;
        double[] data = returns.ToArray();
        double[] lnSigma2 = new double[n];
        double[] z = new double[n];
        double mean = 0.0;
        for (int t = 0; t < n; t++)
        {
            mean += data[t];
        }
        mean /= n;

        int betaStart = 2 + 2 * p;
        double omegaLow = init.LogMeanSquare - OmegaLogRange;
        double omegaHigh = init.LogMeanSquare + OmegaLogRange;

        double Objective(ReadOnlySpan<double> x)
        {
            if (x[1] < omegaLow || x[1] > omegaHigh)
            {
                return double.PositiveInfinity;
            }

            double betaSum = 0.0;
            for (int j = 0; j < q; j++)
            {
                double beta = x[betaStart + j];
                if (!(beta >= 0.0) || beta > BetaUpperBound(q))
                {
                    return double.PositiveInfinity;
                }
                betaSum += beta;
            }
            if (betaSum > 1.0)
            {
                return double.PositiveInfinity;
            }

            double logLikelihood = Recurse(data, x, p, q, init, lnSigma2, z, default, int.MaxValue, out _);
            return double.IsFinite(logLikelihood) ? -logLikelihood : double.PositiveInfinity;
        }

        // Starting values: best of arch's (α, γ, β) grid, μ at the sample mean.
        double target = init.LogMeanSquare;
        Span<double> candidate = stackalloc double[k];
        double bestGrid = double.NegativeInfinity;
        foreach (double alpha in StartAlphas)
        {
            foreach (double gamma in StartGammas)
            {
                foreach (double beta in StartBetas)
                {
                    candidate[0] = mean;
                    candidate[1] = (1.0 - beta) * target;
                    for (int i = 0; i < p; i++)
                    {
                        candidate[2 + i] = alpha / p;
                        candidate[2 + p + i] = gamma / p;
                    }
                    for (int j = 0; j < q; j++)
                    {
                        candidate[betaStart + j] = beta / q;
                    }

                    double value = Objective(candidate);
                    if (-value > bestGrid)
                    {
                        bestGrid = -value;
                        candidate.CopyTo(theta);
                    }
                }
            }
        }

        if (double.IsNegativeInfinity(bestGrid))
        {
            return new EgarchFitResult(false, false, double.NegativeInfinity, init);
        }

        var nelderMead = new NelderMeadOptions(opts.MaxIterations, opts.FunctionTolerance, opts.ParameterTolerance);
        NelderMeadResult run = NelderMeadOptimizer.Minimize(Objective, theta, nelderMead);
        for (int restart = 0; restart < opts.MaxRestarts; restart++)
        {
            double before = run.Value;
            run = NelderMeadOptimizer.Minimize(Objective, theta, nelderMead);
            if (before - run.Value <= opts.FunctionTolerance)
            {
                break;
            }
        }

        bool finite = double.IsFinite(run.Value);
        return new EgarchFitResult(finite, finite && run.Converged, finite ? -run.Value : double.NegativeInfinity, init);
    }

    private static void RecordGuard(int step, int guardCheckFrom, ref int firstGuardedIndex)
    {
        if (firstGuardedIndex < 0 && step >= guardCheckFrom)
        {
            firstGuardedIndex = step;
        }
    }

    /// <summary>
    /// The EGARCH recursion. Fills ln σ²_t / z_t; returns the log-likelihood over the <c>returns.Length</c> observations (−∞ if not finite).
    /// When <paramref name="sigma"/> is given, also writes σ_0..σ_n (lnSigma2 must then have n + 1 elements).
    /// <paramref name="firstGuardedIndex"/> is the first step t &gt;= <paramref name="guardCheckFrom"/> whose variance was clamped by a guard (−1 when none).
    /// </summary>
    private static double Recurse(
        ReadOnlySpan<double> returns,
        ReadOnlySpan<double> theta,
        int p,
        int q,
        in EgarchInitialization init,
        Span<double> lnSigma2,
        Span<double> z,
        Span<double> sigma,
        int guardCheckFrom,
        out int firstGuardedIndex)
    {
        firstGuardedIndex = -1;
        int n = returns.Length;
        double mu = theta[0];
        double omega = theta[1];
        int gammaStart = 2 + p;
        int betaStart = 2 + 2 * p;
        int steps = sigma.IsEmpty ? n : n + 1;
        double constant = -0.5 * Math.Log(MathConstants.TwoPi);
        double logLikelihood = 0.0;

        for (int t = 0; t < steps; t++)
        {
            double ln = omega;
            for (int i = 0; i < p; i++)
            {
                if (t - 1 - i >= 0)
                {
                    double zPrev = z[t - 1 - i];
                    ln += theta[2 + i] * (Math.Abs(zPrev) - ExpectedAbsStandardNormal) + theta[gammaStart + i] * zPrev;
                }
            }
            for (int j = 0; j < q; j++)
            {
                ln += theta[betaStart + j] * (t - 1 - j < 0 ? init.LogBackcast : lnSigma2[t - 1 - j]);
            }

            ln = Math.Min(ln, LogVarianceCeiling);
            double variance = Math.Exp(ln);
            if (variance <= init.MinVariance)
            {
                variance = init.MinVariance;
                RecordGuard(t, guardCheckFrom, ref firstGuardedIndex);
            }
            else if (variance > init.MaxVariance)
            {
                variance = init.MaxVariance + Math.Log(variance) - Math.Log(init.MaxVariance);
                RecordGuard(t, guardCheckFrom, ref firstGuardedIndex);
            }
            ln = Math.Log(variance);
            lnSigma2[t] = ln;

            if (!sigma.IsEmpty)
            {
                sigma[t] = Math.Sqrt(variance);
            }

            if (t < n)
            {
                double residual = returns[t] - mu;
                z[t] = residual / Math.Sqrt(variance);
                logLikelihood += constant - 0.5 * (ln + residual * residual / variance);
            }
        }

        return double.IsFinite(logLikelihood) ? logLikelihood : double.NegativeInfinity;
    }
}
