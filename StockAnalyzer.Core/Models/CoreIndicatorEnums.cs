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
