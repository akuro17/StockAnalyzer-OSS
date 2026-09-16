namespace StockAnalyzer.Avalonia.Common;

public class ScreenerSettings
{
    public string[] DefaultSymbols { get; set; } = System.Array.Empty<string>();

    public void Validate()
    {
        if (DefaultSymbols == null) throw new System.InvalidOperationException("ScreenerSettings: DefaultSymbols cannot be null.");
    }
}
