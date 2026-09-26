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
        // Empty is valid: it means "no initial symbol" (chart starts empty until the user enters one).
        if (DefaultSymbol == null)
            throw new System.InvalidOperationException("ChartDefaultSettings: DefaultSymbol cannot be null.");
    }
}
