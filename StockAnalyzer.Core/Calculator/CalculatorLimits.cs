using System;

namespace StockAnalyzer.Core.Calculator;

/// <summary>
/// Structural and evaluation safety bounds for <see cref="ExpressionCalculator"/>.
/// Every property is bounded within [1, MaxAllowed*] by <see cref="Validate"/>.
/// </summary>
public sealed record CalculatorLimits
{
    public const int MaxAllowedExpressionChars = 4096;
    public const int MaxAllowedTokens = 2048;
    public const int MaxAllowedNestingDepth = 64;
    public const int MaxAllowedIntegerExponent = 1024;
    public const int MaxAllowedFunctionArguments = 64;
    public const int MaxAllowedExecutionOps = 1_000_000;
    public const int MaxAllowedArrayLiteralElements = 512;
    public const int MinAllowedArenaElements = MathUtils.DslArrayArena.DefaultMaxArenaElements;
    public const int MaxAllowedArenaElements = MathUtils.DslArrayArena.MaxAllowedArenaElements;

    public static CalculatorLimits Default { get; } = new();

    public int MaxExpressionChars { get; init; } = 512;

    public int MaxTokens { get; init; } = 256;

    public int MaxNestingDepth { get; init; } = 16;

    public int MaxIntegerExponent { get; init; } = 64;

    public int MaxFunctionArguments { get; init; } = 8;

    public int MaxStatisticalFunctionArguments { get; init; } = 32;

    public int MaxVariadicFunctionArguments { get; init; } = 64;

    public int MaxArrayLiteralElements { get; init; } = 512;

    public int MaxExecutionOps { get; init; } = 10_000;

    public int MaxArenaElements { get; init; } = MathUtils.DslArrayArena.DefaultMaxArenaElements;

    /// <summary>
    /// Validates that every configured limit is within its allowed range.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when any limit falls outside its allowed range.</exception>
    public void Validate()
    {
        ValidateRange(MaxExpressionChars, MaxAllowedExpressionChars, nameof(MaxExpressionChars));
        ValidateRange(MaxTokens, MaxAllowedTokens, nameof(MaxTokens));
        ValidateRange(MaxNestingDepth, MaxAllowedNestingDepth, nameof(MaxNestingDepth));
        ValidateRange(MaxIntegerExponent, MaxAllowedIntegerExponent, nameof(MaxIntegerExponent));
        ValidateRange(MaxFunctionArguments, MaxAllowedFunctionArguments, nameof(MaxFunctionArguments));
        ValidateRange(MaxStatisticalFunctionArguments, MaxAllowedFunctionArguments, nameof(MaxStatisticalFunctionArguments));
        ValidateRange(MaxVariadicFunctionArguments, MaxAllowedFunctionArguments, nameof(MaxVariadicFunctionArguments));
        ValidateRange(MaxArrayLiteralElements, MaxAllowedArrayLiteralElements, nameof(MaxArrayLiteralElements));
        ValidateRange(MaxExecutionOps, MaxAllowedExecutionOps, nameof(MaxExecutionOps));
        if (MaxArenaElements < MinAllowedArenaElements || MaxArenaElements > MaxAllowedArenaElements)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxArenaElements),
                MaxArenaElements,
                $"{nameof(MaxArenaElements)} must be between {MinAllowedArenaElements} and {MaxAllowedArenaElements}.");
        }
    }

    private static void ValidateRange(int value, int maxAllowed, string paramName)
    {
        if (value < 1 || value > maxAllowed)
        {
            throw new ArgumentOutOfRangeException(paramName, value, $"{paramName} must be between 1 and {maxAllowed}.");
        }
    }
}
