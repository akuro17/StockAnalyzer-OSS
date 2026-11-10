using System;

namespace StockAnalyzer.Core.Utils;

/// <summary>
/// High-performance utility for strictly parsing enums.
/// Enforces named identifier validation by rejecting raw integer and signed numeric strings (e.g. "0", "14", "-1"),
/// and uses generic Enum.IsDefined to eliminate object boxing and reflection overhead.
/// </summary>
public static class StrictEnumValidator
{
    /// <summary>
    /// Attempts to parse an enum strictly from its named member string.
    /// Returns false if the input is null, empty, whitespace, or begins with a numeric digit or sign (+/-).
    /// </summary>
    public static bool TryParseStrict<TEnum>(string? value, out TEnum result, bool ignoreCase = true) where TEnum : struct, Enum
    {
        result = default;
        if (string.IsNullOrWhiteSpace(value)) return false;

        ReadOnlySpan<char> span = value.AsSpan().Trim();
        char first = span[0];
        if (char.IsAsciiDigit(first) || first is '-' or '+')
        {
            return false; // Reject numeric strings from matching enum integer indices
        }

        if (!Enum.TryParse<TEnum>(value, ignoreCase: ignoreCase, out result))
        {
            return false;
        }

        return Enum.IsDefined<TEnum>(result);
    }
}
