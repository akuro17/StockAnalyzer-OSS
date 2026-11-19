#nullable enable
using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Calculator;
using Xunit;

namespace StockAnalyzer.Core.Tests.Calculator;

public sealed class CalculatorEvaluatorSemanticsTests
{
    private static readonly IReadOnlyDictionary<string, decimal> Empty = new Dictionary<string, decimal>(StringComparer.Ordinal);

    [Theory]
    [InlineData("8-3-2", 3)]
    [InlineData("2^3^2", 512)]
    [InlineData("100/10/5", 2)]
    [InlineData("-2^2", -4)]
    [InlineData("(-2)^2", 4)]
    [InlineData("2^-1", 0.5)]
    [InlineData("-2^-2", -0.25)]
    [InlineData("--2^2", 4)]
    [InlineData("+5", 5)]
    [InlineData("trunc(0.9)", 0)]
    [InlineData("round(0.5, 0)", 1)]
    public void AssociativityPrecedenceAndFunctionSemantics(string expression, decimal expected)
    {
        CalculatorOutcome outcome = ExpressionCalculator.Evaluate(expression, Empty, CalculatorLimits.Default);
        Assert.Null(outcome.Error);
        Assert.Equal(expected, outcome.Value);
    }

    [Fact]
    public void RoundMinusZeroProducesPlainPositiveZero()
    {
        CalculatorOutcome outcome = ExpressionCalculator.Evaluate("round(-0.4, 0)", Empty, CalculatorLimits.Default);
        Assert.Null(outcome.Error);
        Assert.Equal(0m, outcome.Value);
        int[] bits = decimal.GetBits(outcome.Value!.Value);
        Assert.Equal(0, bits[3]);
    }

    [Fact]
    public void NamesLookupIsCaseSensitiveAndCopiesNonOrdinalDictionaries()
    {
        var ignoreCase = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
        {
            ["x"] = 10m,
            ["my_var_1"] = 5m,
        };

        CalculatorOutcome ok = ExpressionCalculator.Evaluate("x + my_var_1", ignoreCase, CalculatorLimits.Default);
        Assert.Equal(15m, ok.Value);

        CalculatorOutcome missingUpper = ExpressionCalculator.Evaluate("X + 1", ignoreCase, CalculatorLimits.Default);
        Assert.Equal(CalculatorErrorCode.UndefinedName, missingUpper.Error!.Code);
        Assert.Equal("X", missingUpper.Error.Token);
        Assert.Equal(0, missingUpper.Error.Position);
    }

    [Fact]
    public void NameEqualToFunctionNameDoesNotShadowBuiltInCall()
    {
        var names = new Dictionary<string, decimal>(StringComparer.Ordinal) { ["abs"] = 99m };

        CalculatorOutcome callOutcome = ExpressionCalculator.Evaluate("abs(-1)", names, CalculatorLimits.Default);
        Assert.Equal(1m, callOutcome.Value);

        CalculatorOutcome nameOutcome = ExpressionCalculator.Evaluate("abs + 1", names, CalculatorLimits.Default);
        Assert.Equal(100m, nameOutcome.Value);
    }

    [Fact]
    public void ExponentBoundariesMatchExactDecimalArithmetic()
    {
        decimal expectedPow64 = 1.1m;
        for (int i = 0; i < 6; i++)
        {
            expectedPow64 *= expectedPow64;
        }

        CalculatorOutcome pow64 = ExpressionCalculator.Evaluate("1.1^64", Empty, CalculatorLimits.Default);
        Assert.Null(pow64.Error);
        Assert.Equal(expectedPow64, pow64.Value);
        Assert.Equal(445.7915684526m, decimal.Round(pow64.Value!.Value, 10, MidpointRounding.AwayFromZero));

        CalculatorOutcome pow28 = ExpressionCalculator.Evaluate("10^28", Empty, CalculatorLimits.Default);
        Assert.Equal(10_000_000_000_000_000_000_000_000_000m, pow28.Value);

        CalculatorOutcome pow29 = ExpressionCalculator.Evaluate("10^29", Empty, CalculatorLimits.Default);
        Assert.Equal(CalculatorErrorCode.ArithmeticOverflow, pow29.Error!.Code);
        Assert.Equal(2, pow29.Error.Position);

        CalculatorOutcome divOverflow = ExpressionCalculator.Evaluate("79228162514264337593543950335 / 0.1", Empty, CalculatorLimits.Default);
        Assert.Equal(CalculatorErrorCode.ArithmeticOverflow, divOverflow.Error!.Code);
        Assert.Equal(30, divOverflow.Error.Position);
    }

    [Fact]
    public void DepthLimitsApplyToUnaryCallAndPowerChains()
    {
        string unaryChain = new string('-', 17) + "1";
        CalculatorOutcome unaryErr = ExpressionCalculator.Evaluate(unaryChain, Empty, CalculatorLimits.Default);
        Assert.Equal(CalculatorErrorCode.TooDeep, unaryErr.Error!.Code);
        Assert.Equal(16, unaryErr.Error.Position);

        var depth1 = new CalculatorLimits { MaxNestingDepth = 1 };
        CalculatorOutcome callErr = ExpressionCalculator.Evaluate("abs(abs(1))", Empty, depth1);
        Assert.Equal(CalculatorErrorCode.TooDeep, callErr.Error!.Code);
        Assert.Equal(7, callErr.Error.Position);

        string powerChain = string.Concat(System.Linq.Enumerable.Repeat("1^", 17)) + "1";
        CalculatorOutcome powerErr = ExpressionCalculator.Evaluate(powerChain, Empty, CalculatorLimits.Default);
        Assert.Equal(CalculatorErrorCode.TooDeep, powerErr.Error!.Code);
        Assert.Equal(33, powerErr.Error.Position);
    }

    [Fact]
    public void CustomMaxFunctionArgumentsCapsVariadicAndFixedFunctions()
    {
        var cap2 = new CalculatorLimits { MaxFunctionArguments = 2 };

        CalculatorOutcome minErr = ExpressionCalculator.Evaluate("min(1,2,3)", Empty, cap2);
        Assert.Equal(CalculatorErrorCode.WrongArgumentCount, minErr.Error!.Code);
        Assert.Equal("min", minErr.Error.Token);

        CalculatorOutcome clampErr = ExpressionCalculator.Evaluate("clamp(1,0,2)", Empty, cap2);
        Assert.Equal(CalculatorErrorCode.WrongArgumentCount, clampErr.Error!.Code);
        Assert.Equal("clamp", clampErr.Error.Token);

        CalculatorOutcome sumErr = ExpressionCalculator.Evaluate("sum(1,2,3)", Empty, cap2);
        Assert.Equal(CalculatorErrorCode.WrongArgumentCount, sumErr.Error!.Code);
        Assert.Equal("sum", sumErr.Error.Token);

        CalculatorOutcome avgErr = ExpressionCalculator.Evaluate("avg(1,2,3)", Empty, cap2);
        Assert.Equal(CalculatorErrorCode.WrongArgumentCount, avgErr.Error!.Code);
        Assert.Equal("avg", avgErr.Error.Token);
    }

    [Theory]
    [InlineData("-0")]
    [InlineData("-0.0")]
    [InlineData("0.0 - 0.0")]
    [InlineData("trunc(-0.5)")]
    [InlineData("ceil(-0.5)")]
    [InlineData("floor(-0.0)")]
    [InlineData("abs(-0.0)")]
    [InlineData("min(-0.0, 0)")]
    [InlineData("0 * -1")]
    [InlineData("frac(-5.0)")]
    [InlineData("mod(-6, 3)")]
    [InlineData("sum(-0.0, -0.0)")]
    [InlineData("avg(-1, 1)")]
    [InlineData("ln(1)")]
    [InlineData("sqrt(0)")]
    [InlineData("exp(-1000)")]
    [InlineData("pow(0.1, 100)")]
    public void V2ZeroResultsNormalizeToPlainPositiveZero(string expression)
    {
        CalculatorOutcome outcome = ExpressionCalculator.Evaluate(expression, Empty, CalculatorLimits.Default);
        Assert.Null(outcome.Error);
        Assert.Equal(0m, outcome.Value);
        Assert.Equal(0, decimal.GetBits(outcome.Value!.Value)[3]);
    }

    [Theory]
    [InlineData("3.75")]
    [InlineData("-1.25")]
    [InlineData("-0.0001")]
    [InlineData("0")]
    [InlineData("79228162514264337593543950335")]
    [InlineData("-79228162514264337593543950335")]
    public void FracSatisfiesTruncPlusFracIdentity(string xLiteral)
    {
        var vars = new Dictionary<string, decimal>(StringComparer.Ordinal)
        {
            ["x"] = decimal.Parse(xLiteral, System.Globalization.CultureInfo.InvariantCulture),
        };
        CalculatorOutcome sum = ExpressionCalculator.Evaluate("trunc(x) + frac(x)", vars, CalculatorLimits.Default);
        Assert.Null(sum.Error);
        Assert.Equal(vars["x"], sum.Value);
    }

    [Theory]
    [InlineData("-0.0000000000000000000000000001", "3")]
    [InlineData("-0.0000000000000000000000000001", "10")]
    [InlineData("0.0000000000000000000000000001", "-3")]
    [InlineData("-100", "7")]
    [InlineData("100", "-7")]
    public void ModSatisfiesHalfOpenDivisorInterval(string aText, string bText)
    {
        decimal a = decimal.Parse(aText, System.Globalization.CultureInfo.InvariantCulture);
        decimal b = decimal.Parse(bText, System.Globalization.CultureInfo.InvariantCulture);
        var vars = new Dictionary<string, decimal>(StringComparer.Ordinal) { ["a"] = a, ["b"] = b };

        CalculatorOutcome outcome = ExpressionCalculator.Evaluate("mod(a, b)", vars, CalculatorLimits.Default);
        Assert.Null(outcome.Error);
        decimal r = outcome.Value!.Value;
        if (b > 0m)
        {
            Assert.True(r >= 0m && r < b, $"Expected 0 <= {r} < {b}");
        }
        else
        {
            Assert.True(r <= 0m && r > b, $"Expected {b} < {r} <= 0");
        }
    }

    [Fact]
    public void PowUsesExactIntegerDelegationWithNoFallbackOnOverflow()
    {
        CalculatorOutcome caretOk = ExpressionCalculator.Evaluate("1.05^10", Empty, CalculatorLimits.Default);
        CalculatorOutcome powOk = ExpressionCalculator.Evaluate("pow(1.05, 10)", Empty, CalculatorLimits.Default);
        Assert.Null(caretOk.Error);
        Assert.Equal(caretOk.Value, powOk.Value);

        CalculatorOutcome pow28 = ExpressionCalculator.Evaluate("pow(10, 28)", Empty, CalculatorLimits.Default);
        Assert.Null(pow28.Error);
        Assert.Equal(10_000_000_000_000_000_000_000_000_000m, pow28.Value);

        CalculatorOutcome pow29 = ExpressionCalculator.Evaluate("pow(10, 29)", Empty, CalculatorLimits.Default);
        Assert.Equal(CalculatorErrorCode.ArithmeticOverflow, pow29.Error!.Code);
        Assert.Equal(0, pow29.Error.Position);

        CalculatorOutcome powMaxSq = ExpressionCalculator.Evaluate("pow(79228162514264337593543950335, 2)", Empty, CalculatorLimits.Default);
        Assert.Equal(CalculatorErrorCode.ArithmeticOverflow, powMaxSq.Error!.Code);
        Assert.Equal(0, powMaxSq.Error.Position);

        // Above custom MaxIntegerExponent, pow bridges to Math.Pow while ^ rejects ExponentOutOfRange.
        var cap2 = new CalculatorLimits { MaxIntegerExponent = 2 };
        CalculatorOutcome caretRejected = ExpressionCalculator.Evaluate("1.05^3", Empty, cap2);
        Assert.Equal(CalculatorErrorCode.ExponentOutOfRange, caretRejected.Error!.Code);
        CalculatorOutcome powBridged = ExpressionCalculator.Evaluate("pow(1.05, 3)", Empty, cap2);
        Assert.Null(powBridged.Error);
    }
}
