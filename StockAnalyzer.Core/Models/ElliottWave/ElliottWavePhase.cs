namespace StockAnalyzer.Core.Models.ElliottWave;

/// <summary>
/// Represents the current phase of an Elliott Wave pattern.
/// Used for screening to identify actionable trade setups.
/// </summary>
public enum ElliottWavePhase
{
    /// <summary>Impulse Wave 1 starting (initial trend move).</summary>
    Wave1,

    /// <summary>Impulse Wave 2 retracement (pullback from Wave 1).</summary>
    Wave2,

    /// <summary>Impulse Wave 3 starting (strongest, longest wave - high-probability entry).</summary>
    Wave3Start,

    /// <summary>Impulse Wave 3 in progress.</summary>
    Wave3,

    /// <summary>Impulse Wave 4 retracement.</summary>
    Wave4,

    /// <summary>Impulse Wave 5 in progress (final push, often with divergence).</summary>
    Wave5,

    /// <summary>Impulse Wave 5 completed with RSI divergence (reversal signal).</summary>
    Wave5Divergence,

    /// <summary>Corrective Wave A (first counter-trend move).</summary>
    WaveA,

    /// <summary>Corrective Wave B (partial retracement of A).</summary>
    WaveB,

    /// <summary>Corrective Wave C (final corrective move).</summary>
    WaveC
}
