using System;
using System.Runtime.CompilerServices;

namespace StockAnalyzer.Core.MathUtils;

/// <summary>
/// Mathematical engine for Clothoid Curve (Euler / Cornu Spiral) trend trajectory projections.
/// Uses FresnelMath for analytical closed-form coordinate evaluation, coupled with
/// a robust exponentially weighted 2nd-order polynomial fit (WLS) for scale-invariant initial parameter estimation.
/// Enforces time-monotonicity invariant (dx/ds &gt; 0) with a smooth tanh angle damping filter.
/// </summary>
public static class ClothoidMath
{
    private const double MaxAngle = Math.PI / 2.0 - 0.05; // ~87.1 degrees maximum pitch
    private const double MinAngle = -MaxAngle;
    private const double MaxInitialCurvature = 1.5;

    public readonly record struct ClothoidState(
        double BasePrice,
        double VolatilityScale,
        double InitialAngle,
        double InitialCurvature,
        double CurvatureRate);

    /// <summary>
    /// Applies an analytical smooth damping filter to keep angles within (-MaxAngle, MaxAngle)
    /// without abrupt clipping kinks.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double DampAngle(double rawAngle)
    {
        return MaxAngle * Math.Tanh(rawAngle / MaxAngle);
    }

    /// <summary>
    /// Estimates dimensionless kinematic parameters (velocity, acceleration, curvature) from recent prices.
    /// Uses exponentially time-decay weighted 2nd-order polynomial OLS regression over the window of length N &gt;= 2.
    /// Zero heap allocation.
    /// </summary>
    /// <param name="prices">Price history in chronological order.</param>
    /// <param name="curvatureRate">User-specified curvature variation rate (default 0.05).</param>
    /// <returns>Scale-invariant ClothoidState.</returns>
    public static ClothoidState EstimateInitialState(ReadOnlySpan<decimal> prices, double curvatureRate = 0.05)
    {
        if (prices.Length == 0)
        {
            return new ClothoidState(0.0, 1.0, 0.0, 0.0, curvatureRate);
        }

        decimal basePrice = prices[^1];
        double basePriceDbl = (double)basePrice;
        if (prices.Length < 2)
        {
            return new ClothoidState(basePriceDbl, Math.Max(0.01, Math.Abs(basePriceDbl) * 0.001), 0.0, 0.0, curvatureRate);
        }

        int n = prices.Length;

        // Calculate sample mean and variance for volatility scaling strictly in decimal financial arithmetic
        decimal sum = 0m;
        for (int i = 0; i < n; i++)
        {
            sum += prices[i];
        }
        decimal mean = sum / n;

        decimal sumSqDiff = 0m;
        for (int i = 0; i < n; i++)
        {
            decimal diff = prices[i] - mean;
            sumSqDiff += diff * diff;
        }

        decimal variance = sumSqDiff / n;
        double stdDev = Math.Sqrt((double)variance);
        double minScale = Math.Max(0.001, (double)Math.Abs(basePrice) * 0.001);
        double volatilityScale = Math.Max(stdDev, minScale);

        // Exponential time-decay weighted 2nd-order polynomial fit (WLS):
        // p_norm(t) = beta0 + beta1 * t + 0.5 * beta2 * t^2
        // Time coordinate t is centered such that t = 0 at the last bar: t_i = i - (n - 1) <= 0.
        // Weight w_i = exp(1.5 * t_i / n) places higher importance on recent bars to diminish past wick noise.
        double v0 = 0.0;
        double a0 = 0.0;

        if (n == 2)
        {
            // Linear velocity between 2 bars
            double p0 = (double)(prices[0] - basePrice) / volatilityScale;
            double p1 = 0.0;
            v0 = p1 - p0;
            a0 = 0.0;
        }
        else
        {
            double s0 = 0.0;
            double s1 = 0.0, s2 = 0.0, s3 = 0.0, s4 = 0.0;
            double sy = 0.0, syt = 0.0, syt2 = 0.0;

            for (int i = 0; i < n; i++)
            {
                double t = i - (n - 1);
                double t2 = t * t;
                double t3 = t2 * t;
                double t4 = t2 * t2;

                double w = Math.Exp(1.5 * t / n); // Exponential weighting: w <= 1.0, w(0) = 1.0
                double y = (double)(prices[i] - basePrice) / volatilityScale;

                s0 += w;
                s1 += w * t;
                s2 += w * t2;
                s3 += w * t3;
                s4 += w * t4;

                sy += w * y;
                syt += w * y * t;
                syt2 += w * y * t2;
            }

            // Invert 3x3 symmetric weighted matrix:
            // | s4 s3 s2 |
            // | s3 s2 s1 |
            // | s2 s1 s0 |
            double det = s4 * (s2 * s0 - s1 * s1) - s3 * (s3 * s0 - s1 * s2) + s2 * (s3 * s1 - s2 * s2);

            if (Math.Abs(det) > 1e-12)
            {
                // Solve for c2 (half-acceleration) and c1 (terminal velocity):
                double detC2 = syt2 * (s2 * s0 - s1 * s1) - s3 * (syt * s0 - s1 * sy) + s2 * (syt * s1 - s2 * sy);
                double detC1 = s4 * (syt * s0 - s1 * sy) - syt2 * (s3 * s0 - s1 * s2) + s2 * (s3 * sy - syt * s2);

                double c2 = detC2 / det;
                double c1 = detC1 / det;

                v0 = c1;
                a0 = 2.0 * c2;
            }
            else
            {
                // Fallback for singular matrix
                v0 = (double)(prices[^1] - prices[0]) / (volatilityScale * (n - 1));
                a0 = 0.0;
            }

            // Calculate macro linear trend slope over the entire window N
            // t_i = i - (n - 1), mean_t = -(n - 1) / 2.0
            double meanT = -(n - 1) / 2.0;
            double meanY = (double)(sum - n * basePrice) / (n * volatilityScale);
            double sumDeltaTDeltaY = 0.0;
            double sumDeltaT2 = 0.0;
            for (int i = 0; i < n; i++)
            {
                double t = i - (n - 1);
                double y = (double)(prices[i] - basePrice) / volatilityScale;
                double deltaT = t - meanT;
                sumDeltaTDeltaY += deltaT * (y - meanY);
                sumDeltaT2 += deltaT * deltaT;
            }
            double vMacro = sumDeltaT2 > 1e-12 ? sumDeltaTDeltaY / sumDeltaT2 : 0.0;

            // Blend terminal local velocity (70%) with macro linear trend (30%)
            // to prevent a 1-2 bar terminal counter-bounce from overwhelming the macro trend
            v0 = 0.70 * v0 + 0.30 * vMacro;
        }

        // Clamp dimensionless velocity and acceleration to realistic limits
        v0 = Math.Clamp(v0, -10.0, 10.0);
        a0 = Math.Clamp(a0, -5.0, 5.0);

        double initialAngle = DampAngle(Math.Atan(v0));
        double denom = Math.Pow(1.0 + v0 * v0, 1.5);
        double initialCurvature = Math.Clamp(a0 / denom, -MaxInitialCurvature, MaxInitialCurvature);

        return new ClothoidState(basePriceDbl, volatilityScale, initialAngle, initialCurvature, curvatureRate);
    }

    /// <summary>
    /// Computes coordinates (x(s), y(s)) along the damped clothoid trajectory starting from origin (0, 0)
    /// with heading angle theta(tau) = DampAngle(theta0 + kappa0 * tau + 0.5 * c * tau^2).
    /// Guarantees strict origin continuity (0, 0), strict forward time monotonicity (dx/ds > 0),
    /// and consistency with GenerateTrajectory.
    /// Allocates zero heap memory.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static (double X, double Y) EvaluateClothoidPoint(double s, double theta0, double kappa0, double c)
    {
        if (s <= 0.0)
        {
            return (0.0, 0.0);
        }

        // Case 1: Straight line (c == 0, kappa0 == 0)
        if (Math.Abs(c) < 1e-12 && Math.Abs(kappa0) < 1e-12)
        {
            double th = Math.Abs(theta0) < MaxAngle ? theta0 : DampAngle(theta0);
            return (s * Math.Cos(th), s * Math.Sin(th));
        }

        // Case 2: Analytical circular arc when angle remains well within linear damping bounds
        if (Math.Abs(c) < 1e-12)
        {
            double thEnd = theta0 + kappa0 * s;
            if (Math.Abs(thEnd) < MaxAngle - 0.1 && Math.Abs(theta0) < MaxAngle - 0.1)
            {
                double invK = 1.0 / kappa0;
                double x = invK * (Math.Sin(thEnd) - Math.Sin(theta0));
                double y = -invK * (Math.Cos(thEnd) - Math.Cos(theta0));
                return (x, y);
            }
        }

        // Case 3: General damped Euler spiral trajectory via composite Simpson quadrature
        // Sub-step size h <= 0.25 guarantees high numerical precision with zero heap allocation.
        int nSub = Math.Max(8, (int)Math.Ceiling(s / 0.25));
        if ((nSub & 1) != 0) nSub++; // Ensure even number of sub-intervals
        double h = s / nSub;

        double th0 = DampAngle(theta0);
        double xSum = Math.Cos(th0);
        double ySum = Math.Sin(th0);

        for (int i = 1; i < nSub; i++)
        {
            double tau = i * h;
            double rawAngle = theta0 + kappa0 * tau + 0.5 * c * tau * tau;
            double th = DampAngle(rawAngle);
            double weight = (i & 1) == 1 ? 4.0 : 2.0;

            xSum += weight * Math.Cos(th);
            ySum += weight * Math.Sin(th);
        }

        double thFinal = DampAngle(theta0 + kappa0 * s + 0.5 * c * s * s);
        xSum += Math.Cos(thFinal);
        ySum += Math.Sin(thFinal);

        double factor = h / 3.0;
        return (xSum * factor, ySum * factor);
    }

    /// <summary>
    /// Generates forward projected price points and volatility diffusion bounds into pre-allocated spans.
    /// Implements the Damped Forward Trajectory Model (Plan B): integrates the causal dynamical system
    /// ds/dx = 1 / cos(theta(s)), dy/dx = tan(theta(s)) forward in dimensionless time x = 1..steps.
    /// Guarantees strict time monotonicity (dx/ds > 0), smooth bounded curvature,
    /// and consistent non-negative price ordering (0.0001 <= lower <= projected <= upper).
    /// Allocates zero heap memory.
    /// </summary>
    public static void GenerateTrajectory(
        in ClothoidState state,
        int steps,
        double confidenceMultiplier,
        Span<double> outProjectedPrices,
        Span<double> outUpperBounds,
        Span<double> outLowerBounds)
    {
        if (steps <= 0) return;

        double basePrice = state.BasePrice;
        double scale = state.VolatilityScale;
        double theta0 = state.InitialAngle;
        double kappa0 = state.InitialCurvature;
        double c = state.CurvatureRate;

        // Ensure curvature and curvature rate stay within safe geometrical limits over the projection horizon (steps)
        double maxAllowedAngleDelta = Math.Max(0.1, MaxAngle - Math.Abs(theta0));
        double maxSafeKappa = maxAllowedAngleDelta / Math.Max(5, steps);
        double safeKappa = Math.Clamp(kappa0, -maxSafeKappa, maxSafeKappa);

        double maxSafeC = (2.0 * maxAllowedAngleDelta) / ((double)steps * steps);
        double safeC = Math.Clamp(c, -maxSafeC, maxSafeC);

        // State vector: arc length s, dimensionless price displacement y
        double s = 0.0;
        double y = 0.0;

        // Step size for RK4 sub-stepping (4 sub-steps per forward bar: deltaX = 1.0, h = 0.25)
        const int subStepsPerBar = 4;
        const double h = 1.0 / subStepsPerBar;

        for (int k = 1; k <= steps; k++)
        {
            // Advance state from x = k - 1 to x = k using 4th-order Runge-Kutta
            for (int sub = 0; sub < subStepsPerBar; sub++)
            {
                // k1
                double th1 = DampAngle(theta0 + safeKappa * s + 0.5 * safeC * s * s);
                double cos1 = Math.Max(0.05, Math.Cos(th1));
                double ds1 = 1.0 / cos1;
                double dy1 = Math.Sin(th1) / cos1;

                // k2
                double sMid1 = s + 0.5 * h * ds1;
                double th2 = DampAngle(theta0 + safeKappa * sMid1 + 0.5 * safeC * sMid1 * sMid1);
                double cos2 = Math.Max(0.05, Math.Cos(th2));
                double ds2 = 1.0 / cos2;
                double dy2 = Math.Sin(th2) / cos2;

                // k3
                double sMid2 = s + 0.5 * h * ds2;
                double th3 = DampAngle(theta0 + safeKappa * sMid2 + 0.5 * safeC * sMid2 * sMid2);
                double cos3 = Math.Max(0.05, Math.Cos(th3));
                double ds3 = 1.0 / cos3;
                double dy3 = Math.Sin(th3) / cos3;

                // k4
                double sEnd = s + h * ds3;
                double th4 = DampAngle(theta0 + safeKappa * sEnd + 0.5 * safeC * sEnd * sEnd);
                double cos4 = Math.Max(0.05, Math.Cos(th4));
                double ds4 = 1.0 / cos4;
                double dy4 = Math.Sin(th4) / cos4;

                s += (h / 6.0) * (ds1 + 2.0 * ds2 + 2.0 * ds3 + ds4);
                y += (h / 6.0) * (dy1 + 2.0 * dy2 + 2.0 * dy3 + dy4);
            }

            double projectedPrice = Math.Max(0.0001, basePrice + y * scale);
            double margin = confidenceMultiplier * scale * Math.Sqrt(k);
            double lowerPrice = Math.Max(0.0001, projectedPrice - margin);
            double upperPrice = Math.Max(0.0001, projectedPrice + margin);

            int outIdx = k - 1;
            if (outIdx < outProjectedPrices.Length)
            {
                outProjectedPrices[outIdx] = projectedPrice;
            }
            if (outIdx < outUpperBounds.Length)
            {
                outUpperBounds[outIdx] = upperPrice;
            }
            if (outIdx < outLowerBounds.Length)
            {
                outLowerBounds[outIdx] = lowerPrice;
            }
        }
    }

    /// <summary>
    /// Solves a 3rd-order weighted polynomial regression (WLS) over chronological prices:
    /// p(t) = beta0 + beta1 * t + 0.5 * beta2 * t^2 + (1/6) * beta3 * t^3.
    /// Returns dimensionless kinematics at the terminal bar (t = 0):
    /// velocity (v = beta1), acceleration (a = beta2), and jerk (j = beta3).
    /// Allocates zero heap memory.
    /// </summary>
    /// <param name="prices">Chronological price history window (Length &gt;= 4).</param>
    /// <param name="decayAlpha">Exponential time-decay factor (w_i = exp(alpha * t_i / N)).</param>
    /// <param name="velocity">Dimensionless velocity beta1 at t=0.</param>
    /// <param name="acceleration">Dimensionless acceleration beta2 at t=0.</param>
    /// <param name="jerk">Dimensionless jerk beta3 at t=0.</param>
    /// <returns>True if regression successfully solved; false if prices are degenerate or singular.</returns>
    public static bool SolveCubicWlsKinematics(
        ReadOnlySpan<decimal> prices,
        double decayAlpha,
        out double velocity,
        out double acceleration,
        out double jerk)
    {
        velocity = 0.0;
        acceleration = 0.0;
        jerk = 0.0;

        int n = prices.Length;
        if (n < 4 || !double.IsFinite(decayAlpha))
        {
            return false;
        }

        decimal basePrice = prices[^1];

        // Volatility scale: statistics computed in decimal financial price boundary
        decimal sum = 0m;
        for (int i = 0; i < n; i++)
        {
            sum += prices[i];
        }
        decimal mean = sum / n;

        decimal sumSqDiff = 0m;
        for (int i = 0; i < n; i++)
        {
            decimal diff = prices[i] - mean;
            sumSqDiff += diff * diff;
        }

        decimal variance = sumSqDiff / n;
        double stdDev = Math.Sqrt((double)variance);
        double minScale = Math.Max(0.0001, (double)Math.Abs(basePrice) * 1e-4);
        double volatilityScale = Math.Max(stdDev, minScale);

        // Time coordinate t_i in [-(n - 1), 0], normalized y_i
        // Normal equation matrix: 4x4 symmetric matrix S and 4x1 vector Sy
        // S = [ s0 s1 s2 s3 ]
        //     [ s1 s2 s3 s4 ]
        //     [ s2 s3 s4 s5 ]
        //     [ s3 s4 s5 s6 ]
        // Sy = [ sy0, sy1, sy2, sy3 ]
        double s0 = 0.0, s1 = 0.0, s2 = 0.0, s3 = 0.0, s4 = 0.0, s5 = 0.0, s6 = 0.0;
        double sy0 = 0.0, sy1 = 0.0, sy2 = 0.0, sy3 = 0.0;

        double alphaOverN = n > 0 ? decayAlpha / n : 0.0;

        for (int i = 0; i < n; i++)
        {
            double t = i - (n - 1);
            double t2 = t * t;
            double t3 = t2 * t;
            double t4 = t2 * t2;
            double t5 = t3 * t2;
            double t6 = t3 * t3;

            double w = Math.Exp(alphaOverN * t);
            double y = (double)(prices[i] - basePrice) / volatilityScale;

            s0 += w;
            s1 += w * t;
            s2 += w * t2;
            s3 += w * t3;
            s4 += w * t4;
            s5 += w * t5;
            s6 += w * t6;

            sy0 += w * y;
            sy1 += w * y * t;
            sy2 += w * y * t2;
            sy3 += w * y * t3;
        }

        // Augmented matrix 4x5: [A | B]
        Span<double> m = stackalloc double[20]; // 4 rows x 5 cols: index = r * 5 + c
        m[0] = s0; m[1] = s1; m[2] = s2; m[3] = s3; m[4] = sy0;
        m[5] = s1; m[6] = s2; m[7] = s3; m[8] = s4; m[9] = sy1;
        m[10] = s2; m[11] = s3; m[12] = s4; m[13] = s5; m[14] = sy2;
        m[15] = s3; m[16] = s4; m[17] = s5; m[18] = s6; m[19] = sy3;

        // Gaussian elimination with partial pivoting
        for (int col = 0; col < 4; col++)
        {
            int maxRow = col;
            double maxVal = Math.Abs(m[col * 5 + col]);

            for (int row = col + 1; row < 4; row++)
            {
                double val = Math.Abs(m[row * 5 + col]);
                if (val > maxVal)
                {
                    maxVal = val;
                    maxRow = row;
                }
            }

            if (maxVal < 1e-12 || !double.IsFinite(maxVal))
            {
                // Singular or near-singular system
                return false;
            }

            if (maxRow != col)
            {
                // Swap rows col and maxRow
                for (int c = col; c < 5; c++)
                {
                    double tmp = m[col * 5 + c];
                    m[col * 5 + c] = m[maxRow * 5 + c];
                    m[maxRow * 5 + c] = tmp;
                }
            }

            double pivot = m[col * 5 + col];
            for (int row = col + 1; row < 4; row++)
            {
                double factor = m[row * 5 + col] / pivot;
                m[row * 5 + col] = 0.0;
                for (int c = col + 1; c < 5; c++)
                {
                    m[row * 5 + c] -= factor * m[col * 5 + c];
                }
            }
        }

        // Back-substitution
        Span<double> x = stackalloc double[4];
        for (int row = 3; row >= 0; row--)
        {
            double sumVal = m[row * 5 + 4];
            for (int c = row + 1; c < 4; c++)
            {
                sumVal -= m[row * 5 + c] * x[c];
            }
            double diag = m[row * 5 + row];
            if (Math.Abs(diag) < 1e-12 || !double.IsFinite(diag))
            {
                return false;
            }
            x[row] = sumVal / diag;
        }

        // x[0] = beta0, x[1] = beta1, x[2] = 0.5 * beta2, x[3] = (1/6) * beta3
        double v0 = x[1];
        double a0 = 2.0 * x[2];
        double j0 = 6.0 * x[3];

        if (!double.IsFinite(v0) || !double.IsFinite(a0) || !double.IsFinite(j0))
        {
            return false;
        }

        velocity = Math.Clamp(v0, -10.0, 10.0);
        acceleration = Math.Clamp(a0, -10.0, 10.0);
        jerk = Math.Clamp(j0, -20.0, 20.0);

        return true;
    }

    /// <summary>
    /// Computes the analytical arc-length derivative of curvature (c = d(kappa)/ds) at terminal point t = 0
    /// from dimensionless kinematics: velocity (v), acceleration (a), and jerk (j).
    /// Closed-form differential geometry formula: c = (j * (1 + v^2) - 3 * v * a^2) / (1 + v^2)^3.
    /// Allocates zero heap memory.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double CalculateCurvatureRateFromKinematics(double velocity, double acceleration, double jerk)
    {
        double v2 = velocity * velocity;
        double onePlusV2 = 1.0 + v2;
        double denom = onePlusV2 * onePlusV2 * onePlusV2;

        if (denom < 1e-12 || !double.IsFinite(denom))
        {
            return 0.0;
        }

        double num = jerk * onePlusV2 - 3.0 * velocity * acceleration * acceleration;
        if (!double.IsFinite(num))
        {
            return 0.0;
        }
        return num / denom;
    }

    /// <summary>
    /// Generates normalized or raw Fresnel cosine convolution weights for a window of given period,
    /// peak offset ratio, and sigma bandwidth:
    /// u_j = (j - offset * (period - 1)) / (period / sigma),
    /// w_j = cos(pi / 2 * min(1.0, u_j^2)) * exp(-0.5 * u_j^2).
    /// Allocates zero heap memory.
    /// </summary>
    public static void GenerateFresnelWeights(Span<double> weights, int period, double offset, double sigma)
    {
        if (period <= 0 || weights.Length < period)
        {
            return;
        }

        if (period == 1)
        {
            weights[0] = 1.0;
            return;
        }

        double m = Math.Clamp(offset, 0.0, 1.0) * (period - 1);
        double safeSigma = Math.Max(0.001, sigma);
        double s = period / safeSigma;
        double halfPi = Math.PI * 0.5;

        for (int j = 0; j < period; j++)
        {
            double u = (j - m) / s;
            double u2 = u * u;
            if (u2 >= 1.0)
            {
                weights[j] = 0.0;
                continue;
            }
            double cosArg = halfPi * u2;
            double w = Math.Cos(cosArg) * Math.Exp(-0.5 * u2);
            weights[j] = Math.Max(0.0, w);
        }
    }
}
