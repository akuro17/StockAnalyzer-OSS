using System;

namespace StockAnalyzer.Core.MathUtils;

/// <summary>
/// Special functions of the statistical tests: the standard normal distribution (cdf, two-sided p-value, quantile) and the chi-square upper tail.
/// Pure <c>double</c> functions of dimensionless arguments; the callers narrow the result with <c>IndicatorCalculationHelper.ToDecimalOrNull</c>.
/// Every function returns <see cref="double.NaN"/> for an argument outside its domain or for a NaN argument; it never throws.
/// <para>
/// Definitions (docs/indicators_formulas.md, record indicators_formulas_0594, subject SpecialFunctionsMath):
/// <c>Phi(z) = erfc(-z / sqrt 2) / 2</c> with erfc by W. J. Cody's rational Chebyshev approximation (Cody 1969, "Rational Chebyshev approximations for the error function",
/// Math. Comp. 23, 631-637; routine CALERF); <c>Phi^-1</c> by Acklam's rational approximation refined with exactly one Halley step;
/// the chi-square upper tail is the regularized upper incomplete gamma function <c>Q(df / 2, x / 2)</c> evaluated directly (never as <c>1 - P</c> in the region where Q is small).
/// </para>
/// </summary>
public static class SpecialFunctionsMath
{
    // ---- Cody (1969) erfc coefficients (CALERF). Three intervals: |x| <= 0.46875, 0.46875 < |x| <= 4, |x| > 4. ----
    private const double CodySmallLimit = 0.46875;
    private const double CodyMiddleLimit = 4.0;

    /// <summary>Argument beyond which <c>erfc(x)</c> underflows to 0 (Cody's XBIG); symmetric: <c>erfc(-x)</c> is 2 there.</summary>
    private const double CodyUnderflowLimit = 26.543;

    private const double InverseSqrtPi = 5.6418958354775628695e-1;
    private const double CodyTableStep = 16.0;

    private static readonly double[] CodyA =
    {
        3.16112374387056560e00, 1.13864154151050156e02, 3.77485237685302021e02, 3.20937758913846947e03, 1.85777706184603153e-1,
    };

    private static readonly double[] CodyB =
    {
        2.36012909523441209e01, 2.44024637934444173e02, 1.28261652607737228e03, 2.84423683343917062e03,
    };

    private static readonly double[] CodyC =
    {
        5.64188496988670089e-1, 8.88314979438837594e00, 6.61191906371416295e01, 2.98635138197400131e02, 8.81952221241769090e02,
        1.71204761263407058e03, 2.05107837782607147e03, 1.23033935479799725e03, 2.15311535474403846e-8,
    };

    private static readonly double[] CodyD =
    {
        1.57449261107098347e01, 1.17693950891312499e02, 5.37181101862009858e02, 1.62138957456669019e03, 3.29079923573345963e03,
        4.36261909014324716e03, 3.43936767414372164e03, 1.23033935480374942e03,
    };

    private static readonly double[] CodyP =
    {
        3.05326634961232344e-1, 3.60344899949804439e-1, 1.25781726111229246e-1, 1.60837851487422766e-2, 6.58749161529837803e-4,
        1.63153871373020978e-2,
    };

    private static readonly double[] CodyQ =
    {
        2.56852019228982242e00, 1.87295284992346725e00, 5.27905102951428412e-1, 6.05183413124413191e-2, 2.33520497626869185e-3,
    };

    // ---- Acklam (2003) rational approximation of the normal quantile, relative error 1.15e-9 before the Halley step. ----
    private const double AcklamLowTail = 0.02425;

    private static readonly double[] AcklamA =
    {
        -3.969683028665376e+01, 2.209460984245205e+02, -2.759285104469687e+02, 1.383577518672690e+02, -3.066479806614716e+01, 2.506628277459239e+00,
    };

    private static readonly double[] AcklamB =
    {
        -5.447609879822406e+01, 1.615858368580409e+02, -1.556989798598866e+02, 6.680131188771972e+01, -1.328068155288572e+01,
    };

    private static readonly double[] AcklamC =
    {
        -7.784894002430293e-03, -3.223964580411365e-01, -2.400758277161838e+00, -2.549732539343734e+00, 4.374664141464968e+00, 2.938163982698783e+00,
    };

    private static readonly double[] AcklamD =
    {
        7.784695709041462e-03, 3.224671290700398e-01, 2.445134137142996e+00, 3.754408661907416e+00,
    };

    // ---- Regularized incomplete gamma (series and modified Lentz continued fraction). ----
    private const int GammaMaxIterations = 200;
    private const double GammaRelativeTolerance = 1e-15;
    private const double GammaTiny = 1e-300;

    /// <summary>Complementary error function <c>erfc(x) = 1 - erf(x)</c>; relative error about 1e-16 on the whole line (also for large positive x, where <c>1 - erf</c> would lose all digits).</summary>
    public static double Erfc(double x)
    {
        if (double.IsNaN(x))
        {
            return double.NaN;
        }

        double y = Math.Abs(x);
        double result;
        if (y <= CodySmallLimit)
        {
            // erf(x) = x * P(x^2) / Q(x^2): odd in x, so the sign of x needs no extra handling.
            double ySquared = y > double.Epsilon ? y * y : 0.0;
            double numerator = CodyA[4] * ySquared;
            double denominator = ySquared;
            for (int i = 0; i < 3; i++)
            {
                numerator = (numerator + CodyA[i]) * ySquared;
                denominator = (denominator + CodyB[i]) * ySquared;
            }

            double erf = x * (numerator + CodyA[3]) / (denominator + CodyB[3]);
            return 1.0 - erf;
        }

        if (y >= CodyUnderflowLimit)
        {
            result = 0.0;
        }
        else
        {
            if (y <= CodyMiddleLimit)
            {
                double numerator = CodyC[8] * y;
                double denominator = y;
                for (int i = 0; i < 7; i++)
                {
                    numerator = (numerator + CodyC[i]) * y;
                    denominator = (denominator + CodyD[i]) * y;
                }

                result = (numerator + CodyC[7]) / (denominator + CodyD[7]);
            }
            else
            {
                double inverseSquare = 1.0 / (y * y);
                double numerator = CodyP[5] * inverseSquare;
                double denominator = inverseSquare;
                for (int i = 0; i < 4; i++)
                {
                    numerator = (numerator + CodyP[i]) * inverseSquare;
                    denominator = (denominator + CodyQ[i]) * inverseSquare;
                }

                result = inverseSquare * (numerator + CodyP[4]) / (denominator + CodyQ[4]);
                result = (InverseSqrtPi - result) / y;
            }

            // exp(-y^2) in two exactly representable factors, as in Cody's routine, so that the argument error stays below one unit in the last place.
            double truncated = Math.Truncate(y * CodyTableStep) / CodyTableStep;
            double delta = (y - truncated) * (y + truncated);
            result = Math.Exp(-truncated * truncated) * Math.Exp(-delta) * result;
        }

        return x < 0.0 ? 2.0 - result : result;
    }

    /// <summary>Standard normal cumulative distribution function <c>Phi(z) = erfc(-z / sqrt 2) / 2</c>.</summary>
    public static double NormalCdf(double z) => 0.5 * Erfc(-z / Math.Sqrt(2.0));

    /// <summary>Two-sided p-value of a standard normal statistic, <c>erfc(|z| / sqrt 2)</c> (not <c>2 (1 - Phi)</c>, which loses every digit in the tail).</summary>
    public static double NormalTwoSidedPValue(double z) => Erfc(Math.Abs(z) / Math.Sqrt(2.0));

    /// <summary>
    /// Standard normal quantile <c>Phi^-1(p)</c> for p in (0, 1); NaN for p &lt;= 0, p &gt;= 1 or NaN. Acklam's approximation followed by one Halley step
    /// <c>e = Phi(x) - p; u = e sqrt(2 pi) exp(x^2 / 2); x = x - u / (1 + x u / 2)</c>. The upper half is computed by symmetry from <c>1 - p</c>
    /// (exact for p &gt;= 0.5), so that the result keeps its relative accuracy for p close to 1.
    /// </summary>
    public static double NormalQuantile(double p)
    {
        if (!(p > 0.0 && p < 1.0))
        {
            return double.NaN;
        }

        if (p > 0.5)
        {
            return -NormalQuantile(1.0 - p);
        }

        double x;
        if (p < AcklamLowTail)
        {
            double q = Math.Sqrt(-2.0 * Math.Log(p));
            x = (((((AcklamC[0] * q + AcklamC[1]) * q + AcklamC[2]) * q + AcklamC[3]) * q + AcklamC[4]) * q + AcklamC[5])
                / ((((AcklamD[0] * q + AcklamD[1]) * q + AcklamD[2]) * q + AcklamD[3]) * q + 1.0);
        }
        else
        {
            double q = p - 0.5;
            double r = q * q;
            x = (((((AcklamA[0] * r + AcklamA[1]) * r + AcklamA[2]) * r + AcklamA[3]) * r + AcklamA[4]) * r + AcklamA[5]) * q
                / (((((AcklamB[0] * r + AcklamB[1]) * r + AcklamB[2]) * r + AcklamB[3]) * r + AcklamB[4]) * r + 1.0);
        }

        double error = NormalCdf(x) - p;
        double u = error * Math.Sqrt(2.0 * Math.PI) * Math.Exp(x * x / 2.0);
        return x - u / (1.0 + x * u / 2.0);
    }

    /// <summary>Upper tail of the chi-square distribution with <paramref name="degreesOfFreedom"/> degrees of freedom, <c>P(X &gt;= x) = Q(df / 2, x / 2)</c>; NaN for df &lt;= 0 or x &lt; 0.</summary>
    public static double ChiSquareUpperTail(double degreesOfFreedom, double x) => RegularizedGammaQ(degreesOfFreedom / 2.0, x / 2.0);

    /// <summary>
    /// Regularized upper incomplete gamma function <c>Q(a, x) = Gamma(a, x) / Gamma(a)</c>; NaN for a &lt;= 0, x &lt; 0, NaN, or when the iteration has not converged
    /// within 200 steps at relative tolerance 1e-15. For x &lt; a + 1 the lower function P is summed as the series <c>e^-x x^a sum x^n / Gamma(a + n + 1)</c> and Q = 1 - P;
    /// otherwise Q is the Legendre continued fraction by the modified Lentz method (the tail is evaluated directly, never as <c>1 - P</c>).
    /// </summary>
    public static double RegularizedGammaQ(double a, double x)
    {
        if (double.IsNaN(a) || double.IsNaN(x) || !(a > 0.0) || x < 0.0)
        {
            return double.NaN;
        }

        if (x == 0.0)
        {
            return 1.0;
        }

        if (double.IsPositiveInfinity(x))
        {
            return 0.0;
        }

        double logPrefactor = -x + a * Math.Log(x) - BocpdMath.LogGamma(a);
        if (x < a + 1.0)
        {
            double term = 1.0 / a;
            double sum = term;
            for (int n = 1; n <= GammaMaxIterations; n++)
            {
                term *= x / (a + n);
                sum += term;
                if (Math.Abs(term) < Math.Abs(sum) * GammaRelativeTolerance)
                {
                    return 1.0 - sum * Math.Exp(logPrefactor);
                }
            }

            return double.NaN;
        }

        double b = x + 1.0 - a;
        double c = 1.0 / GammaTiny;
        double d = 1.0 / b;
        double h = d;
        for (int i = 1; i <= GammaMaxIterations; i++)
        {
            double an = -i * (i - a);
            b += 2.0;
            d = an * d + b;
            if (Math.Abs(d) < GammaTiny)
            {
                d = GammaTiny;
            }

            c = b + an / c;
            if (Math.Abs(c) < GammaTiny)
            {
                c = GammaTiny;
            }

            d = 1.0 / d;
            double delta = d * c;
            h *= delta;
            if (Math.Abs(delta - 1.0) < GammaRelativeTolerance)
            {
                return Math.Exp(logPrefactor) * h;
            }
        }

        return double.NaN;
    }
}
