using System;
using System.Collections.Generic;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Helpers;

/// <summary>
/// Parser for synthetic custom formula symbols (e.g. "7203.T / 7267.T", "7203 - 7267", "AAPL + MSFT", "AAPL * MSFT").
/// Produces canonical representations and dispatches supported binary operations (+, -, /) while safely rejecting
/// evaluation of '*' to prevent extreme scale amplification.
/// </summary>
public static class SyntheticSymbolParser
{
    public const int MaxExpressionLength = 64;
    private static readonly char[] DisallowedOperatorChars = { '+', '/', '*' };

    /// <summary>
    /// Checks whether the symbol string represents a valid synthetic formula expression.
    /// </summary>
    public static bool IsSyntheticExpression(string? symbol) => TryParse(symbol, out _);

    /// <summary>
    /// Attempts to parse an input expression into a <see cref="SyntheticSymbolExpression"/>.
    /// </summary>
    public static bool TryParse(string? input, out SyntheticSymbolExpression expression)
    {
        expression = default;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        string s = input.Trim();
        if (s.Length > MaxExpressionLength)
        {
            return false;
        }

        // 1. Check for Add (+), Divide (/), Multiply (*)
        // These operators never appear in valid single ticker symbols.
        if (TryExtractOperator(s, '+', out string leftPart, out string rightPart))
        {
            return BuildExpression(leftPart, rightPart, SeriesOperation.Add, '+', isSupported: true, out expression);
        }

        if (TryExtractDivideOperator(s, out leftPart, out rightPart))
        {
            return BuildExpression(leftPart, rightPart, SeriesOperation.Divide, '/', isSupported: true, out expression);
        }

        if (TryExtractOperator(s, '*', out leftPart, out rightPart))
        {
            return BuildExpression(leftPart, rightPart, SeriesOperation.Multiply, '*', isSupported: false, out expression);
        }

        // 2. Check for Subtract (-)
        // Disambiguate hyphen in ticker symbols (e.g. 7203-T, BRK-B) from binary subtraction.
        if (TryExtractSubtractOperator(s, out leftPart, out rightPart))
        {
            return BuildExpression(leftPart, rightPart, SeriesOperation.Subtract, '-', isSupported: true, out expression);
        }

        return false;
    }

    private static bool TryExtractOperator(string s, char opChar, out string leftPart, out string rightPart)
    {
        leftPart = string.Empty;
        rightPart = string.Empty;

        int idx = s.IndexOf(opChar);
        if (idx < 0)
        {
            return false;
        }

        // Binary expressions require exactly one operator
        if (s.IndexOf(opChar, idx + 1) >= 0)
        {
            return false;
        }

        leftPart = s.Substring(0, idx);
        rightPart = s.Substring(idx + 1);
        return !string.IsNullOrWhiteSpace(leftPart) && !string.IsNullOrWhiteSpace(rightPart);
    }

    private static bool TryExtractDivideOperator(string s, out string leftPart, out string rightPart)
    {
        leftPart = string.Empty;
        rightPart = string.Empty;

        int idx = s.IndexOf('/');
        if (idx < 0)
        {
            return false;
        }

        // Binary expressions require exactly one operator
        if (s.IndexOf('/', idx + 1) >= 0)
        {
            return false;
        }

        string l = s.Substring(0, idx);
        string r = s.Substring(idx + 1);

        if (string.IsNullOrWhiteSpace(l) || string.IsNullOrWhiteSpace(r))
        {
            return false;
        }

        // Pattern A: Whitespace before or after slash (e.g. "7203 / 7267", "AAPL / MSFT")
        if (char.IsWhiteSpace(s[idx - 1]) || char.IsWhiteSpace(s[idx + 1]))
        {
            leftPart = l;
            rightPart = r;
            return true;
        }

        // Pattern B: No whitespace around slash (e.g. "7203/7267", "7203-T/7267-T")
        // To avoid treating relative file paths or path traversal (e.g. "a/b") as division formulas:
        // Division without whitespace requires:
        // 1. Both sides must be at least 2 characters (excluding single-character path elements like "a/b")
        // 2. Both sides must be recognized stock tickers (4-digit Japanese code, exchange suffix, or uppercase ticker)
        string lTrim = l.Trim();
        string rTrim = r.Trim();
        if (lTrim.Length < 2 || rTrim.Length < 2)
        {
            return false;
        }

        bool lIsTicker = TickerHelper.IsFourDigitJapaneseCode(lTrim) ||
                         lTrim.EndsWith("-T", StringComparison.OrdinalIgnoreCase) ||
                         lTrim.EndsWith(".T", StringComparison.OrdinalIgnoreCase) ||
                         (lTrim.All(char.IsAsciiLetterOrDigit) && lTrim.Any(char.IsUpper) && !lTrim.Any(char.IsLower));

        bool rIsTicker = TickerHelper.IsFourDigitJapaneseCode(rTrim) ||
                         rTrim.EndsWith("-T", StringComparison.OrdinalIgnoreCase) ||
                         rTrim.EndsWith(".T", StringComparison.OrdinalIgnoreCase) ||
                         (rTrim.All(char.IsAsciiLetterOrDigit) && rTrim.Any(char.IsUpper) && !rTrim.Any(char.IsLower));

        if (lIsTicker && rIsTicker)
        {
            leftPart = l;
            rightPart = r;
            return true;
        }

        return false;
    }

    private static bool TryExtractSubtractOperator(string s, out string leftPart, out string rightPart)
    {
        leftPart = string.Empty;
        rightPart = string.Empty;

        // Pattern A: Whitespace-delimited subtraction (e.g. " - ")
        int wsHyphenIdx = s.IndexOf(" - ", StringComparison.Ordinal);
        if (wsHyphenIdx >= 0)
        {
            if (s.IndexOf(" - ", wsHyphenIdx + 3, StringComparison.Ordinal) >= 0)
            {
                return false;
            }

            leftPart = s.Substring(0, wsHyphenIdx);
            rightPart = s.Substring(wsHyphenIdx + 3);
            return true;
        }

        // Any whitespace before or after hyphen
        for (int i = 1; i < s.Length - 1; i++)
        {
            if (s[i] == '-' && (char.IsWhiteSpace(s[i - 1]) || char.IsWhiteSpace(s[i + 1])))
            {
                leftPart = s.Substring(0, i);
                rightPart = s.Substring(i + 1);
                return true;
            }
        }

        // Pattern B: No whitespace around hyphens (e.g. "7203-7267", "7203-T-7267-T", "AAPL-MSFT")
        var hyphenIndices = new List<int>();
        for (int i = 1; i < s.Length - 1; i++)
        {
            if (s[i] == '-')
            {
                hyphenIndices.Add(i);
            }
        }

        if (hyphenIndices.Count == 0)
        {
            return false;
        }

        if (hyphenIndices.Count == 1)
        {
            int idx = hyphenIndices[0];
            string l = s.Substring(0, idx);
            string r = s.Substring(idx + 1);

            // Without whitespace, a single hyphen is ONLY a spread/subtraction if BOTH sides are 4-digit Japanese codes (e.g. "7203-7267")
            if (TickerHelper.IsFourDigitJapaneseCode(l) && TickerHelper.IsFourDigitJapaneseCode(r))
            {
                leftPart = l;
                rightPart = r;
                return true;
            }

            // Otherwise, without whitespace, a single hyphen represents a ticker with suffix or hyphenated symbol (e.g. 7203-T, BRK-B, ZZZ-EMPTY)
            return false;
        }

        // Multiple hyphens without spaces: search for split between two well-formed tickers
        foreach (int idx in hyphenIndices)
        {
            string l = s.Substring(0, idx);
            string r = s.Substring(idx + 1);
            if (l.Length <= 1 || r.Length <= 1) continue;
            if (l.EndsWith("-", StringComparison.Ordinal) || r.StartsWith("-", StringComparison.Ordinal)) continue;

            string normL = TickerHelper.NormalizeTicker(l.Trim());
            string normR = TickerHelper.NormalizeTicker(r.Trim());

            // A constituent ticker cannot contain internal whitespace
            if (normL.Any(char.IsWhiteSpace) || normR.Any(char.IsWhiteSpace))
            {
                continue;
            }

            // A valid single ticker cannot contain multiple hyphens
            if (normL.Count(c => c == '-') > 1 || normR.Count(c => c == '-') > 1)
            {
                continue;
            }

            if (TickerSymbol.IsWellFormed(normL) && TickerSymbol.IsWellFormed(normR) &&
                !normL.Equals("T", StringComparison.OrdinalIgnoreCase) &&
                !normR.Equals("T", StringComparison.OrdinalIgnoreCase))
            {
                leftPart = l;
                rightPart = r;
                return true;
            }
        }

        return false;
    }

    private static bool BuildExpression(
        string leftPart,
        string rightPart,
        SeriesOperation operation,
        char opChar,
        bool isSupported,
        out SyntheticSymbolExpression expression)
    {
        expression = default;
        string leftTrimmed = leftPart.Trim();
        string rightTrimmed = rightPart.Trim();

        if (string.IsNullOrEmpty(leftTrimmed) || string.IsNullOrEmpty(rightTrimmed))
        {
            return false;
        }

        // Ticker symbols must not contain whitespace
        if (leftTrimmed.Any(char.IsWhiteSpace) || rightTrimmed.Any(char.IsWhiteSpace))
        {
            return false;
        }

        string leftNorm = TickerHelper.NormalizeTicker(leftTrimmed);
        string rightNorm = TickerHelper.NormalizeTicker(rightTrimmed);

        if (leftNorm.Any(char.IsWhiteSpace) || rightNorm.Any(char.IsWhiteSpace))
        {
            return false;
        }

        if (!TickerSymbol.IsWellFormed(leftNorm) || !TickerSymbol.IsWellFormed(rightNorm))
        {
            return false;
        }

        if (leftNorm.Count(c => c == '-') > 1 || rightNorm.Count(c => c == '-') > 1)
        {
            return false;
        }

        // Neither leg may contain operator characters
        if (leftNorm.IndexOfAny(DisallowedOperatorChars) >= 0 ||
            rightNorm.IndexOfAny(DisallowedOperatorChars) >= 0)
        {
            return false;
        }

        string canonical = $"{leftNorm} {opChar} {rightNorm}";
        expression = new SyntheticSymbolExpression(
            leftNorm,
            rightNorm,
            operation,
            opChar,
            isSupported,
            canonical);

        return true;
    }
}
