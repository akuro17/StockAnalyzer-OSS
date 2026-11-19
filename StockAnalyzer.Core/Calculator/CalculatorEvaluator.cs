using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using StockAnalyzer.Core.MathUtils;

namespace StockAnalyzer.Core.Calculator;

internal readonly record struct BoundVariable(string Name, decimal Value);

[InlineArray(DslExecutionBudget.DefaultMaxDepth)]
internal struct BoundVariableBuffer
{
    public BoundVariable Element0;
}

internal struct BoundVariableScope
{
    public BoundVariableBuffer Items;
    public int Count;

    public readonly bool TryGetValue(string name, out decimal value)
    {
        for (int i = Count - 1; i >= 0; i--)
        {
            if (string.Equals(Items[i].Name, name, StringComparison.Ordinal))
            {
                value = Items[i].Value;
                return true;
            }
        }

        value = 0m;
        return false;
    }

    public bool TryPush(string name, decimal value, out BoundVariableScope next)
    {
        next = this;
        if (Count >= DslExecutionBudget.DefaultMaxDepth)
        {
            return false;
        }

        next.Items[Count] = new BoundVariable(name, value);
        next.Count = Count + 1;
        return true;
    }
}

/// <summary>
/// Pure checked-<see cref="decimal"/> evaluator for parsed <see cref="CalcNode"/> ASTs.
/// </summary>
internal static class CalculatorEvaluator
{
    private static readonly IReadOnlyDictionary<string, CalculatorFunctionInfo> FunctionLookup = BuildFunctionLookup();

    private static IReadOnlyDictionary<string, CalculatorFunctionInfo> BuildFunctionLookup()
    {
        var map = new Dictionary<string, CalculatorFunctionInfo>(ExpressionCalculator.Functions.Count, StringComparer.Ordinal);
        foreach (CalculatorFunctionInfo fn in ExpressionCalculator.Functions)
        {
            map.Add(fn.Name, fn);
        }

        return map;
    }

    public static CalculatorOutcome Evaluate(
        CalcNode node,
        IReadOnlyDictionary<string, decimal> names,
        CalculatorLimits limits)
    {
        var budget = DslExecutionBudget.Create(limits.MaxExecutionOps, limits.MaxNestingDepth);
        BoundVariableScope emptyScope = default;
        return Evaluate(node, names, limits, ref budget, in emptyScope);
    }

    public static CalculatorOutcome Evaluate(
        CalcNode node,
        IReadOnlyDictionary<string, decimal> names,
        CalculatorLimits limits,
        ref DslExecutionBudget budget,
        in BoundVariableScope boundScope)
    {
        if (!budget.TryConsume(1))
        {
            return Fail(CalculatorErrorCode.BudgetExceeded, GetNodePosition(node), "Execution budget exceeded.");
        }

        switch (node)
        {
            case NumberNode number:
                return Ok(number.Value);

            case IdentifierNode identifier:
                return EvaluateIdentifier(identifier, names, in boundScope);

            case UnaryNode unary:
                if (!budget.TryEnterDepth())
                {
                    return Fail(CalculatorErrorCode.BudgetExceeded, unary.Position, "Execution depth budget exceeded.");
                }

                try
                {
                    CalculatorOutcome operandOutcome = Evaluate(unary.Operand, names, limits, ref budget, in boundScope);
                    if (operandOutcome.Error is not null)
                    {
                        return operandOutcome;
                    }

                    decimal operandVal = operandOutcome.Value!.Value;
                    return Ok(unary.Operator == UnaryOperator.Minus ? -operandVal : operandVal);
                }
                finally
                {
                    budget.ExitDepth();
                }

            case BinaryChainNode chain:
                if (!budget.TryEnterDepth())
                {
                    return Fail(CalculatorErrorCode.BudgetExceeded, GetNodePosition(chain), "Execution depth budget exceeded.");
                }

                try
                {
                    return EvaluateBinaryChain(chain, names, limits, ref budget, in boundScope);
                }
                finally
                {
                    budget.ExitDepth();
                }

            case PowerNode power:
                if (!budget.TryEnterDepth())
                {
                    return Fail(CalculatorErrorCode.BudgetExceeded, power.CaretPosition, "Execution depth budget exceeded.");
                }

                try
                {
                    return EvaluatePower(power, names, limits, ref budget, in boundScope);
                }
                finally
                {
                    budget.ExitDepth();
                }

            case CallNode call:
                if (!budget.TryEnterDepth())
                {
                    return Fail(CalculatorErrorCode.BudgetExceeded, call.NamePosition, "Execution depth budget exceeded.");
                }

                try
                {
                    return EvaluateCall(call, names, limits, ref budget, in boundScope);
                }
                finally
                {
                    budget.ExitDepth();
                }

            default:
                throw new InvalidOperationException($"Unsupported AST node '{node.GetType().Name}'.");
        }
    }

    private static CalculatorOutcome EvaluateIdentifier(
        IdentifierNode identifier,
        IReadOnlyDictionary<string, decimal> names,
        in BoundVariableScope boundScope)
    {
        // (1) Non-finite pseudo-literals are always forbidden with exact UTF-16 position reporting.
        if (identifier.Name is "nan" or "inf")
        {
            return Fail(
                CalculatorErrorCode.DomainError,
                identifier.Position,
                $"Non-finite identifier '{identifier.Name}' is not allowed.",
                identifier.Name);
        }

        // (2) Higher-order dummy binding or caller-provided variables (backwards compatible).
        if (boundScope.Count > 0 && boundScope.TryGetValue(identifier.Name, out decimal boundValue))
        {
            return Ok(boundValue);
        }

        if (names.TryGetValue(identifier.Name, out decimal namedValue))
        {
            return Ok(namedValue);
        }

        // (3) Built-in mathematical constants.
        if (TryResolveBuiltInConstant(identifier.Name, out decimal constantValue))
        {
            return Ok(constantValue);
        }

        // (4) Undefined name.
        return Fail(CalculatorErrorCode.UndefinedName, identifier.Position, $"Undefined name '{identifier.Name}'.", identifier.Name);
    }

    private static bool TryResolveBuiltInConstant(string name, out decimal value)
    {
        switch (name)
        {
            case "e":
                value = MathConstants.EDecimal;
                return true;
            case "pi":
                value = MathConstants.PiDecimal;
                return true;
            case "phi":
                value = MathConstants.PhiDecimal;
                return true;
            case "euler_gamma":
                value = MathConstants.EulerGammaDecimal;
                return true;
            default:
                value = 0m;
                return false;
        }
    }

    internal static int GetNodePosition(CalcNode node) => node switch
    {
        NumberNode n => n.Position,
        IdentifierNode id => id.Position,
        UnaryNode u => u.Position,
        PowerNode p => p.CaretPosition,
        CallNode c => c.NamePosition,
        BinaryChainNode b => b.Rest.Count > 0 ? b.Rest[0].Position : GetNodePosition(b.First),
        _ => 0,
    };

    private static CalculatorOutcome EvaluateBinaryChain(
        BinaryChainNode chain,
        IReadOnlyDictionary<string, decimal> names,
        CalculatorLimits limits,
        ref DslExecutionBudget budget,
        in BoundVariableScope boundScope)
    {
        CalculatorOutcome accOutcome = Evaluate(chain.First, names, limits, ref budget, in boundScope);
        if (accOutcome.Error is not null)
        {
            return accOutcome;
        }

        decimal acc = accOutcome.Value!.Value;
        IReadOnlyList<BinaryChainTerm> rest = chain.Rest;
        for (int i = 0; i < rest.Count; i++)
        {
            BinaryChainTerm term = rest[i];
            if (!budget.TryConsume(1))
            {
                return Fail(CalculatorErrorCode.BudgetExceeded, term.Position, "Execution budget exceeded.");
            }

            CalculatorOutcome rightOutcome = Evaluate(term.Right, names, limits, ref budget, in boundScope);
            if (rightOutcome.Error is not null)
            {
                return rightOutcome;
            }

            decimal right = rightOutcome.Value!.Value;
            if (term.Operator is BinaryOperator.Divide or BinaryOperator.Modulo && right == 0m)
            {
                return Fail(CalculatorErrorCode.DivisionByZero, term.Position, $"Division by zero at position {term.Position}.");
            }

            try
            {
                acc = term.Operator switch
                {
                    BinaryOperator.Add => checked(acc + right),
                    BinaryOperator.Subtract => checked(acc - right),
                    BinaryOperator.Multiply => checked(acc * right),
                    BinaryOperator.Divide => checked(acc / right),
                    BinaryOperator.Modulo => checked(acc % right),
                    _ => throw new InvalidOperationException($"Unsupported binary operator '{term.Operator}'."),
                };
            }
            catch (OverflowException)
            {
                return Fail(CalculatorErrorCode.ArithmeticOverflow, term.Position, $"Arithmetic overflow at position {term.Position}.");
            }
        }

        return Ok(acc);
    }

    private static CalculatorOutcome EvaluatePower(
        PowerNode power,
        IReadOnlyDictionary<string, decimal> names,
        CalculatorLimits limits,
        ref DslExecutionBudget budget,
        in BoundVariableScope boundScope)
    {
        CalculatorOutcome baseOutcome = Evaluate(power.Base, names, limits, ref budget, in boundScope);
        if (baseOutcome.Error is not null)
        {
            return baseOutcome;
        }

        CalculatorOutcome expOutcome = Evaluate(power.Exponent, names, limits, ref budget, in boundScope);
        if (expOutcome.Error is not null)
        {
            return expOutcome;
        }

        decimal a = baseOutcome.Value!.Value;
        decimal b = expOutcome.Value!.Value;

        if (b != decimal.Truncate(b))
        {
            return Fail(CalculatorErrorCode.NonIntegerExponent, power.CaretPosition, $"Exponent must be an integer at position {power.CaretPosition}.");
        }

        if (Math.Abs(b) > limits.MaxIntegerExponent)
        {
            return Fail(
                CalculatorErrorCode.ExponentOutOfRange,
                power.CaretPosition,
                $"Exponent magnitude exceeds {limits.MaxIntegerExponent} at position {power.CaretPosition}.");
        }

        return EvaluateIntegerPower(power.CaretPosition, a, b);
    }

    internal static CalculatorOutcome EvaluateIntegerPower(int position, decimal a, decimal b)
    {
        int n = (int)Math.Abs(b);
        if (n == 0)
        {
            return Ok(1m);
        }

        if (b < 0m && a == 0m)
        {
            return Fail(CalculatorErrorCode.DivisionByZero, position, $"Division by zero in negative power at position {position}.");
        }

        try
        {
            decimal powered = PowPositive(a, n);
            if (b > 0m)
            {
                return Ok(powered);
            }

            if (powered == 0m)
            {
                return Fail(CalculatorErrorCode.ArithmeticOverflow, position, $"Arithmetic overflow in negative power reciprocal at position {position}.");
            }

            return Ok(checked(1m / powered));
        }
        catch (OverflowException)
        {
            return Fail(CalculatorErrorCode.ArithmeticOverflow, position, $"Arithmetic overflow at position {position}.");
        }
    }

    private static decimal PowPositive(decimal baseValue, int exponent)
    {
        decimal result = 1m;
        decimal factor = baseValue;
        int exp = exponent;

        while (exp > 0)
        {
            if ((exp & 1) != 0)
            {
                result = checked(result * factor);
            }

            exp >>= 1;
            if (exp > 0)
            {
                factor = checked(factor * factor);
            }
        }

        return result;
    }

    private static CalculatorOutcome EvaluateCall(
        CallNode call,
        IReadOnlyDictionary<string, decimal> names,
        CalculatorLimits limits,
        ref DslExecutionBudget budget,
        in BoundVariableScope boundScope)
    {
        if (!FunctionLookup.TryGetValue(call.Name, out CalculatorFunctionInfo? info))
        {
            return Fail(CalculatorErrorCode.UnknownFunction, call.NamePosition, $"Unknown function '{call.Name}'.", call.Name);
        }

        int maxArgs = info.IsStatisticalVariadic
            ? limits.MaxStatisticalFunctionArguments
            : info.IsVariadic
                ? limits.MaxFunctionArguments
                : limits.MaxFunctionArguments < CalculatorLimits.Default.MaxFunctionArguments
                    ? Math.Min(info.MaxArguments, limits.MaxFunctionArguments)
                    : info.MaxArguments;

        int argCount = call.Arguments.Count;
        if (argCount < info.MinArguments || argCount > maxArgs)
        {
            return Fail(
                CalculatorErrorCode.WrongArgumentCount,
                call.NamePosition,
                $"Function '{call.Name}' expects {info.MinArguments}..{maxArgs} arguments, got {argCount}.",
                call.Name);
        }

        if (CalculatorHigherOrderFunctions.IsHigherOrderFunction(call.Name))
        {
            return CalculatorHigherOrderFunctions.Evaluate(call, names, limits, ref budget, in boundScope);
        }

        Span<decimal> args = argCount <= CalculatorLimits.MaxAllowedFunctionArguments
            ? stackalloc decimal[argCount]
            : throw new InvalidOperationException($"Argument count {argCount} exceeds stack allocation bound.");

        for (int i = 0; i < argCount; i++)
        {
            CalculatorOutcome argOutcome = Evaluate(call.Arguments[i], names, limits, ref budget, in boundScope);
            if (argOutcome.Error is not null)
            {
                return argOutcome;
            }

            args[i] = argOutcome.Value!.Value;
        }

        try
        {
            switch (call.Name)
            {
                case "abs":
                    return Ok(Math.Abs(args[0]));
                case "sign":
                    return Ok(args[0] < 0m ? -1m : (args[0] > 0m ? 1m : 0m));
                case "min":
                    return Ok(MinOf(args));
                case "max":
                    return Ok(MaxOf(args));
                case "clamp":
                    return EvaluateClamp(call.NamePosition, args[0], args[1], args[2]);
                case "floor":
                    return Ok(decimal.Floor(args[0]));
                case "ceil":
                    return Ok(decimal.Ceiling(args[0]));
                case "trunc":
                    return Ok(decimal.Truncate(args[0]));
                case "round":
                    return EvaluateRound(call.NamePosition, args[0], args[1]);
                case "change_ratio":
                    return EvaluateChangeRatio(call.NamePosition, args[0], args[1]);
                case "mod":
                    return EvaluateMod(call.NamePosition, args[0], args[1]);
                case "sum":
                    return Ok(SumArgs(args));
                case "avg":
                    return Ok(checked(SumArgs(args) / args.Length));
                case "frac":
                    return Ok(checked(args[0] - decimal.Truncate(args[0])));
                case "sqrt":
                    return CalculatorTranscendentals.EvaluateSqrt(call.NamePosition, args[0]);
                case "ln":
                    return CalculatorTranscendentals.EvaluateLn(call.NamePosition, args[0]);
                case "exp":
                    return CalculatorTranscendentals.EvaluateExp(call.NamePosition, args[0]);
                case "pow":
                    return CalculatorTranscendentals.EvaluatePow(call.NamePosition, args[0], args[1], limits);
                default:
                    if (CalculatorElementaryFunctions.TryEvaluate(call.Name, call.NamePosition, args, out CalculatorOutcome elemOutcome))
                    {
                        return elemOutcome;
                    }

                    if (CalculatorSpecialFunctions.TryEvaluate(call.Name, call.NamePosition, args, ref budget, out CalculatorOutcome specOutcome))
                    {
                        return specOutcome;
                    }

                    if (CalculatorDistributionFunctions.TryEvaluate(call.Name, call.NamePosition, args, ref budget, out CalculatorOutcome distOutcome))
                    {
                        return distOutcome;
                    }

                    if (CalculatorMatrixFunctions.TryEvaluate(call.Name, call.NamePosition, args, out CalculatorOutcome matrixOutcome))
                    {
                        return matrixOutcome;
                    }

                    throw new InvalidOperationException($"Unimplemented calculator function '{call.Name}'.");
            }
        }
        catch (OverflowException)
        {
            return Fail(CalculatorErrorCode.ArithmeticOverflow, call.NamePosition, $"Arithmetic overflow in '{call.Name}' at position {call.NamePosition}.");
        }
    }

    private static decimal MinOf(ReadOnlySpan<decimal> values)
    {
        decimal best = values[0];
        for (int i = 1; i < values.Length; i++)
        {
            if (values[i] < best)
            {
                best = values[i];
            }
        }

        return best;
    }

    private static decimal MaxOf(ReadOnlySpan<decimal> values)
    {
        decimal best = values[0];
        for (int i = 1; i < values.Length; i++)
        {
            if (values[i] > best)
            {
                best = values[i];
            }
        }

        return best;
    }

    private static CalculatorOutcome EvaluateClamp(int position, decimal x, decimal lo, decimal hi)
    {
        if (lo > hi)
        {
            return Fail(CalculatorErrorCode.DomainError, position, "clamp requires lo <= hi.", "clamp");
        }

        return Ok(Math.Min(Math.Max(x, lo), hi));
    }

    private static CalculatorOutcome EvaluateRound(int position, decimal value, decimal digits)
    {
        if (digits != decimal.Truncate(digits) || digits < 0m || digits > ExpressionCalculator.MaxRoundDigits)
        {
            return Fail(
                CalculatorErrorCode.DomainError,
                position,
                $"round digits must be an integer between 0 and {ExpressionCalculator.MaxRoundDigits}.",
                "round");
        }

        decimal rounded = Math.Round(value, (int)digits, MidpointRounding.AwayFromZero);
        return Ok(rounded);
    }

    private static CalculatorOutcome EvaluateChangeRatio(int position, decimal from, decimal to)
    {
        if (from == 0m)
        {
            return Fail(CalculatorErrorCode.DivisionByZero, position, "change_ratio 'from' argument must not be zero.");
        }

        return Ok(checked((to - from) / from));
    }

    private static CalculatorOutcome EvaluateMod(int position, decimal a, decimal b)
    {
        if (b == 0m)
        {
            return Fail(CalculatorErrorCode.DivisionByZero, position, "mod divisor must not be zero.");
        }

        decimal remainder = checked(a % b);
        if (remainder != 0m && (remainder < 0m) != (b < 0m))
        {
            remainder = checked(remainder + b);
            if (remainder == b)
            {
                remainder = 0m;
            }
        }

        return Ok(remainder);
    }

    private static decimal SumArgs(ReadOnlySpan<decimal> values)
    {
        decimal acc = values[0];
        for (int i = 1; i < values.Length; i++)
        {
            acc = checked(acc + values[i]);
        }

        return acc;
    }

    // Uniformly normalizes signed zero (-0m) to positive 0m across all AST node outcomes.
    internal static CalculatorOutcome Ok(decimal value) => new(value == 0m ? 0m : value, null);

    internal static CalculatorOutcome Fail(CalculatorErrorCode code, int position, string message, string? token = null)
        => new(null, new CalculatorError(code, position, message, token));
}
