namespace StockAnalyzer.Core.Models.MarketStructure;

/// <summary>
/// Defines the type of market structure shift detected from pivot point analysis.
/// BOS = Break of Structure (trend continuation), CHoCH = Change of Character (trend reversal).
/// </summary>
public enum MarketStructureType
{
    /// <summary>No significant structure shift detected.</summary>
    None = 0,

    /// <summary>Bullish Break of Structure: Higher high confirms uptrend continuation.</summary>
    BullishBOS,

    /// <summary>Bearish Break of Structure: Lower low confirms downtrend continuation.</summary>
    BearishBOS,

    /// <summary>Bullish Change of Character: Higher high after a downtrend signals potential reversal to uptrend.</summary>
    BullishCHoCH,

    /// <summary>Bearish Change of Character: Lower low after an uptrend signals potential reversal to downtrend.</summary>
    BearishCHoCH
}
