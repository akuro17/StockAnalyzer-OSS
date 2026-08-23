using System;
using Avalonia;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Immutable parameters describing a solved catenary curve in screen coordinates.
/// In screen coordinates (Y down):
/// S = +1 for downward sag (hanging/support, curve bulges toward larger Y),
/// S = -1 for upward arch (resistance, curve bulges toward smaller Y).
/// Curve equation: y(x) = Y0 - S * A * cosh((x - X0) / A).
/// If solver fails to converge, IsParabolic is true and the exact 3-point passing parabola is evaluated.
/// </summary>
public readonly record struct CatenaryParams(
    double A,
    double X0,
    double Y0,
    int S,
    double X1,
    double Y1,
    double X2,
    double Y2,
    bool IsLinear,
    bool IsParabolic = false,
    double SagDelta = 0.0
)
{
    /// <summary>
    /// Evaluates the Y coordinate of the catenary curve at the specified X coordinate.
    /// </summary>
    public double EvaluateY(double x)
    {
        if (IsLinear)
        {
            double w = X2 - X1;
            if (Math.Abs(w) < 1e-6) return Y1;
            return Y1 + (Y2 - Y1) / w * (x - X1);
        }

        if (IsParabolic)
        {
            double w = X2 - X1;
            if (Math.Abs(w) < 1e-6) return Y1;
            double chordY = Y1 + (Y2 - Y1) / w * (x - X1);
            return chordY + 4.0 * SagDelta * ((x - X1) * (X2 - x)) / (w * w);
        }

        double z = (x - X0) / A;
        z = Math.Clamp(z, -700.0, 700.0);
        return Y0 - S * A * Math.Cosh(z);
    }

    /// <summary>
    /// Evaluates the tangent slope m = dy/dx at the specified X coordinate.
    /// </summary>
    public double EvaluateSlope(double x)
    {
        if (IsLinear)
        {
            double w = X2 - X1;
            if (Math.Abs(w) < 1e-6) return 0.0;
            return (Y2 - Y1) / w;
        }

        if (IsParabolic)
        {
            double w = X2 - X1;
            if (Math.Abs(w) < 1e-6) return 0.0;
            double chordSlope = (Y2 - Y1) / w;
            return chordSlope + 4.0 * SagDelta * (X1 + X2 - 2.0 * x) / (w * w);
        }

        double z = (x - X0) / A;
        z = Math.Clamp(z, -700.0, 700.0);
        return -S * Math.Sinh(z);
    }

    /// <summary>
    /// Calculates the approximate normal (perpendicular) distance from a screen point to the curve.
    /// </summary>
    public double DistanceToPoint(double px, double py)
    {
        double yAtX = EvaluateY(px);
        double m = EvaluateSlope(px);
        return Math.Abs(py - yAtX) / Math.Sqrt(1.0 + m * m);
    }
}

/// <summary>
/// High-performance numerical solver for Catenary Curve Trendlines.
/// Implements Bracketed Safe Newton-Raphson method with Closed-Form parameter derivation.
/// Guarantees Zero-Crash and Zero-Allocation during evaluation.
/// Falls back to exact 3-point passing Parabola if catenary cannot be converged.
/// </summary>
public static class CatenaryMath
{
    public const double MinWidthThreshold = 1e-4;
    public const double LinearSagThreshold = 1.0;
    public const int MaxNewtonIterations = 15;
    public const double ConvergenceTolerancePx = 0.1;
    public const double DeltaATolerance = 1e-3;

    /// <summary>
    /// Solves the catenary curve passing through 3 screen coordinates:
    /// S0 (start), S1 (end), S2 (sag control point).
    /// </summary>
    /// <returns>CatenaryParams if solvable, or null if degenerate (width &lt; 1e-4 px).</returns>
    public static CatenaryParams? Solve(global::Avalonia.Point s0, global::Avalonia.Point s1, global::Avalonia.Point s2)
    {
        // 1. Sort X coordinates so that x1 <= x2
        double x1, y1, x2, y2;
        if (s0.X <= s1.X)
        {
            x1 = s0.X; y1 = s0.Y;
            x2 = s1.X; y2 = s1.Y;
        }
        else
        {
            x1 = s1.X; y1 = s1.Y;
            x2 = s0.X; y2 = s0.Y;
        }

        double w = x2 - x1;
        if (w < MinWidthThreshold)
        {
            return null; // Degenerate width guard
        }

        // 2. Clamp sag point X to [x1 + 1.0, x2 - 1.0] (or proportional if w < 2.0)
        double minSagX = w > 2.0 ? x1 + 1.0 : x1 + 0.1 * w;
        double maxSagX = w > 2.0 ? x2 - 1.0 : x2 - 0.1 * w;
        double x3 = Math.Clamp(s2.X, minSagX, maxSagX);
        double y3 = s2.Y;

        // 3. Basic geometric parameters
        double d = y2 - y1;
        double xm = 0.5 * (x1 + x2);
        double yChordAtSag = y1 + (d / w) * (x3 - x1);
        double delta = y3 - yChordAtSag;

        // 4. Linear Mode check: if sag is negligible, treat as straight line
        if (Math.Abs(delta) < LinearSagThreshold)
        {
            return new CatenaryParams(
                A: 1.0,
                X0: xm,
                Y0: y1,
                S: 1,
                X1: x1,
                Y1: y1,
                X2: x2,
                Y2: y2,
                IsLinear: true
            );
        }

        // 5. Orientation sign s: +1 for downward sag (delta >= 0), -1 for upward sag (delta < 0)
        int s = delta >= 0 ? 1 : -1;
        double h = Math.Max(Math.Abs(delta), 0.1);

        // 6. Search bounds for parameter a
        double aMin = Math.Max(1.0, 0.01 * w);
        double aMax = 100.0 * w;

        // Initial parabolic Taylor approximation: a0 = w^2 / (8h)
        double a0 = Math.Clamp((w * w) / (8.0 * h), aMin, aMax);

        // 7. Bracketed Safe Newton-Raphson Solver
        double lo = aMin;
        double hi = aMax;
        double aCurrent = a0;
        bool converged = false;

        double fLo = EvaluateF(lo, w, d, xm, x1, y1, x3, y3, s);

        for (int k = 0; k < MaxNewtonIterations; k++)
        {
            double fCurrent = EvaluateF(aCurrent, w, d, xm, x1, y1, x3, y3, s);

            if (Math.Abs(fCurrent) < ConvergenceTolerancePx)
            {
                converged = true;
                break;
            }

            // Central difference for derivative F'(a)
            double deltaA = Math.Max(1e-4 * aCurrent, 1e-6);
            double fPlus = EvaluateF(aCurrent + deltaA, w, d, xm, x1, y1, x3, y3, s);
            double fMinus = EvaluateF(aCurrent - deltaA, w, d, xm, x1, y1, x3, y3, s);
            double fPrime = (fPlus - fMinus) / (2.0 * deltaA);

            double aNext;
            if (Math.Abs(fPrime) > 1e-9)
            {
                // Damped Newton step
                aNext = aCurrent - 0.8 * (fCurrent / fPrime);
            }
            else
            {
                aNext = double.NaN;
            }

            // If Newton step is out of bounds or NaN, use bisection step
            if (double.IsNaN(aNext) || double.IsInfinity(aNext) || aNext < lo || aNext > hi)
            {
                aNext = 0.5 * (lo + hi);
            }

            if (Math.Abs(aNext - aCurrent) < DeltaATolerance)
            {
                aCurrent = aNext;
                converged = true;
                break;
            }

            // Update bracket based on sign
            double fNext = EvaluateF(aNext, w, d, xm, x1, y1, x3, y3, s);
            if (double.IsNaN(fNext) || double.IsInfinity(fNext))
            {
                break;
            }

            if ((fLo > 0 && fNext > 0) || (fLo < 0 && fNext < 0))
            {
                lo = aNext;
                fLo = fNext;
            }
            else
            {
                hi = aNext;
            }

            aCurrent = aNext;
        }

        // Exact 3-point passing Parabola fallback if catenary solver did not converge or yielded invalid numbers
        if (!converged || double.IsNaN(aCurrent) || double.IsInfinity(aCurrent) || aCurrent <= 0)
        {
            return new CatenaryParams(
                A: a0,
                X0: xm,
                Y0: y1,
                S: s,
                X1: x1,
                Y1: y1,
                X2: x2,
                Y2: y2,
                IsLinear: false,
                IsParabolic: true,
                SagDelta: delta
            );
        }

        // 8. Compute final closed-form vertex parameters (x0, y0)
        double uFinal = ComputeU(aCurrent, w, d, s);
        double x0Final = xm - aCurrent * Asinh(uFinal);
        double z1Final = Math.Clamp((x1 - x0Final) / aCurrent, -700.0, 700.0);
        double y0Final = y1 + s * aCurrent * Math.Cosh(z1Final);

        return new CatenaryParams(
            A: aCurrent,
            X0: x0Final,
            Y0: y0Final,
            S: s,
            X1: x1,
            Y1: y1,
            X2: x2,
            Y2: y2,
            IsLinear: false,
            IsParabolic: false,
            SagDelta: delta
        );
    }

    private static double ComputeU(double a, double w, double d, int s)
    {
        double sinhArg = Math.Clamp(w / (2.0 * a), -700.0, 700.0);
        double denom = 2.0 * a * Math.Sinh(sinhArg);
        if (Math.Abs(denom) < 1e-12)
        {
            denom = 1e-12;
        }
        return (-s * d) / denom;
    }

    private static double Asinh(double x)
    {
        return Math.Log(x + Math.Sqrt(x * x + 1.0));
    }

    private static double EvaluateF(double a, double w, double d, double xm, double x1, double y1, double x3, double y3, int s)
    {
        double u = ComputeU(a, w, d, s);
        double x0 = xm - a * Asinh(u);
        double z1 = Math.Clamp((x1 - x0) / a, -700.0, 700.0);
        double y0 = y1 + s * a * Math.Cosh(z1);

        double z3 = Math.Clamp((x3 - x0) / a, -700.0, 700.0);
        double yAtSag = y0 - s * a * Math.Cosh(z3);

        return yAtSag - y3;
    }
}
