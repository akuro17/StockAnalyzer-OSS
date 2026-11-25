namespace StockAnalyzer.Core.Services.Analysis.PatternEngines;

/// <summary>
/// The numbers of the Harmonic specifications (<c>03_Harmonic\01_gartley_pattern.md</c>, <c>02_butterfly_pattern.md</c>, <c>03_bat_pattern.md</c>) that are definitions and not user settings
/// (plan ChartPatterns56 v2, run V2-6; ledger <c>docs/sa_pattern_literal_ledger_V2-6_Harmonic</c>). The numbers a user may tune are parameters in <c>CoreHarmonicPatternParameter</c>; their defaults
/// and ranges live in <c>PatternFlagParameterLimits.Harmonic.cs</c>.
/// </summary>
public static partial class PatternSpecConstants
{
    /// <summary>Spec headers (01..03): minimum history (bars) of the harmonic indicators (40-bar zone range + ATR14).</summary>
    public const int HarmonicMinBars = 40;

    /// <summary>Spec 2 (01..03): lookback bars of the price-zone percentile <c>Pos</c> (point X for Gartley/Bat, point D for Butterfly).</summary>
    public const int HarmonicZoneBars = 40;

    /// <summary>Spec 01/03 section 2: point X high/low zone threshold (bullish &lt;= 0.25, bearish &gt;= 0.75).</summary>
    public const decimal HarmonicPointXZoneThreshold = 0.25m;

    /// <summary>Spec 02 section 2: Butterfly point D extreme-zone threshold (bullish &lt;= 0.15, bearish &gt;= 0.85).</summary>
    public const decimal HarmonicButterflyPointDZoneThreshold = 0.15m;

    /// <summary>Spec 01..03 section 2: minimum X-&gt;A initial leg percentage move (0.03 = 3.0%).</summary>
    public const decimal HarmonicInitialLegMinFraction = 0.03m;

    /// <summary>Spec 01..03 section 2: minimum X-&gt;A initial leg move in context ATRs measured at <c>i_X - 1</c>.</summary>
    public const decimal HarmonicInitialLegAtrMultiplier = 2.5m;

    /// <summary>Spec 01..03 section 2: causal local-extrema window sizes in ascending order so the smallest confirming window <c>w_min</c> is tried first.</summary>
    public static readonly int[] HarmonicWindows = { 2, 3, 5, 8 };

    /// <summary>Spec 01..03 section 3: denominator floor (bars) in the time-symmetry test <c>max(d_k) / max(3.0, min(d_k))</c>.</summary>
    public const decimal HarmonicTimeSymmetryFloorBars = 3.0m;

    /// <summary>Spec 01 section 3 (decision U2-13 A): AB/CD leg duration symmetry bounds for Gartley only.</summary>
    public const decimal HarmonicGartleyAbCdSymmetryMin = 0.33m;
    public const decimal HarmonicGartleyAbCdSymmetryMax = 3.0m;

    /// <summary>Spec 01..03 section 4: Fibonacci trapezoid weights (sum = 1.0).</summary>
    public const decimal HarmonicWeightAbXa = 0.30m;
    public const decimal HarmonicWeightBcAb = 0.15m;
    public const decimal HarmonicWeightCdBc = 0.15m;
    public const decimal HarmonicWeightAdXa = 0.40m;

    /// <summary>Spec 01 section 4: Gartley ideal Fibonacci targets.</summary>
    public const decimal HarmonicGartleyIdealAbXa = 0.618m;
    public const decimal HarmonicGartleyIdealBcAbMin = 0.382m;
    public const decimal HarmonicGartleyIdealBcAbMax = 0.886m;
    public const decimal HarmonicGartleyIdealCdBcMin = 1.272m;
    public const decimal HarmonicGartleyIdealCdBcMax = 1.618m;
    public const decimal HarmonicGartleyIdealAdXa = 0.786m;

    /// <summary>Spec 02 section 4: Butterfly ideal Fibonacci targets.</summary>
    public const decimal HarmonicButterflyIdealAbXa = 0.786m;
    public const decimal HarmonicButterflyIdealBcAbMin = 0.382m;
    public const decimal HarmonicButterflyIdealBcAbMax = 0.886m;
    public const decimal HarmonicButterflyIdealCdBcMin = 1.618m;
    public const decimal HarmonicButterflyIdealCdBcMax = 2.618m;
    public const decimal HarmonicButterflyIdealAdXaMin = 1.272m;
    public const decimal HarmonicButterflyIdealAdXaMax = 1.618m;

    /// <summary>Spec 03 section 4: Bat ideal Fibonacci targets.</summary>
    public const decimal HarmonicBatIdealAbXaMin = 0.382m;
    public const decimal HarmonicBatIdealAbXaMax = 0.500m;
    public const decimal HarmonicBatIdealBcAbMin = 0.382m;
    public const decimal HarmonicBatIdealBcAbMax = 0.886m;
    public const decimal HarmonicBatIdealCdBcMin = 1.618m;
    public const decimal HarmonicBatIdealCdBcMax = 2.618m;
    public const decimal HarmonicBatIdealAdXa = 0.886m;

    /// <summary>Spec 02 section 5: Butterfly over-extension invalidation threshold (<c>AD / XA &gt; 1.75</c>).</summary>
    public const decimal HarmonicButterflyInvalidationExtensionMax = 1.75m;
}
