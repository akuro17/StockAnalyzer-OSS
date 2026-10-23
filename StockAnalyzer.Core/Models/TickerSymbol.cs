namespace StockAnalyzer.Core.Models;

/// <summary>
/// Single definition of the canonical ticker-symbol form and of the well-formedness check applied to
/// symbols that come from outside the process (for example an AI client). Canonical form:
/// <c>Trim().ToUpperInvariant()</c> with '.' replaced by '-' (the data files are named <c>2681-T.parquet</c>).
/// </summary>
public static class TickerSymbol
{
    /// <summary>Longest accepted external symbol, in characters (safety bound; real tickers are far shorter).</summary>
    public const int MaxLength = 32;

    private const string ParentSegment = "..";
    private const char DriveSeparator = ':';

    /// <summary>Returns the canonical form of <paramref name="symbol"/>. No validation is performed.</summary>
    public static string Canonicalize(string symbol) => symbol.Trim().ToUpperInvariant().Replace('.', '-');

    /// <summary>
    /// True when <paramref name="symbol"/> is safe to map to a file name: non-empty after trimming, at most
    /// <see cref="MaxLength"/> characters, and free of path separators, drive separators, parent-directory
    /// segments and control characters.
    /// </summary>
    public static bool IsWellFormed(string? symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return false;
        }

        string trimmed = symbol.Trim();
        if (trimmed.Length > MaxLength || trimmed.Contains(ParentSegment, StringComparison.Ordinal))
        {
            return false;
        }

        foreach (char c in trimmed)
        {
            if (c == '/' || c == '\\' || c == DriveSeparator || char.IsControl(c))
            {
                return false;
            }
        }

        return true;
    }
}
