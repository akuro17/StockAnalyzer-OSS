#nullable enable
using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Calculator;
using Xunit;

namespace StockAnalyzer.Core.Tests.Calculator;

public sealed class CalculatorLexerTests
{
    private static readonly IReadOnlyDictionary<string, decimal> Empty = new Dictionary<string, decimal>(StringComparer.Ordinal);

    [Fact]
    public void WhitespaceAllowsAsciiSpaceAndTabOnly()
    {
        CalculatorOutcome ok = ExpressionCalculator.Evaluate(" \t 12 \t + \t 30 \t ", Empty, CalculatorLimits.Default);
        Assert.Null(ok.Error);
        Assert.Equal(42m, ok.Value);
    }

    [Theory]
    [InlineData("1\r+2", 1)]
    [InlineData("1\n+2", 1)]
    [InlineData("1\u00A0+2", 1)]
    [InlineData("1\v+2", 1)]
    [InlineData("1\f+2", 1)]
    [InlineData("\uFF11+1", 0)]
    [InlineData("1@2", 1)]
    public void DisallowedCharactersProduceUnexpectedCharacterAtIndex(string expression, int expectedPosition)
    {
        CalculatorOutcome outcome = ExpressionCalculator.Evaluate(expression, Empty, CalculatorLimits.Default);
        Assert.Null(outcome.Value);
        Assert.NotNull(outcome.Error);
        Assert.Equal(CalculatorErrorCode.UnexpectedCharacter, outcome.Error!.Code);
        Assert.Equal(expectedPosition, outcome.Error.Position);
        Assert.Null(outcome.Error.Token);
    }

    [Theory]
    [InlineData(".5", 0)]
    [InlineData("1.", 1)]
    [InlineData("1..2", 1)]
    [InlineData("1.2.3", 3)]
    public void InvalidDecimalDotPlacementsReportUnexpectedCharacter(string expression, int expectedPosition)
    {
        CalculatorOutcome outcome = ExpressionCalculator.Evaluate(expression, Empty, CalculatorLimits.Default);
        Assert.NotNull(outcome.Error);
        Assert.Equal(CalculatorErrorCode.UnexpectedCharacter, outcome.Error!.Code);
        Assert.Equal(expectedPosition, outcome.Error.Position);
    }

    [Theory]
    [InlineData("00001", "1")]
    [InlineData("000.5", "0.5")]
    [InlineData("0000000000000000000000000000001", "1")]
    [InlineData("0.0000000000000000000000000001", "0.0000000000000000000000000001")]
    [InlineData("79228162514264337593543950335", "79228162514264337593543950335")]
    [InlineData("7922816251426433759354395033.5", "7922816251426433759354395033.5")]
    public void ValidBoundaryNumbersAreParsedWithoutRounding(string expression, string expectedDecimalText)
    {
        decimal expected = decimal.Parse(expectedDecimalText, System.Globalization.CultureInfo.InvariantCulture);
        CalculatorOutcome outcome = ExpressionCalculator.Evaluate(expression, Empty, CalculatorLimits.Default);
        Assert.Null(outcome.Error);
        Assert.Equal(expected, outcome.Value);
    }

    [Theory]
    [InlineData("0.00000000000000000000000000001")] // 29 fractional digits
    [InlineData("79228162514264337593543950336")] // decimal.MaxValue + 1
    [InlineData("9.1234567890123456789012345678")] // 29 significant digits, unscaled > decimal.MaxValue
    [InlineData("7922816251426433759354395033.6")] // 29 significant digits, unscaled > decimal.MaxValue
    [InlineData("123456789012345678901234567890")] // 30 significant digits
    public void OutOfRangeNumbersFailWithNumberOutOfRangeWithoutSilentRounding(string expression)
    {
        CalculatorOutcome outcome = ExpressionCalculator.Evaluate(expression, Empty, CalculatorLimits.Default);
        Assert.NotNull(outcome.Error);
        Assert.Equal(CalculatorErrorCode.NumberOutOfRange, outcome.Error!.Code);
        Assert.Equal(0, outcome.Error.Position);
    }

    [Fact]
    public void SinglePassLexingChecksUnexpectedCharacterBeforeTooManyTokens()
    {
        string prefixError = "@" + string.Concat(System.Linq.Enumerable.Repeat("+1", 200));
        CalculatorOutcome outcome1 = ExpressionCalculator.Evaluate(prefixError, Empty, CalculatorLimits.Default);
        Assert.Equal(CalculatorErrorCode.UnexpectedCharacter, outcome1.Error!.Code);
        Assert.Equal(0, outcome1.Error.Position);

        // 256 valid tokens ("1" + 127 x "+1" = 255 tokens, + "+" = 256 tokens), followed by '@' at index 256
        string atLimit = "1" + string.Concat(System.Linq.Enumerable.Repeat("+1", 127)) + "+@";
        CalculatorOutcome outcome2 = ExpressionCalculator.Evaluate(atLimit, Empty, CalculatorLimits.Default);
        Assert.Equal(CalculatorErrorCode.UnexpectedCharacter, outcome2.Error!.Code);
        Assert.Equal(atLimit.Length - 1, outcome2.Error.Position);
    }
}
