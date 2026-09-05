namespace StockAnalyzer.Core.Models.HarmonicPattern;

/// <summary>
/// Enumerates the classic harmonic chart pattern types defined by
/// specific Fibonacci retracement and extension ratios between X-A-B-C-D legs.
/// </summary>
public enum HarmonicPatternType
{
    /// <summary>
    /// Gartley pattern: AB=0.618*XA, D=0.786*XA retracement.
    /// The most common harmonic pattern signaling a trend reversal.
    /// </summary>
    Gartley,

    /// <summary>
    /// Bat pattern: AB=0.382-0.50*XA, D=0.886*XA retracement.
    /// Deep retracement variant producing precise reversal zones.
    /// </summary>
    Bat,

    /// <summary>
    /// Butterfly pattern: AB=0.786*XA, D=1.272-1.618*XA extension.
    /// Extension pattern where D extends beyond X.
    /// </summary>
    Butterfly,

    /// <summary>
    /// Crab pattern: AB=0.382-0.618*XA, D=1.618*XA extension.
    /// Extreme extension pattern providing the most precise PRZ.
    /// </summary>
    Crab,

    /// <summary>
    /// Alternate Bat pattern: AB&lt;=0.382*XA, D=1.130*XA extension.
    /// Fakeout variant where D slightly exceeds X, trapping breakout traders.
    /// </summary>
    AlternateBat,

    /// <summary>
    /// Deep Crab pattern: AB=0.886*XA (fixed), D=1.618*XA extension.
    /// Crab variant with a very deep initial retracement at B.
    /// </summary>
    DeepCrab,

    /// <summary>
    /// Leonardo pattern: AB=0.500*XA (fixed), D=0.786*XA retracement.
    /// Hybrid between Gartley and Bat with half-retracement at B.
    /// </summary>
    Leonardo,

    /// <summary>
    /// Nen Star pattern: BC extends beyond A (1.130-1.414), D=1.130-1.272*XA extension.
    /// Double fakeout structure trapping traders at both C and D breakouts.
    /// </summary>
    NenStar,

    /// <summary>
    /// Cypher pattern: BC extends beyond A (1.272-1.414), D=0.786 of XC retracement.
    /// Liquidity trap where breakout traders are absorbed before sharp reversal.
    /// Uses D/XC ratio instead of D/XA for PRZ calculation.
    /// </summary>
    Cypher,

    /// <summary>
    /// Shark pattern: BC extends significantly beyond A (1.618-2.236), D=0.886-1.130*XA.
    /// Counter-trend thrust pattern capturing extreme harmonic impulse waves.
    /// </summary>
    Shark,

    /// <summary>
    /// AB=CD pattern: The fundamental equal-leg pattern where CD ≈ AB in price and time.
    /// Detected using a dedicated 4-point window (X=A in result).
    /// </summary>
    ABCD,

    /// <summary>
    /// Three Drives pattern: 3 symmetrical drives with 0.618 retracement corrections.
    /// Each drive extends 1.272-1.618 of the preceding correction.
    /// </summary>
    ThreeDrives,

    /// <summary>
    /// Sea Pony pattern: AB=CD variant with shallow BC retracement (0.382-0.500)
    /// and extended CD leg (1.618-2.618 of AB).
    /// </summary>
    SeaPony,

    /// <summary>
    /// 5-0 pattern (0-X-A-B-C structure): Captures the first pullback after a Shark completion.
    /// AB extends 1.130-1.618 of XA, BC extends 1.618-2.240 of AB, PRZ at BC × 0.500.
    /// </summary>
    FiveZero,

    /// <summary>
    /// White Swan pattern (bullish reversal): Extreme AB extension (1.382-2.000 of XA)
    /// capturing panic sell-offs. Shallow BC correction followed by sharp reversal.
    /// </summary>
    WhiteSwan,

    /// <summary>
    /// Black Swan pattern (bearish reversal): Same ratios as White Swan but signals
    /// a bearish reversal from an extreme bullish overextension.
    /// </summary>
    BlackSwan,

    /// <summary>
    /// Sea Horse pattern: AB=CD variant similar to Sea Pony with shallow BC retracement
    /// (0.382-0.500) and extended CD leg (1.618-2.618 of AB). Stronger momentum continuation.
    /// </summary>
    SeaHorse,

    /// <summary>
    /// Dragon pattern: Fibonacci-quantified double bottom/top. 4-point structure where
    /// the hump retraces 0.382-0.500 and the second foot reaches 0.680-1.000 of the first swing.
    /// </summary>
    Dragon,

    /// <summary>
    /// Navarro 200 pattern: XABCD pattern with AB/XA=0.382-0.786, BC/AB=0.886-1.128,
    /// D/XA=0.886-1.128. Uniquely requires a time-zone constraint: the C-D leg duration
    /// must fall within a Fibonacci ratio (0.382-2.618) of the X-A leg duration.
    /// </summary>
    Navarro200
}
