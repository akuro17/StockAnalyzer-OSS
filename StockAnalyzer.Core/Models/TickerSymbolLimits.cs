namespace StockAnalyzer.Core.Models;

/// <summary>
/// The length bounds that each layer applies to a symbol typed or read from outside the process. They are input contracts (not tunables), so they live in code:
/// the character rules are defined once in <see cref="TickerSymbol"/> (<see cref="TickerSymbol.IsAcceptedSymbolFormat"/>), and each layer passes its own bound.
/// The bounds differ per layer on purpose of history, not of design; changing one is a behaviour change that needs an explicit decision.
/// </summary>
public static class TickerSymbolLimits
{
    /// <summary>Add Ticker dialog: longest text (trimmed) offered as a new ticker. The dialog applies this length bound only.</summary>
    public const int DialogMaxLength = 10;

    /// <summary>Chart: longest symbol accepted when the symbol is changed.</summary>
    public const int ChartMaxLength = 15;

    /// <summary>Bulk import: longest token accepted.</summary>
    public const int ImportMaxLength = 16;

    /// <summary>File safety (MCP, read-only gateway, synthetic parser): longest external symbol mapped to a file name.</summary>
    public const int FileSafetyMaxLength = 32;
}
