namespace StockAnalyzer.Core.Models.HarmonicPattern;

/// <summary>
/// Specifies the pivot extraction mode for harmonic pattern detection.
/// </summary>
public enum HarmonicPivotMode
{
    /// <summary>
    /// Legacy mode: uses multi-scale ZigZag thresholds mapped to local extrema windows.
    /// Default for saved drawings without explicit mode property.
    /// </summary>
    Legacy = 0,

    /// <summary>
    /// Recommended mode (D4 = C'): uses multi-scale local extrema windows {2, 3, 5, 8}
    /// combined with minimum percentage swing validation across all pattern legs.
    /// </summary>
    LocalExtremaAmplitude = 1
}
