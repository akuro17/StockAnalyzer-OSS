using System;
using System.Runtime.CompilerServices;

namespace StockAnalyzer.Core.MathUtils;

/// <summary>
/// High-precision, zero-allocation numerical evaluator for Fresnel Cosine C(u) and Sine S(u) integrals.
/// Definitions:
///   C(u) = \int_0^u \cos(\frac{\pi}{2} t^2) dt
///   S(u) = \int_0^u \sin(\frac{\pi}{2} t^2) dt
/// Uses analytical term-ratio recurrence relations for Taylor series expansions (|u| &lt;= 4.0),
/// and asymptotic auxiliary rational expansions for larger arguments (|u| &gt; 4.0); infinite arguments return the limits +-0.5.
/// Measured maximum absolute error against a 28-digit decimal Taylor oracle (FresnelMathAccuracyTests, grid step 0.01, |u| &lt;= 5):
/// 7.2e-8 on the Taylor branch (cancellation of terms up to ~5e8 near u = 4) and 4.7e-8 on the asymptotic branch. Larger |u| is not measured.
/// Zero heap allocation.
/// </summary>
public static class FresnelMath
{
    private const double PiOver2 = Math.PI / 2.0;

    /// <summary>
    /// Evaluates both Fresnel Cosine C(u) and Sine S(u) integrals simultaneously.
    /// Allocates zero heap memory.
    /// </summary>
    /// <param name="u">Input real parameter.</param>
    /// <returns>Tuple (C, S) representing C(u) and S(u).</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static (double C, double S) Evaluate(double u)
    {
        if (double.IsNaN(u))
        {
            return (double.NaN, double.NaN);
        }

        if (u == 0.0)
        {
            return (0.0, 0.0);
        }

        // Limits of the integrals: C(+-inf) = S(+-inf) = +-1/2.
        if (double.IsInfinity(u))
        {
            double limit = u > 0.0 ? 0.5 : -0.5;
            return (limit, limit);
        }

        // Fresnel integrals are odd functions: C(-u) = -C(u), S(-u) = -S(u)
        bool isNegative = u < 0.0;
        double absU = isNegative ? -u : u;

        double c, s;
        if (absU <= 4.0)
        {
            EvaluateTaylor(absU, out c, out s);
        }
        else
        {
            EvaluateAsymptotic(absU, out c, out s);
        }

        return isNegative ? (-c, -s) : (c, s);
    }

    /// <summary>
    /// Evaluates the Fresnel Cosine integral C(u).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double CosineIntegral(double u) => Evaluate(u).C;

    /// <summary>
    /// Evaluates the Fresnel Sine integral S(u).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double SineIntegral(double u) => Evaluate(u).S;

    /// <summary>
    /// High-precision Taylor series evaluation via recurrence ratio:
    /// T_n = T_{n-1} * [ -(Pi/2 * u^2)^2 / (2n * (2n - 1)) ] * [ (4n - 3) / (4n + 1) ]
    /// U_n = U_{n-1} * [ -(Pi/2 * u^2)^2 / ((2n + 1) * 2n) ] * [ (4n - 1) / (4n + 3) ]
    /// </summary>
    private static void EvaluateTaylor(double u, out double c, out double s)
    {
        double u2 = u * u;
        double arg = PiOver2 * u2;
        double argSq = arg * arg;

        // C(u) summation
        double termC = u;
        c = termC;

        for (int n = 1; n <= 60; n++)
        {
            int twoN = 2 * n;
            double ratio = -argSq / (twoN * (twoN - 1)) * (4 * n - 3) / (4 * n + 1);
            termC *= ratio;
            c += termC;

            if (Math.Abs(termC) < 1e-15 * Math.Abs(c))
            {
                break;
            }
        }

        // S(u) summation
        double termS = PiOver2 * u2 * u / 3.0;
        s = termS;

        for (int n = 1; n <= 60; n++)
        {
            int twoN = 2 * n;
            double ratio = -argSq / ((twoN + 1) * twoN) * (4 * n - 1) / (4 * n + 3);
            termS *= ratio;
            s += termS;

            if (Math.Abs(termS) < 1e-15 * Math.Abs(s))
            {
                break;
            }
        }
    }

    /// <summary>
    /// Asymptotic auxiliary function evaluation for |u| &gt; 4.0:
    /// C(u) = 1/2 + f(u) \sin(\pi u^2 / 2) - g(u) \cos(\pi u^2 / 2)
    /// S(u) = 1/2 - f(u) \cos(\pi u^2 / 2) - g(u) \sin(\pi u^2 / 2)
    /// </summary>
    private static void EvaluateAsymptotic(double u, out double c, out double s)
    {
        double u2 = u * u;
        double arg = PiOver2 * u2;
        double sinArg = Math.Sin(arg);
        double cosArg = Math.Cos(arg);

        double w = Math.PI * u2;
        double invW2 = 1.0 / (w * w);

        // Abramowitz & Stegun 7.3.27:
        // f(x) ~ (1 / (\pi x)) * (1 - 3 / w^2 + 105 / w^4)
        // g(x) ~ (1 / (\pi x w)) * (1 - 15 / w^2 + 945 / w^4)
        double invPiU = 1.0 / (Math.PI * u);
        double f = invPiU * (1.0 - 3.0 * invW2 + 105.0 * invW2 * invW2);
        double g = (invPiU / w) * (1.0 - 15.0 * invW2 + 945.0 * invW2 * invW2);

        c = 0.5 + f * sinArg - g * cosArg;
        s = 0.5 - f * cosArg - g * sinArg;
    }
}
