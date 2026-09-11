namespace StockAnalyzer.Core.Models.ElliottWave;

/// <summary>
/// Defines the specific Elliott Wave condition types for screening.
/// Each type represents an actionable phase of the wave pattern.
/// </summary>
public enum ElliottWaveConditionType
{
    /// <summary>Any Elliott Wave impulse pattern detected.</summary>
    AnyImpulse,

    /// <summary>Any Elliott Wave corrective pattern detected.</summary>
    AnyCorrective,

    /// <summary>Wave 3 just starting (high-probability long entry for bullish).</summary>
    Wave3Start,

    /// <summary>Wave 5 completed with divergence (reversal warning).</summary>
    Wave5Divergence,

    /// <summary>Corrective Wave C completing (potential continuation entry).</summary>
    WaveCComplete
}
