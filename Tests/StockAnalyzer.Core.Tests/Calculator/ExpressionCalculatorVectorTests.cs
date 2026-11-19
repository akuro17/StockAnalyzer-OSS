#nullable enable
using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Calculator;
using StockAnalyzer.Core.Models.Indicators;
using Xunit;

namespace StockAnalyzer.Core.Tests.Calculator;

public sealed class ExpressionCalculatorVectorTests
{
    private static readonly IReadOnlyDictionary<string, decimal> Empty = new Dictionary<string, decimal>(StringComparer.Ordinal);

    public static IReadOnlyList<object?[]> AllVectors { get; } = BuildVectors();

    private static IReadOnlyList<object?[]> BuildVectors()
    {
        var list = new List<object?[]>();

        void Ok(string id, string expr, decimal expected, IReadOnlyDictionary<string, decimal>? vars = null)
            => list.Add(new object?[] { id, expr, vars, true, expected, null, null, null });

        void Err(string id, string expr, CalculatorErrorCode code, int pos, string? token = null, IReadOnlyDictionary<string, decimal>? vars = null)
            => list.Add(new object?[] { id, expr, vars, false, 0m, code, pos, token });

        Ok("01", "1+2*3", 7m);
        Ok("02", "(1+2)*3", 9m);
        Ok("03", "-2^2", -4m);
        Ok("04", "2^-2", 0.25m);
        Ok("05", "2^3^2", 512m);
        Ok("06", "0^0", 1m);
        Err("07", "0^-1", CalculatorErrorCode.DivisionByZero, 1);
        Err("08", "2^0.5", CalculatorErrorCode.NonIntegerExponent, 1);
        Err("09", "2^65", CalculatorErrorCode.ExponentOutOfRange, 1);
        Ok("10", "7/2", 3.5m);
        Err("11", "1/0", CalculatorErrorCode.DivisionByZero, 1);
        Ok("12", "10%3", 1m);
        Ok("13", "-10%3", -1m);
        Err("14", "1 % 0", CalculatorErrorCode.DivisionByZero, 2);
        Ok("15", "abs(-3)", 3m);
        Ok("16a", "sign(-3)", -1m);
        Ok("16b", "sign(0)", 0m);
        Ok("17a", "min(3,1,2)", 1m);
        Ok("17b", "max(3,1,2)", 3m);
        Err("18", "min(1)", CalculatorErrorCode.WrongArgumentCount, 0, "min");
        Ok("19", "clamp(5,1,3)", 3m);
        Err("20", "clamp(5,3,1)", CalculatorErrorCode.DomainError, 0, "clamp");
        Ok("21a", "floor(-1.5)", -2m);
        Ok("21b", "ceil(-1.5)", -1m);
        Ok("21c", "trunc(-1.5)", -1m);
        Ok("22a", "round(2.5,0)", 3m);
        Ok("22b", "round(-2.5,0)", -3m);
        Ok("23", "round(1.005,2)", 1.01m);
        Err("24", "round(1,11)", CalculatorErrorCode.DomainError, 0, "round");
        Ok("25", "change_ratio(100,110)", 0.1m);
        Err("26", "change_ratio(0,1)", CalculatorErrorCode.DivisionByZero, 0);
        Err("27", "79228162514264337593543950335 + 1", CalculatorErrorCode.ArithmeticOverflow, 30);
        Err("28", "x + 1", CalculatorErrorCode.UndefinedName, 0, "x");
        Err("29", "foo(1)", CalculatorErrorCode.UnknownFunction, 0, "foo");
        Err("30", "ABS(1)", CalculatorErrorCode.UnknownFunction, 0, "ABS");
        Err("31", "1 2", CalculatorErrorCode.SyntaxError, 2);
        Err("32", "1e5", CalculatorErrorCode.SyntaxError, 1);
        Err("33a", "", CalculatorErrorCode.SyntaxError, 0);
        Err("33b", "   ", CalculatorErrorCode.SyntaxError, 3);
        Err("34", "1 +", CalculatorErrorCode.SyntaxError, 3);
        Err("35", new string('(', 17) + "1" + new string(')', 17), CalculatorErrorCode.TooDeep, 16);
        Err("36", "1" + string.Concat(System.Linq.Enumerable.Repeat("+1", 256)), CalculatorErrorCode.ExpressionTooLong, 512);
        Err("37", "1" + string.Concat(System.Linq.Enumerable.Repeat("+1", 128)), CalculatorErrorCode.TooManyTokens, 256);
        Err("38", "0.12345678901234567890123456789", CalculatorErrorCode.NumberOutOfRange, 0);
        Ok("39", "1\t+\t2", 3m);
        Err("40", "1\n+2", CalculatorErrorCode.UnexpectedCharacter, 1);
        Err("41", "\uFF11+1", CalculatorErrorCode.UnexpectedCharacter, 0);
        Ok("42", "0.1 + 0.2", 0.3m);
        Ok("43", "1/3", 0.3333333333333333333333333333m);
        Ok("44", "1/3*3", 0.9999999999999999999999999999m);
        Ok("45", "close - 2 * atr", 2853m, new Dictionary<string, decimal>(StringComparer.Ordinal) { ["close"] = 2950m, ["atr"] = 48.5m });
        Ok("46", "high - (high - low) * 0.618", 2848.08m, new Dictionary<string, decimal>(StringComparer.Ordinal) { ["high"] = 3120m, ["low"] = 2680m });
        Err("47", "(", CalculatorErrorCode.SyntaxError, 1);
        Err("48", "max(1,)", CalculatorErrorCode.SyntaxError, 6);
        Err("49", "0.1 ^ -29", CalculatorErrorCode.ArithmeticOverflow, 4);
        Ok("50", "0.1 ^ 29", 0m);
        Err("51", "9.1234567890123456789012345678", CalculatorErrorCode.NumberOutOfRange, 0);
        Err("52a", "1.", CalculatorErrorCode.UnexpectedCharacter, 1);
        Err("52b", ".5", CalculatorErrorCode.UnexpectedCharacter, 0);
        Err("53", "change_ratio(0.0000000000000000000000000001, 1000)", CalculatorErrorCode.ArithmeticOverflow, 0);
        Err("54a", "foo(1/0)", CalculatorErrorCode.UnknownFunction, 0, "foo");
        Err("54b", "min(1/0)", CalculatorErrorCode.WrongArgumentCount, 0, "min");
        Err("55", "1/0 + foo(1)", CalculatorErrorCode.DivisionByZero, 1);
        Err("56a", "0 ^ -65", CalculatorErrorCode.ExponentOutOfRange, 2);
        Err("56b", "0 ^ -0.5", CalculatorErrorCode.NonIntegerExponent, 2);

        // Calculator v2 pure-decimal algebraic & aggregate vectors (57a..64e)
        Ok("57a", "mod(10, 3)", 1m);
        Ok("57b", "mod(-10, 3)", 2m);
        Ok("57c", "mod(10, -3)", -2m);
        Ok("57d", "mod(-10, -3)", -1m);
        Ok("57e", "mod(-6, 3)", 0m);
        Ok("57f", "mod(2953.7, 5)", 3.7m);
        Ok("57g", "mod(-0.0000000000000000000000000001, 3)", 2.9999999999999999999999999999m);
        Ok("57h", "mod(-0.0000000000000000000000000001, 10)", 0m);
        Ok("57i", "mod(79228162514264337593543950335, 0.5)", 0m);
        Err("58", "mod(1, 0)", CalculatorErrorCode.DivisionByZero, 0);
        Ok("59a", "sum(1, 2, 3, 4)", 10m);
        Ok("59b", "sum(1, 2, 3, 4, 5, 6, 7, 8)", 36m);
        Err("60a", "sum(1)", CalculatorErrorCode.WrongArgumentCount, 0, "sum");
        Err("60b", "sum(1,2,3,4,5,6,7,8,9)", CalculatorErrorCode.WrongArgumentCount, 0, "sum");
        Err("61", "sum(79228162514264337593543950335, 1)", CalculatorErrorCode.ArithmeticOverflow, 0);
        Ok("62a", "avg(10, 20, 30)", 20m);
        Ok("62b", "avg(1, 1, 1)", 1m);
        Ok("62c", "avg(1, 2)", 1.5m);
        Err("63a", "avg(1)", CalculatorErrorCode.WrongArgumentCount, 0, "avg");
        Err("63b", "avg(79228162514264337593543950335, 79228162514264337593543950335)", CalculatorErrorCode.ArithmeticOverflow, 0);
        Ok("64a", "frac(3.75)", 0.75m);
        Ok("64b", "frac(-3.75)", -0.75m);
        Ok("64c", "frac(5)", 0m);
        Ok("64d", "frac(-5.0)", 0m);
        Ok("64e", "frac(-1.25)", -0.25m);

        // Calculator v2 transcendental & pow vectors (65a..72g)
        Ok("65a", "sqrt(0)", 0m);
        Ok("65b", "sqrt(1)", 1m);
        Ok("65c", "sqrt(9)", 3m);
        Ok("65d", "sqrt(0.25)", 0.5m);
        Ok("65e", "sqrt(2)", IndicatorCalculationHelper.ToDecimalOrNull(Math.Sqrt(2.0))!.Value);
        Err("66", "sqrt(-1)", CalculatorErrorCode.DomainError, 0, "sqrt");
        Ok("67a", "ln(1)", 0m);
        Ok("67b", "ln(2.718)", IndicatorCalculationHelper.ToDecimalOrNull(Math.Log(2.718))!.Value);
        Err("68a", "ln(0)", CalculatorErrorCode.DomainError, 0, "ln");
        Err("68b", "ln(-2)", CalculatorErrorCode.DomainError, 0, "ln");
        Ok("69a", "exp(0)", 1m);
        Ok("69b", "exp(1)", IndicatorCalculationHelper.ToDecimalOrNull(Math.Exp(1.0))!.Value);
        Ok("69c", "exp(64)", IndicatorCalculationHelper.ToDecimalOrNull(Math.Exp(64.0))!.Value);
        Ok("69d", "exp(-1000)", 0m);
        Err("70a", "exp(65)", CalculatorErrorCode.ArithmeticOverflow, 0);
        Err("70b", "exp(100)", CalculatorErrorCode.ArithmeticOverflow, 0);
        Err("70c", "exp(1000)", CalculatorErrorCode.ArithmeticOverflow, 0);
        Ok("71a", "pow(0, 0)", 1m);
        Ok("71b", "pow(0, 2.5)", 0m);
        Ok("71c", "pow(1, 1000000000)", 1m);
        Ok("71d", "pow(-1, 9007199254740993)", -1m);
        Ok("71e", "pow(-1, 9007199254740992)", 1m);
        Ok("71f", "pow(1.05, 10)", 1.62889462677744140625m);
        Ok("71g", "pow(4, 0.5)", 2m);
        Ok("71h", "pow(-8, 3)", -512m);
        Ok("71i", "pow(-0.999, 65)", -IndicatorCalculationHelper.ToDecimalOrNull(Math.Pow(0.999, 65.0))!.Value);
        Ok("71j", "pow(0.1, 100)", 0m);
        Err("72a", "pow(0, -1)", CalculatorErrorCode.DivisionByZero, 0);
        Err("72b", "pow(0, -0.5)", CalculatorErrorCode.DivisionByZero, 0);
        Err("72c", "pow(-2, 0.5)", CalculatorErrorCode.DomainError, 0, "pow");
        Err("72d", "pow(-2, -0.5)", CalculatorErrorCode.DomainError, 0, "pow");
        Err("72e", "pow(10, 28.5)", CalculatorErrorCode.ArithmeticOverflow, 0);
        Err("72f", "pow(2, 10000)", CalculatorErrorCode.ArithmeticOverflow, 0);
        Err("72g", "pow(10, 29)", CalculatorErrorCode.ArithmeticOverflow, 0);

        return list;
    }

    [Theory]
    [MemberData(nameof(AllVectors))]
    public void SharedGoldenVectorMatchesContract(
        string id,
        string expression,
        IReadOnlyDictionary<string, decimal>? variables,
        bool expectOk,
        decimal expectedValue,
        CalculatorErrorCode? expectedCode,
        int? expectedPos,
        string? expectedToken)
    {
        CalculatorOutcome outcome = ExpressionCalculator.Evaluate(expression, variables ?? Empty, CalculatorLimits.Default);

        if (expectOk)
        {
            Assert.True(outcome.Error is null, $"Vector {id} expected Ok({expectedValue}) but got {outcome.Error}");
            Assert.True(outcome.Value == expectedValue, $"Vector {id} expected {expectedValue} but got {outcome.Value}");
        }
        else
        {
            Assert.True(outcome.Value is null, $"Vector {id} expected Error({expectedCode}) but got {outcome.Value}");
            Assert.NotNull(outcome.Error);
            Assert.True(outcome.Error!.Code == expectedCode, $"Vector {id} code mismatch: expected {expectedCode}, got {outcome.Error.Code}");
            Assert.True(outcome.Error.Position == expectedPos, $"Vector {id} position mismatch: expected {expectedPos}, got {outcome.Error.Position}");
            Assert.Equal(expectedToken, outcome.Error.Token);
        }
    }
}
