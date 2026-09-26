using System;

namespace StockAnalyzer.Core.Common;

/// <summary>
/// Helper methods for normalizing and comparing ticker symbol strings.
/// Single source of truth (SSoT) for ticker alias matching (e.g. ^GSPC vs GSPC, 7203.T vs 7203-T).
/// </summary>
public static class SymbolHelper
{
    /// <summary>
    /// Checks whether two ticker symbols represent the same instrument,
    /// ignoring casing, leading caret (^), and dot/hyphen variations.
    /// </summary>
    public static bool IsSameSymbol(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        return string.Equals(NormalizeSymbol(a), NormalizeSymbol(b), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Normalizes a symbol to uppercase, trims whitespace, strips leading caret (^), and converts dots (.) to hyphens (-).
    /// </summary>
    public static string NormalizeSymbol(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return string.Empty;
        return symbol.Trim().ToUpperInvariant().TrimStart('^').Replace('.', '-');
    }
}
