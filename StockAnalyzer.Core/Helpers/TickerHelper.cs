using System;
using System.Text.RegularExpressions;

namespace StockAnalyzer.Core.Helpers;

/// <summary>
/// Centralized helper utilities for ticker symbol parsing, validation, and normalization.
/// Single Source of Truth (SSoT) for Japanese stock code auto-completion (-T suffix).
/// </summary>
public static partial class TickerHelper
{
    // [0-9], not \d: \d also matches the digits of other scripts (full-width, Arabic-Indic), which are not Japanese stock codes.
    [GeneratedRegex(@"^[0-9]{4}$", RegexOptions.Compiled)]
    private static partial Regex FourDigitJapaneseCodeRegex();

    /// <summary>
    /// Checks whether the provided character span matches a 4-digit Japanese stock code.
    /// </summary>
    public static bool IsFourDigitJapaneseCode(ReadOnlySpan<char> code)
    {
        return FourDigitJapaneseCodeRegex().IsMatch(code);
    }

    /// <summary>
    /// Checks whether the provided string matches a 4-digit Japanese stock code.
    /// </summary>
    public static bool IsFourDigitJapaneseCode(string code)
    {
        return !string.IsNullOrEmpty(code) && FourDigitJapaneseCodeRegex().IsMatch(code);
    }

    /// <summary>
    /// Normalizes a ticker symbol: the canonical form (<see cref="StockAnalyzer.Core.Models.TickerSymbol.Canonicalize"/>: trimmed, upper-case, dots replaced by hyphens), plus the
    /// "-T" suffix when the result is a 4-digit Japanese stock code. Null or white space gives an empty string. The suffix rule is the only rule owned here.
    /// </summary>
    public static string NormalizeTicker(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        string canonical = StockAnalyzer.Core.Models.TickerSymbol.Canonicalize(input);
        return IsFourDigitJapaneseCode(canonical) ? canonical + "-T" : canonical;
    }
}
