#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Calculator;
using Xunit;

namespace StockAnalyzer.Core.Tests.Calculator;

/// <summary>
/// Shared helpers for the DSL v7 test classes: single-expression evaluation through <see cref="ExpressionCalculator.EvaluateBatch"/>
/// with the default limits, plus negative-zero checks. Older Calculator test classes keep their own private copies.
/// </summary>
internal static class CalculatorDslTestHelpers
{
    internal static readonly IReadOnlyDictionary<string, decimal> Empty =
        new Dictionary<string, decimal>(StringComparer.Ordinal);

    /// <summary>Raises only the expression size and execution budget; the nesting depth stays at the default.</summary>
    internal static readonly CalculatorLimits Heavy = new()
    {
        MaxExecutionOps = 800_000,
        MaxExpressionChars = 2048,
        MaxTokens = 1024,
    };

    /// <summary>A negative zero (sign bit set, value zero).</summary>
    internal static decimal NegativeZero => new(0, 0, 0, true, 0);

    internal static ExpressionCalculator.CalculatorBatchResult Run(string expression, CalculatorLimits? limits = null)
    {
        var items = new[] { new ExpressionCalculator.CalculatorBatchItem("r", expression) };
        return ExpressionCalculator.EvaluateBatch(Empty, null, items, limits ?? CalculatorLimits.Default)[0];
    }

    internal static decimal Scalar(string expression, CalculatorLimits? limits = null)
    {
        ExpressionCalculator.CalculatorBatchResult result = Run(expression, limits);
        Assert.True(result.IsOk, $"{expression} => {result.Error?.Code}: {result.Error?.Message}");
        Assert.False(result.IsArray, $"{expression} returned an array.");
        return result.ScalarValue;
    }

    internal static decimal[] Arr(string expression, CalculatorLimits? limits = null)
    {
        ExpressionCalculator.CalculatorBatchResult result = Run(expression, limits);
        Assert.True(result.IsOk, $"{expression} => {result.Error?.Code}: {result.Error?.Message}");
        Assert.True(result.IsArray, $"{expression} returned a scalar.");
        return result.ArrayValues!.ToArray();
    }

    internal static CalculatorError Fails(string expression, CalculatorErrorCode expected, CalculatorLimits? limits = null)
    {
        ExpressionCalculator.CalculatorBatchResult result = Run(expression, limits);
        Assert.False(result.IsOk, $"{expression} unexpectedly succeeded.");
        Assert.Equal(expected, result.Error!.Code);
        return result.Error;
    }

    internal static void AssertNoNegativeZero(IEnumerable<decimal> values, string context)
    {
        foreach (decimal value in values)
        {
            if (value == 0m)
            {
                Assert.True(decimal.GetBits(value)[3] == 0, $"{context}: negative zero or scaled zero leaked into the output.");
            }
        }
    }
}
