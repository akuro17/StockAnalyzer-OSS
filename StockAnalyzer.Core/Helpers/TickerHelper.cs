using System;
using System.Text.RegularExpressions;

namespace StockAnalyzer.Core.Helpers;

/// <summary>
/// Centralized helper utilities for ticker symbol parsing, validation, and normalization.
/// Single Source of Truth (SSoT) for Japanese stock code auto-completion (-T suffix).
/// </summary>
public static partial class TickerHelper
{
    [GeneratedRegex(@"^\d{4}$", RegexOptions.Compiled)]
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
    /// Normalizes a ticker symbol, automatically appending "-T" to 4-digit Japanese stock codes
    /// and replacing dots with hyphens.
    /// </summary>
    public static string NormalizeTicker(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        var raw = input.Trim().ToUpperInvariant();
        if (IsFourDigitJapaneseCode(raw))
        {
            raw += "-T";
        }
        return raw.Replace('.', '-');
    }
}
