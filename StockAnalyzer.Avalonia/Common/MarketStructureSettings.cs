namespace StockAnalyzer.Avalonia.Common;

public class MarketStructureSettings
{
    public decimal ZigzagThresholdPercent { get; set; } = 5.0m;

    public void Validate()
    {
        if (ZigzagThresholdPercent <= 0) throw new System.InvalidOperationException("MarketStructureSettings: ZigzagThresholdPercent must be positive.");
    }
}
