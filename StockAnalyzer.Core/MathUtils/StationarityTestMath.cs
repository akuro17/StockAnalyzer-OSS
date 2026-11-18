using System;

namespace StockAnalyzer.Core.MathUtils;

/// <summary>
/// Result of one augmented Dickey-Fuller regression. <see cref="Tau"/> is NaN where the regression has no positive residual variance (perfect fit);
/// <see cref="HalfLife"/> is NaN unless <c>-1 &lt; Rho &lt; 0</c>. <see cref="CriticalValue5Percent"/> depends only on <see cref="EffectiveSampleSize"/>.
/// </summary>
public readonly record struct AugmentedDickeyFullerResult(double Tau, double Rho, double HalfLife, double CriticalValue5Percent, int EffectiveSampleSize);

/// <summary>
/// Augmented Dickey-Fuller unit-root test with a constant and <c>p</c> lagged differences, the MacKinnon (2010) 5 % critical value and the mean-reversion half-life.
/// Regression on the window <c>y</c> (0-based): <c>dy_t = c + rho y_(t-1) + sum_(i=1..p) phi_i dy_(t-i) + e_t</c> for <c>t = p + 1 .. W - 1</c>
/// (<c>T = W - p - 1</c> rows, <c>k = p + 2</c> parameters); <c>tau = rho / sqrt(sigma^2 c_rho)</c> with <c>sigma^2 = SSR / (T - k)</c> and <c>c_rho = [(X^T X)^-1]_(rho rho)</c>.
/// <c>double</c> arithmetic for a dimensionless statistic; the caller narrows with <c>IndicatorCalculationHelper.ToDecimalOrNull</c>.
/// Specification: docs/indicators_formulas.md, record indicators_formulas_0598 (this is a new kernel; the no-constant regression of the Cointegration indicator is unchanged).
/// </summary>
public static class StationarityTestMath
{
    // MacKinnon (2010), "Critical Values for Cointegration Tests", Queen's Economics Department Working Paper 1227, response surface
    // C(T) = b_inf + b_1 / T + b_2 / T^2 + b_3 / T^3 for the 5 % level, N = 1 (a single variable), constant and no trend (statsmodels tsa/adfvalues.py, tau_c_2010).
    // Owner decision U-9 = A: the four-term form; only the 5 % row is stored (1 % and 10 % have no consumer).
    // Source check (2026-11-18): the four numbers equal the N = 1, 5 % row of tau_c_2010 in the published statsmodels file tsa/adfvalues.py, as read in two mirrors of that file
    // (the statsmodels package on gemfury, and the tradestats crate's mackinnon.rs, which cites it). The primary table of Queen's Economics WP 1227 itself was NOT compared.
    public const double CriticalValue5PercentInfinity = -2.86154;
    public const double CriticalValue5PercentInverse1 = -2.8903;
    public const double CriticalValue5PercentInverse2 = -4.234;
    public const double CriticalValue5PercentInverse3 = -40.040;

    /// <summary>Largest number of lagged differences (a scratch matrix of <c>W x 12</c> at most).</summary>
    public const int MaxLagOrder = 10;

    /// <summary>Parameters of the regression: constant, lagged level and <paramref name="lagOrder"/> lagged differences.</summary>
    public static int ParameterCount(int lagOrder) => lagOrder + 2;

    /// <summary>Rows of the regression, <c>W - p - 1</c>.</summary>
    public static int EffectiveSampleSize(int windowLength, int lagOrder) => windowLength - lagOrder - 1;

    /// <summary>Shortest window with at least two residual degrees of freedom: <c>T &gt;= k + 2</c>, i.e. <c>W &gt;= 2 p + 5</c>.</summary>
    public static int MinWindowLength(int lagOrder) => 2 * lagOrder + 5;

    /// <summary>Elements of scratch memory <see cref="TryAugmentedDickeyFuller"/> needs: the matrix, the right-hand side, the solution and one work row.</summary>
    public static int ScratchLength(int windowLength, int lagOrder)
    {
        int rows = EffectiveSampleSize(windowLength, lagOrder);
        int columns = ParameterCount(lagOrder);
        return rows * columns + rows + 2 * columns;
    }

    /// <summary>MacKinnon (2010) 5 % critical value for <paramref name="effectiveSampleSize"/> regression rows (NaN for a size below 1).</summary>
    public static double CriticalValue5Percent(int effectiveSampleSize)
    {
        if (effectiveSampleSize < 1)
        {
            return double.NaN;
        }

        double inverse = 1.0 / effectiveSampleSize;
        return CriticalValue5PercentInfinity
            + inverse * (CriticalValue5PercentInverse1 + inverse * (CriticalValue5PercentInverse2 + inverse * CriticalValue5PercentInverse3));
    }

    /// <summary>
    /// Half-life in bars of an AR(1) with coefficient <c>1 + rho</c>: <c>-ln 2 / ln(1 + rho)</c>, defined for <c>-1 &lt; rho &lt; 0</c>
    /// and not within <paramref name="logEpsilon"/> of <c>ln(1 + rho) = 0</c>; NaN otherwise.
    /// </summary>
    public static double HalfLife(double rho, double logEpsilon)
    {
        if (!(rho > -1.0 && rho < 0.0))
        {
            return double.NaN;
        }

        double log = Math.Log(1.0 + rho);
        if (!double.IsFinite(log) || Math.Abs(log) <= logEpsilon)
        {
            return double.NaN;
        }

        return -Math.Log(2.0) / log;
    }

    /// <summary>
    /// Runs the regression on <paramref name="window"/> (levels: log prices or prices). False when the window is shorter than <see cref="MinWindowLength"/>,
    /// has a non-finite value, <paramref name="lagOrder"/> is outside 0..<see cref="MaxLagOrder"/>, <paramref name="scratch"/> is shorter than <see cref="ScratchLength"/>,
    /// or the regression is rank deficient (<see cref="SmallMatrixMath.RankTolerance"/>). A true result may still have a NaN <see cref="AugmentedDickeyFullerResult.Tau"/>
    /// (perfect fit: the residual norm is below <see cref="SmallMatrixMath.RankTolerance"/> times the norm of the dependent variable, the floating-point form of SSR = 0)
    /// while <see cref="AugmentedDickeyFullerResult.Rho"/> and <see cref="AugmentedDickeyFullerResult.HalfLife"/> are returned. Allocates nothing.
    /// </summary>
    public static bool TryAugmentedDickeyFuller(ReadOnlySpan<double> window, int lagOrder, double halfLifeLogEpsilon, Span<double> scratch, out AugmentedDickeyFullerResult result)
    {
        result = new AugmentedDickeyFullerResult(double.NaN, double.NaN, double.NaN, double.NaN, 0);
        int w = window.Length;
        if (lagOrder < 0 || lagOrder > MaxLagOrder || w < MinWindowLength(lagOrder) || scratch.Length < ScratchLength(w, lagOrder))
        {
            return false;
        }

        int rows = EffectiveSampleSize(w, lagOrder);
        int columns = ParameterCount(lagOrder);
        Span<double> matrix = scratch.Slice(0, rows * columns);
        Span<double> rhs = scratch.Slice(rows * columns, rows);
        Span<double> solution = scratch.Slice(rows * columns + rows, columns);
        Span<double> work = scratch.Slice(rows * columns + rows + columns, columns);

        double dependentNormSquared = 0.0;
        for (int row = 0; row < rows; row++)
        {
            int t = row + lagOrder + 1;
            double current = window[t];
            double previous = window[t - 1];
            double dependent = current - previous;
            if (!double.IsFinite(dependent) || !double.IsFinite(previous))
            {
                return false;
            }

            rhs[row] = dependent;
            dependentNormSquared += dependent * dependent;
            int offset = row * columns;
            matrix[offset] = 1.0;
            matrix[offset + 1] = previous;
            for (int lag = 1; lag <= lagOrder; lag++)
            {
                matrix[offset + 1 + lag] = window[t - lag] - window[t - lag - 1];
            }
        }

        if (!SmallMatrixMath.TryHouseholderQr(matrix, rows, columns, rhs, out double ssr))
        {
            return false;
        }

        SmallMatrixMath.SolveUpperTriangular(matrix, columns, rhs, solution);
        double rho = solution[1];
        double halfLife = HalfLife(rho, halfLifeLogEpsilon);
        double critical = CriticalValue5Percent(rows);
        double tau = double.NaN;
        double perfectFitLimit = SmallMatrixMath.RankTolerance * SmallMatrixMath.RankTolerance * dependentNormSquared;
        if (ssr > perfectFitLimit)
        {
            double variance = ssr / (rows - columns);
            double cRho = SmallMatrixMath.InverseNormalDiagonal(matrix, columns, 1, work);
            double product = variance * cRho;
            if (double.IsFinite(product) && product > 0.0)
            {
                double candidate = rho / Math.Sqrt(product);
                if (double.IsFinite(candidate))
                {
                    tau = candidate;
                }
            }
        }

        if (!double.IsFinite(rho))
        {
            return false;
        }

        result = new AugmentedDickeyFullerResult(tau, rho, halfLife, critical, rows);
        return true;
    }
}
