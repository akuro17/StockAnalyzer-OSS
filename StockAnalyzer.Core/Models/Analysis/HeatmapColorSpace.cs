using System;

namespace StockAnalyzer.Core.Models.Analysis;

/// <summary>
/// Oklab perceptual colour (Bjorn Ottosson, 2020). Platform-agnostic single source of truth for heatmap colour math.
/// Output unit: dimensionless Oklab coordinates (L in [0, 1] for in-gamut sRGB).
/// Conversion chain: linear sRGB -> LMS (matrix M1) -> cube root -> LMS' -> Oklab (matrix M2); the inverse uses
/// the inverse of M2, a cube, and the inverse of M1. The coefficients below are the published Oklab values.
/// </summary>
public readonly record struct OklabColor(double L, double A, double B)
{
    private const double ByteMax = 255.0;
    private const double SrgbLinearThreshold = 0.04045;
    private const double SrgbLinearDivisor = 12.92;
    private const double SrgbOffset = 0.055;
    private const double SrgbScale = 1.055;
    private const double SrgbGamma = 2.4;
    private const double LinearEncodeThreshold = 0.0031308;
    private const int GamutSearchIterations = 16;

    /// <summary>Builds an Oklab colour from gamma-encoded sRGB bytes.</summary>
    public static OklabColor FromRgb(byte r, byte g, byte b)
    {
        double rl = SrgbToLinear(r / ByteMax);
        double gl = SrgbToLinear(g / ByteMax);
        double bl = SrgbToLinear(b / ByteMax);

        // Linear sRGB -> LMS (M1), cube root, then LMS' -> Oklab (M2).
        double l = Math.Cbrt((0.4122214708 * rl) + (0.5363325363 * gl) + (0.0514459929 * bl));
        double m = Math.Cbrt((0.2119034982 * rl) + (0.6806995451 * gl) + (0.1073969566 * bl));
        double s = Math.Cbrt((0.0883024619 * rl) + (0.2817188376 * gl) + (0.6299787005 * bl));

        return new OklabColor(
            (0.2104542553 * l) + (0.7936177850 * m) - (0.0040720468 * s),
            (1.9779984951 * l) - (2.4285922050 * m) + (0.4505937099 * s),
            (0.0259040371 * l) + (0.7827717662 * m) - (0.8086757660 * s));
    }

    /// <summary>Builds an Oklab colour from an <see cref="IndicatorColor"/>; alpha is ignored.</summary>
    public static OklabColor FromIndicatorColor(IndicatorColor c) => FromRgb(c.R, c.G, c.B);

    /// <summary>Linear interpolation on (L, A, B).</summary>
    public static OklabColor Lerp(OklabColor c1, OklabColor c2, double t) =>
        new(c1.L + (t * (c2.L - c1.L)), c1.A + (t * (c2.A - c1.A)), c1.B + (t * (c2.B - c1.B)));

    /// <summary>Euclidean Oklab distance.</summary>
    public static double DeltaE(OklabColor c1, OklabColor c2)
    {
        double dl = c1.L - c2.L;
        double da = c1.A - c2.A;
        double db = c1.B - c2.B;
        return Math.Sqrt((dl * dl) + (da * da) + (db * db));
    }

    /// <summary>
    /// Converts to sRGB. Out-of-gamut colours are brought into gamut by scaling chroma toward (L, 0, 0),
    /// preserving lightness and hue instead of clipping channels independently.
    /// </summary>
    public IndicatorColor ToIndicatorColorInGamut(byte alpha = 255)
    {
        if (!double.IsFinite(L) || !double.IsFinite(A) || !double.IsFinite(B) || L <= 0.0)
        {
            return new IndicatorColor(alpha, 0, 0, 0);
        }

        if (L >= 1.0)
        {
            return new IndicatorColor(alpha, 255, 255, 255);
        }

        var (rLin, gLin, bLin) = ToLinearRgb(L, A, B);
        if (!IsInUnitCube(rLin, gLin, bLin))
        {
            double lo = 0.0;
            double hi = 1.0;
            double best = 0.0;
            for (int i = 0; i < GamutSearchIterations; i++)
            {
                double mid = 0.5 * (lo + hi);
                var (r, g, b) = ToLinearRgb(L, mid * A, mid * B);
                if (IsInUnitCube(r, g, b))
                {
                    best = mid;
                    lo = mid;
                }
                else
                {
                    hi = mid;
                }
            }

            (rLin, gLin, bLin) = ToLinearRgb(L, best * A, best * B);
        }

        return new IndicatorColor(alpha, EncodeChannel(rLin), EncodeChannel(gLin), EncodeChannel(bLin));
    }

    private static (double R, double G, double B) ToLinearRgb(double l, double a, double b)
    {
        // Oklab -> LMS' (inverse M2), cube, then LMS -> linear sRGB (inverse M1).
        double lc = l + (0.3963377774 * a) + (0.2158037573 * b);
        double mc = l - (0.1055613458 * a) - (0.0638541728 * b);
        double sc = l - (0.0894841775 * a) - (1.2914855480 * b);

        double lp = lc * lc * lc;
        double mp = mc * mc * mc;
        double sp = sc * sc * sc;

        return (
            (+4.0767416621 * lp) - (3.3077115913 * mp) + (0.2309699292 * sp),
            (-1.2684380046 * lp) + (2.6097574011 * mp) - (0.3413193965 * sp),
            (-0.0041960863 * lp) - (0.7034186147 * mp) + (1.7076147010 * sp));
    }

    private static bool IsInUnitCube(double r, double g, double b) =>
        r >= 0.0 && r <= 1.0 && g >= 0.0 && g <= 1.0 && b >= 0.0 && b <= 1.0;

    private static byte EncodeChannel(double linear)
    {
        double encoded = LinearToSrgb(Math.Clamp(linear, 0.0, 1.0));
        return (byte)Math.Clamp((int)Math.Round(encoded * ByteMax), 0, 255);
    }

    // Re-evaluate: extract the sRGB transfer functions into a dedicated SrgbTransfer type as soon as a second file needs them.
    internal static double SrgbToLinear(double c) =>
        c <= SrgbLinearThreshold ? c / SrgbLinearDivisor : Math.Pow((c + SrgbOffset) / SrgbScale, SrgbGamma);

    private static double LinearToSrgb(double c) =>
        c <= LinearEncodeThreshold ? c * SrgbLinearDivisor : (SrgbScale * Math.Pow(c, 1.0 / SrgbGamma)) - SrgbOffset;
}

/// <summary>
/// Neutral-to-peak heatmap colour ramp. The Neutral colour is t = 0, the user's Up/Down colour is the peak at t = 1,
/// and the first non-neutral level starts at <see cref="OnsetAnchorFraction"/> so the step from Neutral is visible but bounded.
/// </summary>
public static class HeatmapColorRamp
{
    /// <summary>Ramp position of factor 0 (the first non-neutral level).</summary>
    public const double OnsetAnchorFraction = 0.20;

    /// <summary>Number of LUT slots; index = round(factor * (LutEntryCount - 1)).</summary>
    public const int LutEntryCount = 513;

    private const int MaxLutIndex = LutEntryCount - 1;

    /// <summary>Colour for a classified factor in [0, 1] (non-finite becomes 0).</summary>
    public static IndicatorColor InterpolateStep(IndicatorColor neutral, IndicatorColor peak, double factor)
    {
        double clamped = double.IsFinite(factor) ? Math.Clamp(factor, 0.0, 1.0) : 0.0;
        double t = OnsetAnchorFraction + (clamped * (1.0 - OnsetAnchorFraction));
        return OklabColor.Lerp(OklabColor.FromIndicatorColor(neutral), OklabColor.FromIndicatorColor(peak), t)
            .ToIndicatorColorInGamut(255);
    }

    /// <summary>Fills <paramref name="destination"/> (length <see cref="LutEntryCount"/>) without heap allocation.</summary>
    public static void BuildLut(IndicatorColor neutral, IndicatorColor peak, Span<IndicatorColor> destination)
    {
        if (destination.Length != LutEntryCount)
        {
            throw new ArgumentException($"Destination must hold exactly {LutEntryCount} entries.", nameof(destination));
        }

        var neutralLab = OklabColor.FromIndicatorColor(neutral);
        var peakLab = OklabColor.FromIndicatorColor(peak);
        for (int i = 0; i <= MaxLutIndex; i++)
        {
            double t = OnsetAnchorFraction + ((i / (double)MaxLutIndex) * (1.0 - OnsetAnchorFraction));
            destination[i] = OklabColor.Lerp(neutralLab, peakLab, t).ToIndicatorColorInGamut(255);
        }
    }
}

/// <summary>WCAG 2.1 contrast helpers (SC 1.4.3). Output unit: ratio in [1, 21].</summary>
public static class WcagColorContrast
{
    public const double MinimumAaNormalTextRatio = 4.5;

    private const double WeightRed = 0.2126;
    private const double WeightGreen = 0.7152;
    private const double WeightBlue = 0.0722;
    private const double ContrastOffset = 0.05;
    private const double ByteMax = 255.0;

    /// <summary>Near-black ink used when the preferred text colour fails the minimum ratio.</summary>
    public static readonly IndicatorColor DarkContrastFallback = IndicatorColor.FromArgb(255, 0x05, 0x06, 0x08);

    /// <summary>White ink used when the preferred text colour fails the minimum ratio.</summary>
    public static readonly IndicatorColor LightContrastFallback = IndicatorColor.FromArgb(255, 0xFF, 0xFF, 0xFF);

    public static double RelativeLuminance(byte r, byte g, byte b) =>
        (WeightRed * OklabColor.SrgbToLinear(r / ByteMax))
        + (WeightGreen * OklabColor.SrgbToLinear(g / ByteMax))
        + (WeightBlue * OklabColor.SrgbToLinear(b / ByteMax));

    public static double RelativeLuminance(IndicatorColor c) => RelativeLuminance(c.R, c.G, c.B);

    public static double ContrastRatio(IndicatorColor a, IndicatorColor b)
    {
        double ya = RelativeLuminance(a);
        double yb = RelativeLuminance(b);
        return (Math.Max(ya, yb) + ContrastOffset) / (Math.Min(ya, yb) + ContrastOffset);
    }

    /// <summary>
    /// Returns <paramref name="preferredText"/> when it meets <paramref name="minimumRatio"/> on <paramref name="background"/>;
    /// otherwise the better of the dark/light fallback inks.
    /// </summary>
    public static (IndicatorColor TextColor, bool IsLightTextOnDarkerBackground) ResolveAccessibleTextColor(
        IndicatorColor background,
        IndicatorColor preferredText,
        double minimumRatio = MinimumAaNormalTextRatio)
    {
        IndicatorColor resolved = ContrastRatio(background, preferredText) >= minimumRatio
            ? preferredText
            : (ContrastRatio(background, DarkContrastFallback) >= ContrastRatio(background, LightContrastFallback)
                ? DarkContrastFallback
                : LightContrastFallback);

        return (resolved, RelativeLuminance(resolved) > RelativeLuminance(background));
    }
}
