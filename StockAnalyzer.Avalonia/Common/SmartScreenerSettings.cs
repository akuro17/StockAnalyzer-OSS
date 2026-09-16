namespace StockAnalyzer.Avalonia.Common;

public class SmartScreenerSettings
{
    public string ScreeningDataPath { get; set; } = "Web/Site/root/data/screening/latest_screening_data.json";

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ScreeningDataPath))
            throw new System.InvalidOperationException("SmartScreenerSettings: ScreeningDataPath cannot be empty.");
    }
}
