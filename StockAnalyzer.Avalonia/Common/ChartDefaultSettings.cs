namespace StockAnalyzer.Avalonia.Common;

/// <summary>
/// Strongly-typed configuration for chart defaults.
/// Bound from the "Chart" section of appsettings.json via IOptions.
/// </summary>
public class ChartDefaultSettings
{
    public string DefaultSymbol { get; set; } = StockAnalyzer.Core.ChartConstants.DefaultSymbol;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(DefaultSymbol))
            throw new System.InvalidOperationException("ChartDefaultSettings: DefaultSymbol cannot be empty.");
    }
}
