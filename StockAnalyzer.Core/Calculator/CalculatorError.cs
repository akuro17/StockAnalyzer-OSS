using System;

namespace StockAnalyzer.Core.Calculator;

/// <summary>
/// Frozen error codes produced by <see cref="ExpressionCalculator"/>. Declaration order is append-only.
/// </summary>
public enum CalculatorErrorCode
{
    SyntaxError,
    UnexpectedCharacter,
    NumberOutOfRange,
    ExpressionTooLong,
    TooManyTokens,
    TooDeep,
    UndefinedName,
    UnknownFunction,
    WrongArgumentCount,
    DivisionByZero,
    ArithmeticOverflow,
    NonIntegerExponent,
    ExponentOutOfRange,
    DomainError,
    ConvergenceFailure,
    SingularMatrix,
    DimensionMismatch,
    BudgetExceeded,
    NonFiniteResult,
}

/// <summary>
/// Deterministic evaluation failure with 0-based UTF-16 character index and optional offending identifier token.
/// </summary>
public sealed record CalculatorError(
    CalculatorErrorCode Code,
    int Position,
    string Message,
    string? Token = null);

/// <summary>
/// Result of evaluating one expression. Exactly one of <see cref="Value"/> or <see cref="Error"/> is non-null.
/// </summary>
public readonly record struct CalculatorOutcome(decimal? Value, CalculatorError? Error);

/// <summary>
/// Maps <see cref="CalculatorErrorCode"/> members to their canonical UPPER_SNAKE_CASE wire names.
/// </summary>
public static class CalculatorErrorCodeNames
{
    public static string ToWireName(this CalculatorErrorCode code) => code switch
    {
        CalculatorErrorCode.SyntaxError => "SYNTAX_ERROR",
        CalculatorErrorCode.UnexpectedCharacter => "UNEXPECTED_CHARACTER",
        CalculatorErrorCode.NumberOutOfRange => "NUMBER_OUT_OF_RANGE",
        CalculatorErrorCode.ExpressionTooLong => "EXPRESSION_TOO_LONG",
        CalculatorErrorCode.TooManyTokens => "TOO_MANY_TOKENS",
        CalculatorErrorCode.TooDeep => "TOO_DEEP",
        CalculatorErrorCode.UndefinedName => "UNDEFINED_NAME",
        CalculatorErrorCode.UnknownFunction => "UNKNOWN_FUNCTION",
        CalculatorErrorCode.WrongArgumentCount => "WRONG_ARGUMENT_COUNT",
        CalculatorErrorCode.DivisionByZero => "DIVISION_BY_ZERO",
        CalculatorErrorCode.ArithmeticOverflow => "ARITHMETIC_OVERFLOW",
        CalculatorErrorCode.NonIntegerExponent => "NON_INTEGER_EXPONENT",
        CalculatorErrorCode.ExponentOutOfRange => "EXPONENT_OUT_OF_RANGE",
        CalculatorErrorCode.DomainError => "DOMAIN_ERROR",
        CalculatorErrorCode.ConvergenceFailure => "CONVERGENCE_FAILURE",
        CalculatorErrorCode.SingularMatrix => "SINGULAR_MATRIX",
        CalculatorErrorCode.DimensionMismatch => "DIMENSION_MISMATCH",
        CalculatorErrorCode.BudgetExceeded => "BUDGET_EXCEEDED",
        CalculatorErrorCode.NonFiniteResult => "NON_FINITE_RESULT",
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Unknown calculator error code."),
    };
}
