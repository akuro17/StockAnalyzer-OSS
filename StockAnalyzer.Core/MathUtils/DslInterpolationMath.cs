using System;

namespace StockAnalyzer.Core.MathUtils;

/// <summary>
/// Zero-allocation 1D interpolation of the DSL v5 (linear, natural cubic spline, monotone PCHIP). The grid must be strictly increasing
/// (every step above <see cref="MinStep"/>), and the query must lie inside <c>[xs[0], xs[n - 1]]</c>: there is no extrapolation.
/// A query that equals a grid node returns the node value exactly.
/// </summary>
public static class DslInterpolationMath
{
    /// <summary>Smallest accepted grid step; a step not above this value is a domain error.</summary>
    public const double MinStep = 1e-12;

    private const int MinPointsLinear = 2;
    private const int MinPointsSpline = 3;
    private const int MinPointsPchip = 2;
    private const double PchipEndpointSlopeLimit = 3.0;

    /// <summary>Piecewise linear interpolation (<c>n &gt;= 2</c>).</summary>
    public static MathResult<double> InterpLinear(scoped ReadOnlySpan<double> xs, scoped ReadOnlySpan<double> ys, double x)
    {
        MathErrorCode err = Locate(xs, ys, x, MinPointsLinear, out int i, out double exact, out bool isNode);
        if (err != MathErrorCode.None)
        {
            return MathResult<double>.Fail(err);
        }

        if (isNode)
        {
            return MathResult<double>.Ok(exact);
        }

        double h = xs[i + 1] - xs[i];
        return Finite(ys[i] + ((x - xs[i]) / h * (ys[i + 1] - ys[i])));
    }

    /// <summary>
    /// Natural cubic spline (<c>n &gt;= 3</c>, <c>S'' = 0</c> at both ends) solved with the Thomas algorithm and evaluated as
    /// <c>y_i + b t + M_i t^2 / 2 + (M_(i+1) - M_i) t^3 / (6 h)</c> with <c>t = x - x_i</c>.
    /// </summary>
    public static MathResult<double> InterpCubicSpline(scoped ReadOnlySpan<double> xs, scoped ReadOnlySpan<double> ys, double x)
    {
        MathErrorCode err = Locate(xs, ys, x, MinPointsSpline, out int i, out double exact, out bool isNode);
        if (err != MathErrorCode.None)
        {
            return MathResult<double>.Fail(err);
        }

        if (isNode)
        {
            return MathResult<double>.Ok(exact);
        }

        int n = xs.Length;
        Span<double> moments = stackalloc double[n];
        Span<double> upper = stackalloc double[n];
        Span<double> rhs = stackalloc double[n];
        moments.Clear();

        // Interior equations: h_(k-1) M_(k-1) + 2 (h_(k-1) + h_k) M_k + h_k M_(k+1) = 6 (delta_k - delta_(k-1)).
        for (int k = 1; k < n - 1; k++)
        {
            double hPrev = xs[k] - xs[k - 1];
            double hNext = xs[k + 1] - xs[k];
            double diagonal = 2.0 * (hPrev + hNext);
            double right = 6.0 * (((ys[k + 1] - ys[k]) / hNext) - ((ys[k] - ys[k - 1]) / hPrev));
            if (k == 1)
            {
                upper[k] = hNext / diagonal;
                rhs[k] = right / diagonal;
            }
            else
            {
                double denominator = diagonal - (hPrev * upper[k - 1]);
                upper[k] = hNext / denominator;
                rhs[k] = (right - (hPrev * rhs[k - 1])) / denominator;
            }
        }

        moments[n - 2] = rhs[n - 2];
        for (int k = n - 3; k >= 1; k--)
        {
            moments[k] = rhs[k] - (upper[k] * moments[k + 1]);
        }

        double step = xs[i + 1] - xs[i];
        double t = x - xs[i];
        double slope = ((ys[i + 1] - ys[i]) / step) - (step * ((2.0 * moments[i]) + moments[i + 1]) / 6.0);
        double value = ys[i] + (t * (slope + (t * ((0.5 * moments[i]) + (t * (moments[i + 1] - moments[i]) / (6.0 * step))))));
        return Finite(value);
    }

    /// <summary>
    /// Fritsch-Carlson monotone piecewise cubic Hermite interpolation (<c>n &gt;= 2</c>; two points reduce to linear interpolation):
    /// weighted harmonic mean interior slopes, three-point one-sided end slopes limited to preserve monotonicity.
    /// </summary>
    public static MathResult<double> InterpPchip(scoped ReadOnlySpan<double> xs, scoped ReadOnlySpan<double> ys, double x)
    {
        MathErrorCode err = Locate(xs, ys, x, MinPointsPchip, out int i, out double exact, out bool isNode);
        if (err != MathErrorCode.None)
        {
            return MathResult<double>.Fail(err);
        }

        if (isNode)
        {
            return MathResult<double>.Ok(exact);
        }

        int n = xs.Length;
        double h = xs[i + 1] - xs[i];
        if (n == 2)
        {
            return Finite(ys[i] + ((x - xs[i]) / h * (ys[i + 1] - ys[i])));
        }

        double d0 = PchipSlope(xs, ys, i);
        double d1 = PchipSlope(xs, ys, i + 1);
        double s = (x - xs[i]) / h;
        double s2 = s * s;
        double s3 = s2 * s;
        double value = (((2.0 * s3) - (3.0 * s2) + 1.0) * ys[i])
            + ((s3 - (2.0 * s2) + s) * h * d0)
            + (((-2.0 * s3) + (3.0 * s2)) * ys[i + 1])
            + ((s3 - s2) * h * d1);
        return Finite(value);
    }

    private static double PchipSlope(scoped ReadOnlySpan<double> xs, scoped ReadOnlySpan<double> ys, int k)
    {
        int n = xs.Length;
        if (k == 0)
        {
            double h0 = xs[1] - xs[0];
            double h1 = xs[2] - xs[1];
            return EndpointSlope(h0, h1, (ys[1] - ys[0]) / h0, (ys[2] - ys[1]) / h1);
        }

        if (k == n - 1)
        {
            double hLast = xs[n - 1] - xs[n - 2];
            double hPrev = xs[n - 2] - xs[n - 3];
            return EndpointSlope(hLast, hPrev, (ys[n - 1] - ys[n - 2]) / hLast, (ys[n - 2] - ys[n - 3]) / hPrev);
        }

        double hLeft = xs[k] - xs[k - 1];
        double hRight = xs[k + 1] - xs[k];
        double deltaLeft = (ys[k] - ys[k - 1]) / hLeft;
        double deltaRight = (ys[k + 1] - ys[k]) / hRight;
        if (deltaLeft * deltaRight <= 0.0)
        {
            return 0.0;
        }

        double w1 = (2.0 * hRight) + hLeft;
        double w2 = hRight + (2.0 * hLeft);
        return (w1 + w2) / ((w1 / deltaLeft) + (w2 / deltaRight));
    }

    /// <summary>Non-centered three-point end slope with the Fritsch-Carlson sign and magnitude limits; <c>hNear</c>/<c>deltaNear</c> belong to the end interval.</summary>
    private static double EndpointSlope(double hNear, double hFar, double deltaNear, double deltaFar)
    {
        double d = (((2.0 * hNear) + hFar) * deltaNear - (hNear * deltaFar)) / (hNear + hFar);
        if (Math.Sign(d) != Math.Sign(deltaNear))
        {
            return 0.0;
        }

        if (Math.Sign(deltaNear) != Math.Sign(deltaFar) && Math.Abs(d) > PchipEndpointSlopeLimit * Math.Abs(deltaNear))
        {
            return PchipEndpointSlopeLimit * deltaNear;
        }

        return d;
    }

    /// <summary>Validates the grid and finds the interval <c>[xs[i], xs[i + 1]]</c> containing <paramref name="x"/> (binary search).</summary>
    private static MathErrorCode Locate(
        scoped ReadOnlySpan<double> xs,
        scoped ReadOnlySpan<double> ys,
        double x,
        int minPoints,
        out int interval,
        out double nodeValue,
        out bool isNode)
    {
        interval = 0;
        nodeValue = 0.0;
        isNode = false;
        if (xs.Length != ys.Length)
        {
            return MathErrorCode.DimensionMismatch;
        }

        int n = xs.Length;
        if (n < minPoints)
        {
            return MathErrorCode.DomainError;
        }

        for (int k = 0; k < n - 1; k++)
        {
            if (!(xs[k + 1] - xs[k] > MinStep))
            {
                return MathErrorCode.DomainError;
            }
        }

        if (!double.IsFinite(x) || x < xs[0] || x > xs[n - 1])
        {
            return MathErrorCode.DomainError;
        }

        int lo = 0;
        int hi = n - 2;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (xs[mid] <= x)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }

        interval = lo;
        if (x == xs[lo])
        {
            isNode = true;
            nodeValue = ys[lo];
        }
        else if (x == xs[lo + 1])
        {
            isNode = true;
            nodeValue = ys[lo + 1];
        }

        return MathErrorCode.None;
    }

    private static MathResult<double> Finite(double value)
        => double.IsFinite(value)
            ? MathResult<double>.Ok(value == 0.0 ? 0.0 : value)
            : MathResult<double>.Fail(MathErrorCode.NonFiniteResult);

    // ---- DSL v6: 2D Bilinear Interpolation ----

    /// <summary>
    /// Bilinear interpolation on a strictly increasing rectilinear grid (<c>xs.Length &gt;= 2</c>, <c>ys.Length &gt;= 2</c>)
    /// with row-major values <c>Z[j * xs.Length + i] = f(xs[i], ys[j])</c>. Extrapolation outside <c>[xs[0], xs[^1]] x [ys[0], ys[^1]]</c>
    /// returns <see cref="MathErrorCode.DomainError"/>.
    /// </summary>
    public static MathResult<double> Interp2DBilinear(
        scoped ReadOnlySpan<double> xs,
        scoped ReadOnlySpan<double> ys,
        scoped ReadOnlySpan<double> z,
        double x,
        double y)
    {
        int m = xs.Length;
        int n = ys.Length;
        if (m < 2 || n < 2)
        {
            return MathResult<double>.Fail(MathErrorCode.DomainError);
        }

        if (z.Length != m * n)
        {
            return MathResult<double>.Fail(MathErrorCode.DimensionMismatch);
        }

        if (!double.IsFinite(x) || !double.IsFinite(y))
        {
            return MathResult<double>.Fail(MathErrorCode.NonFiniteResult);
        }

        if (!TryValidateStrictlyIncreasing(xs) || !TryValidateStrictlyIncreasing(ys))
        {
            return MathResult<double>.Fail(MathErrorCode.DomainError);
        }

        for (int k = 0; k < z.Length; k++)
        {
            if (!double.IsFinite(z[k]))
            {
                return MathResult<double>.Fail(MathErrorCode.NonFiniteResult);
            }
        }

        if (x < xs[0] || x > xs[m - 1] || y < ys[0] || y > ys[n - 1])
        {
            return MathResult<double>.Fail(MathErrorCode.DomainError);
        }

        int i = LocateInterval(xs, x);
        int j = LocateInterval(ys, y);

        double u = (x - xs[i]) / (xs[i + 1] - xs[i]);
        double v = (y - ys[j]) / (ys[j + 1] - ys[j]);

        double z00 = z[j * m + i];
        double z01 = z[j * m + (i + 1)];
        double z10 = z[(j + 1) * m + i];
        double z11 = z[(j + 1) * m + (i + 1)];

        double val = ((1.0 - u) * (1.0 - v) * z00)
            + (u * (1.0 - v) * z01)
            + ((1.0 - u) * v * z10)
            + (u * v * z11);
        return Finite(val);
    }

    private static bool TryValidateStrictlyIncreasing(ReadOnlySpan<double> grid)
    {
        for (int i = 0; i < grid.Length; i++)
        {
            if (!double.IsFinite(grid[i]))
            {
                return false;
            }
        }

        for (int i = 0; i < grid.Length - 1; i++)
        {
            if (!(grid[i + 1] - grid[i] > MinStep))
            {
                return false;
            }
        }

        return true;
    }

    private static int LocateInterval(ReadOnlySpan<double> grid, double q)
    {
        int lo = 0;
        int hi = grid.Length - 2;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (grid[mid] <= q)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return lo;
    }
}
