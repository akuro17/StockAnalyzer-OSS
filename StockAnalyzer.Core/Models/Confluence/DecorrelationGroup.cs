namespace StockAnalyzer.Core.Models.Confluence;

/// <summary>
/// Categories of indicators to prevent multi-collinearity (double-counting similar information).
/// </summary>
public enum DecorrelationGroup
{
    /// <summary>Not categorized or independent</summary>
    None,

    /// <summary>Trend following indicators (MA, MACD, etc.)</summary>
    Trend,

    /// <summary>Momentum/Oscillator indicators (RSI, Stochastic, etc.)</summary>
    Momentum,

    /// <summary>Volatility based indicators (Bollinger Bands, ATR, etc.)</summary>
    Volatility,

    /// <summary>Volume/Flow based indicators (OBV, MFI, etc.)</summary>
    Volume,

    /// <summary>Market structure/Geometric patterns (Chart patterns)</summary>
    Structure
}
