namespace StockAnalyzer.Core.Models.Confluence;

/// <summary>
/// Defines the direction of a trading signal.
/// </summary>
public enum SignalDirection
{
    /// <summary>No clear direction or neutral</summary>
    Neutral = 0,
    
    /// <summary>Upward/Bullish direction</summary>
    Bullish = 1,
    
    /// <summary>Downward/Bearish direction</summary>
    Bearish = -1
}
