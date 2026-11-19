using System;

namespace StockAnalyzer.Core.MathUtils;

/// <summary>
/// UI-independent 2D/3D analytic geometry and curve-sampling helpers (line intersection, point-to-plane distance,
/// and discontinuity-aware zero-allocation curve sampling) complementing <see cref="DslElementaryMath.PolygonArea"/>
/// and <see cref="DslLinearAlgebraMath"/>.
/// </summary>
public static class DslCurveMath
{
    public const int MinCurvePoints = 2;
    public const int MaxCurvePoints = 2_048;
    private const double ParallelDeterminantEpsilon = 1e-12;

    /// <summary>
    /// Uniformly samples <paramref name="f"/> on <c>[xMin, xMax]</c> into <paramref name="xs"/> and <paramref name="ys"/>
    /// (<c>xMin &lt; xMax</c>, <c>2 &lt;= xs.Length &lt;= 2,048</c>). Points that evaluate to non-finite values or
    /// domain/division/overflow errors are recorded as <see cref="double.NaN"/> gaps for segmented rendering;
    /// <see cref="MathErrorCode.BudgetExceeded"/> aborts immediately.
    /// </summary>
    public static MathErrorCode SampleCurve<TFunc>(
        TFunc f,
        double xMin,
        double xMax,
        scoped Span<double> xs,
        scoped Span<double> ys,
        ref DslExecutionBudget budget)
        where TFunc : struct, IScalarFunction
    {
        int nPts = xs.Length;
        if (nPts < MinCurvePoints || nPts > MaxCurvePoints || ys.Length < nPts)
        {
            return MathErrorCode.DimensionMismatch;
        }

        if (!double.IsFinite(xMin) || !double.IsFinite(xMax))
        {
            return MathErrorCode.NonFiniteResult;
        }

        if (!(xMin < xMax))
        {
            return MathErrorCode.DomainError;
        }

        double step = (xMax - xMin) / (nPts - 1);
        for (int i = 0; i < nPts; i++)
        {
            if (!budget.TryConsume(1))
            {
                return MathErrorCode.BudgetExceeded;
            }

            double x = i == nPts - 1 ? xMax : xMin + i * step;
            xs[i] = x == 0.0 ? 0.0 : x;

            budget.LastError = MathErrorCode.None;
            if (!f.TryEvaluate(x, ref budget, out double y))
            {
                if (budget.LastError == MathErrorCode.BudgetExceeded || budget.RemainingOps < 0)
                {
                    return MathErrorCode.BudgetExceeded;
                }

                ys[i] = double.NaN;
                budget.LastError = MathErrorCode.None;
                continue;
            }

            ys[i] = double.IsFinite(y) ? (y == 0.0 ? 0.0 : y) : double.NaN;
        }

        return MathErrorCode.None;
    }

    /// <summary>
    /// Uniformly samples <paramref name="f"/> into <see cref="DslPoint2D"/> (<see cref="decimal"/>) coordinates and
    /// a parallel validity mask (<paramref name="isValid"/>) for UI-independent chart rendering.
    /// </summary>
    public static MathErrorCode SampleCurve<TFunc>(
        TFunc f,
        double xMin,
        double xMax,
        scoped Span<DslPoint2D> points,
        scoped Span<bool> isValid,
        ref DslExecutionBudget budget)
        where TFunc : struct, IScalarFunction
    {
        int nPts = points.Length;
        if (nPts < MinCurvePoints || nPts > MaxCurvePoints || isValid.Length < nPts)
        {
            return MathErrorCode.DimensionMismatch;
        }

        Span<double> xs = stackalloc double[nPts];
        Span<double> ys = stackalloc double[nPts];
        MathErrorCode err = SampleCurve(f, xMin, xMax, xs, ys, ref budget);
        if (err != MathErrorCode.None)
        {
            return err;
        }

        for (int i = 0; i < nPts; i++)
        {
            MathResult<decimal> dx = DslElementaryMath.NarrowDouble(xs[i]);
            if (!dx.IsOk)
            {
                return dx.Error;
            }

            if (double.IsNaN(ys[i]))
            {
                points[i] = new DslPoint2D(dx.Value, 0m);
                isValid[i] = false;
                continue;
            }

            MathResult<decimal> dy = DslElementaryMath.NarrowDouble(ys[i]);
            if (dy.IsOk)
            {
                points[i] = new DslPoint2D(dx.Value, dy.Value);
                isValid[i] = true;
            }
            else
            {
                points[i] = new DslPoint2D(dx.Value, 0m);
                isValid[i] = false;
            }
        }

        return MathErrorCode.None;
    }

    /// <summary>
    /// Computes the intersection point of two 2D lines defined by pairs <c>(p1, p2)</c> and <c>(p3, p4)</c>.
    /// Returns <see cref="MathErrorCode.DomainError"/> if either direction segment has norm &lt; <c>1e-12</c>,
    /// and <see cref="MathErrorCode.DivisionByZero"/> if the lines are parallel or collinear.
    /// </summary>
    public static MathResult<DslPoint2D> LineIntersect2D(
        DslPoint2D p1,
        DslPoint2D p2,
        DslPoint2D p3,
        DslPoint2D p4)
    {
        double dx1 = (double)p2.X - (double)p1.X;
        double dy1 = (double)p2.Y - (double)p1.Y;
        double dx2 = (double)p4.X - (double)p3.X;
        double dy2 = (double)p4.Y - (double)p3.Y;

        double len1 = Math.Sqrt(dx1 * dx1 + dy1 * dy1);
        double len2 = Math.Sqrt(dx2 * dx2 + dy2 * dy2);
        if (!double.IsFinite(len1) || !double.IsFinite(len2))
        {
            return MathResult<DslPoint2D>.Fail(MathErrorCode.ArithmeticOverflow);
        }

        if (len1 < DslLinearAlgebraMath.ZeroNormEpsilon || len2 < DslLinearAlgebraMath.ZeroNormEpsilon)
        {
            return MathResult<DslPoint2D>.Fail(MathErrorCode.DomainError);
        }

        double det = dx1 * dy2 - dy1 * dx2;
        if (Math.Abs(det) < ParallelDeterminantEpsilon * len1 * len2)
        {
            return MathResult<DslPoint2D>.Fail(MathErrorCode.DivisionByZero);
        }

        // Shift origin to p1 for numerical stability on large coordinates.
        double rx = (double)p3.X - (double)p1.X;
        double ry = (double)p3.Y - (double)p1.Y;
        double t = (rx * dy2 - ry * dx2) / det;

        MathResult<decimal> ix = DslElementaryMath.NarrowDouble((double)p1.X + t * dx1);
        if (!ix.IsOk)
        {
            return MathResult<DslPoint2D>.Fail(ix.Error);
        }

        MathResult<decimal> iy = DslElementaryMath.NarrowDouble((double)p1.Y + t * dy1);
        if (!iy.IsOk)
        {
            return MathResult<DslPoint2D>.Fail(iy.Error);
        }

        return MathResult<DslPoint2D>.Ok(new DslPoint2D(ix.Value, iy.Value));
    }

    /// <summary>
    /// Computes the Euclidean distance from 3D point <c>(px, py, pz)</c> to plane
    /// <c>nx*x + ny*y + nz*z + d = 0</c>. Returns <see cref="MathErrorCode.DivisionByZero"/>
    /// when <c>||n|| &lt; 1e-12</c>.
    /// </summary>
    public static MathResult<decimal> PointToPlaneDistance(
        decimal px,
        decimal py,
        decimal pz,
        decimal nx,
        decimal ny,
        decimal nz,
        decimal d)
    {
        double dnx = (double)nx;
        double dny = (double)ny;
        double dnz = (double)nz;
        double maxN = Math.Max(Math.Abs(dnx), Math.Max(Math.Abs(dny), Math.Abs(dnz)));
        if (maxN == 0.0)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DivisionByZero);
        }

        double norm = maxN * Math.Sqrt((dnx / maxN) * (dnx / maxN) + (dny / maxN) * (dny / maxN) + (dnz / maxN) * (dnz / maxN));
        if (norm < DslLinearAlgebraMath.ZeroNormEpsilon)
        {
            return MathResult<decimal>.Fail(MathErrorCode.DivisionByZero);
        }

        double signedNumerator = dnx * (double)px + dny * (double)py + dnz * (double)pz + (double)d;
        return DslElementaryMath.NarrowDouble(Math.Abs(signedNumerator) / norm);
    }
}
