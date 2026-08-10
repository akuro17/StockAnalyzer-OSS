namespace StockAnalyzer.Core.Models.DivergenceCross;

/// <summary>
/// Classification of divergence and cross signal types.
/// </summary>
public enum SignalType
{
    /// <summary>
    /// Price makes lower low, indicator makes higher low. Bullish reversal signal.
    /// </summary>
    RegularBullishDivergence,

    /// <summary>
    /// Price makes higher high, indicator makes lower high. Bearish reversal signal.
    /// </summary>
    RegularBearishDivergence,

    /// <summary>
    /// Price makes higher low, indicator makes lower low. Bullish continuation signal.
    /// </summary>
    HiddenBullishDivergence,

    /// <summary>
    /// Price makes lower high, indicator makes higher high. Bearish continuation signal.
    /// </summary>
    HiddenBearishDivergence,

    /// <summary>
    /// Short-period MA crosses above long-period MA. Bullish signal.
    /// </summary>
    GoldenCross,

    /// <summary>
    /// Short-period MA crosses below long-period MA. Bearish signal.
    /// </summary>
    DeadCross,

    /// <summary>
    /// Generic bullish technical signal.
    /// </summary>
    GenericBullish,

    /// <summary>
    /// Generic bearish technical signal.
    /// </summary>
    GenericBearish
}
