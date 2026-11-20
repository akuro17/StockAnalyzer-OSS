using System;
using System.Collections.Generic;
using System.Globalization;

namespace StockAnalyzer.Core.Calculator;

internal enum CalcTokenKind
{
    Number,
    Identifier,
    Plus,
    Minus,
    Star,
    Slash,
    Percent,
    Caret,
    OpenParen,
    CloseParen,
    OpenBracket,
    CloseBracket,
    Comma,
    EndOfInput,
}

internal readonly record struct CalcToken(CalcTokenKind Kind, int Position, string Text, decimal Value);

/// <summary>
/// Single-pass left-to-right ASCII tokenizer for <see cref="ExpressionCalculator"/>.
/// </summary>
internal static class CalculatorLexer
{
    private const int MaxDecimalFractionalDigits = 28;
    private const int MaxDecimalSignificantDigits = 29;

    public static bool TryTokenize(
        string expression,
        CalculatorLimits limits,
        out List<CalcToken> tokens,
        out CalculatorError? error)
    {
        tokens = new List<CalcToken>();
        error = null;

        int index = 0;
        int length = expression.Length;

        while (true)
        {
            while (index < length && (expression[index] == ' ' || expression[index] == '\t'))
            {
                index++;
            }

            if (index == length)
            {
                tokens.Add(new CalcToken(CalcTokenKind.EndOfInput, length, string.Empty, 0m));
                return true;
            }

            int start = index;
            char ch = expression[index];
            CalcToken token;

            if (ExpressionCalculator.IsAsciiDigit(ch))
            {
                if (!TryReadNumber(expression, ref index, start, out decimal number, out error))
                {
                    return false;
                }

                token = new CalcToken(CalcTokenKind.Number, start, expression.Substring(start, index - start), number);
            }
            else if (ExpressionCalculator.IsIdentifierStart(ch))
            {
                index++;
                while (index < length && ExpressionCalculator.IsIdentifierPart(expression[index]))
                {
                    index++;
                }

                token = new CalcToken(CalcTokenKind.Identifier, start, expression.Substring(start, index - start), 0m);
            }
            else
            {
                CalcTokenKind? opKind = ch switch
                {
                    '+' => CalcTokenKind.Plus,
                    '-' => CalcTokenKind.Minus,
                    '*' => CalcTokenKind.Star,
                    '/' => CalcTokenKind.Slash,
                    '%' => CalcTokenKind.Percent,
                    '^' => CalcTokenKind.Caret,
                    '(' => CalcTokenKind.OpenParen,
                    ')' => CalcTokenKind.CloseParen,
                    '[' => CalcTokenKind.OpenBracket,
                    ']' => CalcTokenKind.CloseBracket,
                    ',' => CalcTokenKind.Comma,
                    _ => null,
                };

                if (opKind is null)
                {
                    error = new CalculatorError(
                        CalculatorErrorCode.UnexpectedCharacter,
                        start,
                        $"Unexpected character '{FormatCharForMessage(ch)}' at position {start}.");
                    return false;
                }

                index++;
                token = new CalcToken(opKind.Value, start, ch.ToString(), 0m);
            }

            if (tokens.Count == limits.MaxTokens)
            {
                error = new CalculatorError(
                    CalculatorErrorCode.TooManyTokens,
                    start,
                    $"Expression exceeds the limit of {limits.MaxTokens} tokens.");
                return false;
            }

            tokens.Add(token);
        }
    }

    private static bool TryReadNumber(
        string expression,
        ref int index,
        int start,
        out decimal value,
        out CalculatorError? error)
    {
        value = 0m;
        error = null;
        int length = expression.Length;

        int intStart = index;
        while (index < length && ExpressionCalculator.IsAsciiDigit(expression[index]))
        {
            index++;
        }

        int intEnd = index;

        bool hasFraction = index + 1 < length && expression[index] == '.' && ExpressionCalculator.IsAsciiDigit(expression[index + 1]);
        int fracStart = 0;
        int fracEnd = 0;
        if (hasFraction)
        {
            index++; // consume '.'
            fracStart = index;
            while (index < length && ExpressionCalculator.IsAsciiDigit(expression[index]))
            {
                index++;
            }

            fracEnd = index;
        }

        int fracLen = hasFraction ? fracEnd - fracStart : 0;
        if (fracLen > MaxDecimalFractionalDigits)
        {
            error = new CalculatorError(
                CalculatorErrorCode.NumberOutOfRange,
                start,
                $"Number at position {start} exceeds {MaxDecimalFractionalDigits} fractional digits.");
            return false;
        }

        int firstNonZeroInt = intStart;
        while (firstNonZeroInt < intEnd && expression[firstNonZeroInt] == '0')
        {
            firstNonZeroInt++;
        }

        int intSigLen = intEnd - firstNonZeroInt;
        if (intSigLen == 0)
        {
            int firstNonZeroFrac = fracStart;
            while (firstNonZeroFrac < fracEnd && expression[firstNonZeroFrac] == '0')
            {
                firstNonZeroFrac++;
            }

            ReadOnlySpan<char> unscaledFrac = expression.AsSpan(firstNonZeroFrac, fracEnd - firstNonZeroFrac);
            if (unscaledFrac.Length > MaxDecimalSignificantDigits ||
                (unscaledFrac.Length > 0 && !decimal.TryParse(unscaledFrac, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
            {
                error = new CalculatorError(
                    CalculatorErrorCode.NumberOutOfRange,
                    start,
                    $"Number at position {start} is out of System.Decimal range.");
                return false;
            }
        }
        else
        {
            int unscaledLen = intSigLen + fracLen;
            if (unscaledLen > MaxDecimalSignificantDigits)
            {
                error = new CalculatorError(
                    CalculatorErrorCode.NumberOutOfRange,
                    start,
                    $"Number at position {start} exceeds {MaxDecimalSignificantDigits} significant digits.");
                return false;
            }

            Span<char> unscaled = stackalloc char[unscaledLen];
            expression.AsSpan(firstNonZeroInt, intSigLen).CopyTo(unscaled);
            if (fracLen > 0)
            {
                expression.AsSpan(fracStart, fracLen).CopyTo(unscaled[intSigLen..]);
            }

            if (!decimal.TryParse(unscaled, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                error = new CalculatorError(
                    CalculatorErrorCode.NumberOutOfRange,
                    start,
                    $"Number at position {start} is out of System.Decimal range.");
                return false;
            }
        }

        ReadOnlySpan<char> literal = expression.AsSpan(start, index - start);
        if (!decimal.TryParse(literal, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value))
        {
            error = new CalculatorError(
                CalculatorErrorCode.NumberOutOfRange,
                start,
                $"Number at position {start} is out of System.Decimal range.");
            return false;
        }

        return true;
    }

    private static string FormatCharForMessage(char ch)
        => char.IsControl(ch) ? $"U+{(int)ch:X4}" : ch.ToString();
}
