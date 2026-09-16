namespace StockAnalyzer.Core.Models;

public enum CoreLineStyle
{
    Solid,
    Dash,
    Dot,
    DashDot,
    Step
}

public enum CoreIndicatorCategory
{
    Trend,
    Oscillator,
    Volume,
    Volatility,
    Math,
    Chart,
    Other
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
