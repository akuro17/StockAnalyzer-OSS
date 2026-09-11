namespace StockAnalyzer.Core.Models;

/// <summary>
/// Defines the 8 Granville's Law signal types.
/// Positive values represent buy signals, negative values represent sell signals.
/// The absolute value indicates the signal number (1-4).
/// </summary>
public enum GranvilleLawSignalType
{
    /// <summary>No signal detected.</summary>
    None = 0,

    /// <summary>Buy 1: MA flat/rising + price breaks above MA from below (New Buy).</summary>
    Buy1_NewBuy = 1,

    /// <summary>Buy 2: MA rising + price dips below MA temporarily (Pullback Buy).</summary>
    Buy2_PullbackBuy = 2,

    /// <summary>Buy 3: MA rising + price approaches MA and bounces up (Bounce Buy).</summary>
    Buy3_BounceBuy = 3,

    /// <summary>Buy 4: MA falling + price extremely far below MA (Reversal Buy - mean reversion).</summary>
    Buy4_ReversalBuy = 4,

    /// <summary>Sell 1: MA flat/falling + price breaks below MA from above (New Sell).</summary>
    Sell1_NewSell = -1,

    /// <summary>Sell 2: MA falling + price rises above MA temporarily (Return Sell).</summary>
    Sell2_ReturnSell = -2,

    /// <summary>Sell 3: MA falling + price approaches MA and reverses down (Rejection Sell).</summary>
    Sell3_RejectionSell = -3,

    /// <summary>Sell 4: MA rising + price extremely far above MA (Reversal Sell - mean reversion).</summary>
    Sell4_ReversalSell = -4
}
