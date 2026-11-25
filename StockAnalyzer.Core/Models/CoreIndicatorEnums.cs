namespace StockAnalyzer.Core.Models;

public enum CoreLineStyle
{
    Solid = 0,
    Dash = 1,
    Dot = 2,
    DashDot = 3,
    Step = 4
}

public enum CoreIndicatorCategory
{
    Trend,
    Oscillator,
    Volume,
    Volatility,
    Math,
    Chart,
    Other,

    // Chart-pattern flag indicators (plan ChartPatterns56): appended after Other so the existing values and the sidebar order of the first seven stay unchanged.
    // The display names come from EnumToLocalizedDisplayNameConverter (Enum_CoreIndicatorCategory_<Member> -> [Description] -> ToString()).
    [System.ComponentModel.Description("Candlestick Patterns")] CandlestickPatterns,
    [System.ComponentModel.Description("Harmonic")] Harmonic,
    [System.ComponentModel.Description("Market Structure")] MarketStructure,
    [System.ComponentModel.Description("Pattern Recognition")] PatternRecognition,
    [System.ComponentModel.Description("Trading Rules")] TradingRules,
    [System.ComponentModel.Description("Wave Theory")] WaveTheory
}

public enum VolumeDistributionMode
{
    Proportional,
    Full
}

public enum DisplaySide
{
    Left,
    Right,
    Both
}
