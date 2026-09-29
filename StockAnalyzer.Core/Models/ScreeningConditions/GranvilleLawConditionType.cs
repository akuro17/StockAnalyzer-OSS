using System.ComponentModel;
using StockAnalyzer.Core.Models.Indicators.Chart;

namespace StockAnalyzer.Core.Models.ScreeningConditions;

/// <summary>
/// Defines the specific Granville's Laws signal or signal group to screen for.
/// </summary>
public enum GranvilleLawConditionType
{
    [Description("Any Buy Signal (B1~B4)")]
    AnyBuy,
    
    [Description("B1: New Buy (MA Flat/Up + Price crosses above)")]
    Buy1_NewBuy = (int)GranvilleLawSignalType.Buy1_NewBuy,
    
    [Description("B2: Dip Buy (MA up + Price dips below briefly)")]
    Buy2_PullbackBuy = (int)GranvilleLawSignalType.Buy2_PullbackBuy,
    
    [Description("B3: Support Buy (MA up + Price bounces off MA)")]
    Buy3_BounceBuy = (int)GranvilleLawSignalType.Buy3_BounceBuy,
    
    [Description("B4: Reversal Buy (MA down + Price extreme deviation below)")]
    Buy4_ReversalBuy = (int)GranvilleLawSignalType.Buy4_ReversalBuy,

    [Description("Any Sell Signal (S1~S4)")]
    AnySell,
    
    [Description("S1: New Sell (MA Flat/Down + Price crosses below)")]
    Sell1_NewSell = (int)GranvilleLawSignalType.Sell1_NewSell,
    
    [Description("S2: Rally Sell (MA down + Price rallies above briefly)")]
    Sell2_ReturnSell = (int)GranvilleLawSignalType.Sell2_ReturnSell,
    
    [Description("S3: Resistance Sell (MA down + Price bounces off MA)")]
    Sell3_RejectionSell = (int)GranvilleLawSignalType.Sell3_RejectionSell,
    
    [Description("S4: Reversal Sell (MA up + Price extreme deviation above)")]
    Sell4_ReversalSell = (int)GranvilleLawSignalType.Sell4_ReversalSell
}
