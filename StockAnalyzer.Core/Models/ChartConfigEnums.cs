namespace StockAnalyzer.Core.Models
{
    public enum ChartSizingMode
    {
        Fixed,
        AutoAtr,
        Percentage
    }

    public enum ChartRoundingMode
    {
        None = 0,
        NiceNumbers = 1,
        TickSize = 2,
        Floor = 3,
        Ceiling = 4,
        Round = 5
    }

    public enum AutoFallbackMode
    {
        Percentage,
        Fixed
    }

    public enum PriceScaleType
    {
        Linear,
        Log,
        Percent,
        Inverted
    }
}
