using System;
using StockAnalyzer.Core.Models.Indicators;

namespace StockAnalyzer.Core.Calculator;

/// <summary>
/// Isolated IEEE-754 <see cref="double"/> bridge for transcendental calculator functions
/// (<c>sqrt</c>, <c>ln</c>, <c>exp</c>, and non-trivial <c>pow</c>), narrowing every result once through
/// <see cref="IndicatorCalculationHelper.ToDecimalOrNull"/> per repository domain invariants.
/// </summary>
internal static class CalculatorTranscendentals
{
    public static CalculatorOutcome EvaluateSqrt(int position, decimal x)
    {
        // Precondition runs in decimal before Math.Sqrt so negative inputs yield DOMAIN_ERROR rather than NaN -> ARITHMETIC_OVERFLOW.
        if (x < 0m)
        {
            return CalculatorEvaluator.Fail(CalculatorErrorCode.DomainError, position, "sqrt requires a non-negative argument.", "sqrt");
        }

        if (x == 0m)
        {
            return CalculatorEvaluator.Ok(0m);
        }

        if (x == 1m)
        {
            return CalculatorEvaluator.Ok(1m);
        }

        return NarrowOrOverflow(position, "sqrt", Math.Sqrt((double)x));
    }

    public static CalculatorOutcome EvaluateLn(int position, decimal x)
    {
        // Precondition runs in decimal before Math.Log so x <= 0 yields DOMAIN_ERROR rather than -Infinity / NaN -> ARITHMETIC_OVERFLOW.
        if (x <= 0m)
        {
            return CalculatorEvaluator.Fail(CalculatorErrorCode.DomainError, position, "ln requires a strictly positive argument.", "ln");
        }

        if (x == 1m)
        {
            return CalculatorEvaluator.Ok(0m);
        }

        return NarrowOrOverflow(position, "ln", Math.Log((double)x));
    }

    public static CalculatorOutcome EvaluateExp(int position, decimal x)
    {
        if (x == 0m)
        {
            return CalculatorEvaluator.Ok(1m);
        }

        return NarrowOrOverflow(position, "exp", Math.Exp((double)x));
    }

    public static CalculatorOutcome EvaluatePow(int position, decimal x, decimal y, CalculatorLimits limits)
    {
        if (y == 0m)
        {
            return CalculatorEvaluator.Ok(1m);
        }

        if (x == 0m)
        {
            return y < 0m
                ? CalculatorEvaluator.Fail(
                    CalculatorErrorCode.DivisionByZero,
                    position,
                    "Division by zero in pow with zero base and negative exponent.")
                : CalculatorEvaluator.Ok(0m);
        }

        if (x == 1m)
        {
            return CalculatorEvaluator.Ok(1m);
        }

        bool isIntegerExponent = y == decimal.Truncate(y);
        if (x < 0m && !isIntegerExponent)
        {
            return CalculatorEvaluator.Fail(
                CalculatorErrorCode.DomainError,
                position,
                "pow with negative base requires an integer exponent.",
                "pow");
        }

        if (x == -1m)
        {
            // Parity computed in decimal to avoid IEEE-754 precision loss above 2^53.
            return CalculatorEvaluator.Ok((y % 2m) == 0m ? 1m : -1m);
        }

        if (isIntegerExponent && Math.Abs(y) <= limits.MaxIntegerExponent)
        {
            // No fallback: integer exponents within MaxIntegerExponent must return EvaluateIntegerPower outcome directly.
            return CalculatorEvaluator.EvaluateIntegerPower(position, x, y);
        }

        double magnitude = Math.Pow((double)Math.Abs(x), (double)y);
        decimal? narrowed = IndicatorCalculationHelper.ToDecimalOrNull(magnitude);
        if (narrowed is not { } result)
        {
            return CalculatorEvaluator.Fail(
                CalculatorErrorCode.ArithmeticOverflow,
                position,
                $"Arithmetic overflow in 'pow' at position {position}.");
        }

        if (x < 0m && (y % 2m) != 0m)
        {
            result = -result;
        }

        return CalculatorEvaluator.Ok(result);
    }

    private static CalculatorOutcome NarrowOrOverflow(int position, string functionName, double computed)
    {
        decimal? narrowed = IndicatorCalculationHelper.ToDecimalOrNull(computed);
        if (narrowed is not { } result)
        {
            return CalculatorEvaluator.Fail(
                CalculatorErrorCode.ArithmeticOverflow,
                position,
                $"Arithmetic overflow in '{functionName}' at position {position}.");
        }

        return CalculatorEvaluator.Ok(result);
    }
}
