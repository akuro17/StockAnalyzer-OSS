using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using StockAnalyzer.Core.MathUtils;

namespace StockAnalyzer.Core.Calculator;

internal readonly record struct BoundVariable(string Name, decimal Value);

[InlineArray(BoundVariableScope.MaxBoundVariables)]
internal struct BoundVariableBuffer
{
    public BoundVariable Element0;
}

internal struct BoundVariableScope
{
    /// <summary>
    /// Capacity of the stack-bound dummy variable slots. Larger than <see cref="DslExecutionBudget.DefaultMaxDepth"/> because one
    /// <c>ode_rk4</c>/<c>ode_rk45</c> right-hand side binds up to <c>1 + 8</c> variables per nesting level.
    /// </summary>
    public const int MaxBoundVariables = 32;

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
        if (Count >= MaxBoundVariables)
        {
            return false;
        }

        next.Items[Count] = new BoundVariable(name, value);
        next.Count = Count + 1;
        return true;
    }
}

/// <summary>
/// Read-only scalar and array variable bindings visible during expression evaluation.
/// </summary>
internal readonly record struct DslEvalScope(
    IReadOnlyDictionary<string, decimal> ScalarNames,
    IReadOnlyDictionary<string, DslArrayHandle>? ArrayNames,
    DslArrayArenaSnapshot ArenaSnapshot = default);

/// <summary>
/// Pure checked-<see cref="decimal"/> evaluator for parsed <see cref="CalcNode"/> ASTs,
/// supporting zero-allocation 1D array lowering over <see cref="DslArrayArena"/>.
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
        var evalScope = new DslEvalScope(names, null);
        return EvaluateWithScope(node, in evalScope, limits, ref budget, in boundScope);
    }

    internal static CalculatorOutcome EvaluateWithScope(
        CalcNode node,
        in DslEvalScope evalScope,
        CalculatorLimits limits,
        ref DslExecutionBudget budget,
        in BoundVariableScope boundScope)
    {
        var arena = DslArrayArena.CreateScopedView(evalScope.ArenaSnapshot);
        try
        {
            DslEvalOutcome outcome = EvaluateValue(node, in evalScope, limits, ref budget, in boundScope, ref arena);
            if (outcome.Error is not null)
            {
                return new CalculatorOutcome(null, outcome.Error);
            }

            if (outcome.Value.IsArray)
            {
                return Fail(
                    CalculatorErrorCode.DomainError,
                    GetNodePosition(node),
                    "Expression must evaluate to a scalar decimal value, not an array.");
            }

            return Ok(outcome.Value.Scalar);
        }
        finally
        {
            arena.Dispose();
        }
    }

    public static DslEvalOutcome EvaluateValue(
        CalcNode node,
        in DslEvalScope scope,
        CalculatorLimits limits,
        ref DslExecutionBudget budget,
        in BoundVariableScope boundScope,
        ref DslArrayArena arena)
    {
        if (!budget.TryConsume(1))
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.BudgetExceeded, GetNodePosition(node), "Execution budget exceeded.");
        }

        switch (node)
        {
            case NumberNode number:
                return DslEvalOutcome.Ok(number.Value);

            case IdentifierNode identifier:
                return EvaluateIdentifier(identifier, in scope, in boundScope);

            case ArrayLiteralNode arrayLiteral:
                if (!budget.TryEnterDepth())
                {
                    return DslEvalOutcome.Fail(CalculatorErrorCode.BudgetExceeded, arrayLiteral.OpenBracketPosition, "Execution depth budget exceeded.");
                }

                try
                {
                    return EvaluateArrayLiteral(arrayLiteral, in scope, limits, ref budget, in boundScope, ref arena);
                }
                finally
                {
                    budget.ExitDepth();
                }

            case UnaryNode unary:
                if (!budget.TryEnterDepth())
                {
                    return DslEvalOutcome.Fail(CalculatorErrorCode.BudgetExceeded, unary.Position, "Execution depth budget exceeded.");
                }

                try
                {
                    DslEvalOutcome operandOutcome = EvaluateValue(unary.Operand, in scope, limits, ref budget, in boundScope, ref arena);
                    if (operandOutcome.Error is not null)
                    {
                        return operandOutcome;
                    }

                    if (operandOutcome.Value.IsArray)
                    {
                        return DslEvalOutcome.Fail(
                            CalculatorErrorCode.DomainError,
                            unary.Position,
                            "Unary operator cannot be applied to an array.");
                    }

                    decimal operandVal = operandOutcome.Value.Scalar;
                    decimal unaryResult = unary.Operator switch
                    {
                        UnaryOperator.Minus => -operandVal,
                        UnaryOperator.LogicalNot => operandVal == 0m ? 1m : 0m,
                        _ => operandVal,
                    };
                    return DslEvalOutcome.Ok(unaryResult);
                }
                finally
                {
                    budget.ExitDepth();
                }

            case LogicalBinaryNode logical:
                if (!budget.TryEnterDepth())
                {
                    return DslEvalOutcome.Fail(CalculatorErrorCode.BudgetExceeded, logical.Position, "Execution depth budget exceeded.");
                }

                try
                {
                    return EvaluateLogicalBinary(logical, in scope, limits, ref budget, in boundScope, ref arena);
                }
                finally
                {
                    budget.ExitDepth();
                }

            case TernaryNode ternary:
                if (!budget.TryEnterDepth())
                {
                    return DslEvalOutcome.Fail(CalculatorErrorCode.BudgetExceeded, ternary.QuestionPosition, "Execution depth budget exceeded.");
                }

                try
                {
                    return EvaluateTernary(ternary, in scope, limits, ref budget, in boundScope, ref arena);
                }
                finally
                {
                    budget.ExitDepth();
                }

            case SubscriptNode subscript:
                if (!budget.TryEnterDepth())
                {
                    return DslEvalOutcome.Fail(CalculatorErrorCode.BudgetExceeded, subscript.OpenBracketPosition, "Execution depth budget exceeded.");
                }

                try
                {
                    return EvaluateSubscript(subscript, in scope, limits, ref budget, in boundScope, ref arena);
                }
                finally
                {
                    budget.ExitDepth();
                }

            case BinaryChainNode chain:
                if (!budget.TryEnterDepth())
                {
                    return DslEvalOutcome.Fail(CalculatorErrorCode.BudgetExceeded, GetNodePosition(chain), "Execution depth budget exceeded.");
                }

                try
                {
                    return EvaluateBinaryChain(chain, in scope, limits, ref budget, in boundScope, ref arena);
                }
                finally
                {
                    budget.ExitDepth();
                }

            case PowerNode power:
                if (!budget.TryEnterDepth())
                {
                    return DslEvalOutcome.Fail(CalculatorErrorCode.BudgetExceeded, power.CaretPosition, "Execution depth budget exceeded.");
                }

                try
                {
                    return EvaluatePower(power, in scope, limits, ref budget, in boundScope, ref arena);
                }
                finally
                {
                    budget.ExitDepth();
                }

            case CallNode call:
                if (!budget.TryEnterDepth())
                {
                    return DslEvalOutcome.Fail(CalculatorErrorCode.BudgetExceeded, call.NamePosition, "Execution depth budget exceeded.");
                }

                try
                {
                    return EvaluateCall(call, in scope, limits, ref budget, in boundScope, ref arena);
                }
                finally
                {
                    budget.ExitDepth();
                }

            default:
                throw new InvalidOperationException($"Unsupported AST node '{node.GetType().Name}'.");
        }
    }

    private static DslEvalOutcome EvaluateArrayLiteral(
        ArrayLiteralNode arrayLiteral,
        in DslEvalScope scope,
        CalculatorLimits limits,
        ref DslExecutionBudget budget,
        in BoundVariableScope boundScope,
        ref DslArrayArena arena)
    {
        IReadOnlyList<CalcNode> elements = arrayLiteral.Elements;
        int count = elements.Count;
        if (!arena.TryAllocate(count, out DslArrayHandle handle, out _))
        {
            return DslEvalOutcome.Fail(
                CalculatorErrorCode.BudgetExceeded,
                arrayLiteral.OpenBracketPosition,
                "Array arena budget exceeded.");
        }

        for (int i = 0; i < count; i++)
        {
            DslEvalOutcome elemOutcome = EvaluateValue(elements[i], in scope, limits, ref budget, in boundScope, ref arena);
            if (elemOutcome.Error is not null)
            {
                return elemOutcome;
            }

            if (elemOutcome.Value.IsArray)
            {
                return DslEvalOutcome.Fail(
                    CalculatorErrorCode.DomainError,
                    GetNodePosition(elements[i]),
                    "Nested 2D array literals are not supported; pass a 1D row-major array.");
            }

            arena.GetMutableSlice(handle)[i] = elemOutcome.Value.Scalar;
        }

        return DslEvalOutcome.OkArray(handle);
    }

    private static DslEvalOutcome EvaluateIdentifier(
        IdentifierNode identifier,
        in DslEvalScope scope,
        in BoundVariableScope boundScope)
    {
        // (1) Non-finite pseudo-literals are always forbidden with exact UTF-16 position reporting.
        if (identifier.Name is "nan" or "inf")
        {
            return DslEvalOutcome.Fail(
                CalculatorErrorCode.DomainError,
                identifier.Position,
                $"Non-finite identifier '{identifier.Name}' is not allowed.",
                identifier.Name);
        }

        // (2) Higher-order dummy binding or caller-provided variables (backwards compatible).
        if (boundScope.Count > 0 && boundScope.TryGetValue(identifier.Name, out decimal boundValue))
        {
            return DslEvalOutcome.Ok(boundValue);
        }

        if (scope.ScalarNames.TryGetValue(identifier.Name, out decimal namedValue))
        {
            return DslEvalOutcome.Ok(namedValue);
        }

        if (scope.ArrayNames is not null && scope.ArrayNames.TryGetValue(identifier.Name, out DslArrayHandle arrayHandle))
        {
            return DslEvalOutcome.OkArray(arrayHandle);
        }

        // (3) Built-in mathematical constants.
        if (TryResolveBuiltInConstant(identifier.Name, out decimal constantValue))
        {
            return DslEvalOutcome.Ok(constantValue);
        }

        // (4) Undefined name.
        return DslEvalOutcome.Fail(CalculatorErrorCode.UndefinedName, identifier.Position, $"Undefined name '{identifier.Name}'.", identifier.Name);
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
        ArrayLiteralNode a => a.OpenBracketPosition,
        UnaryNode u => u.Position,
        PowerNode p => p.CaretPosition,
        CallNode c => c.NamePosition,
        BinaryChainNode b => b.Rest.Count > 0 ? b.Rest[0].Position : GetNodePosition(b.First),
        LogicalBinaryNode l => l.Position,
        TernaryNode t => t.QuestionPosition,
        SubscriptNode s => s.OpenBracketPosition,
        _ => 0,
    };

    private static DslEvalOutcome EvaluateBinaryChain(
        BinaryChainNode chain,
        in DslEvalScope scope,
        CalculatorLimits limits,
        ref DslExecutionBudget budget,
        in BoundVariableScope boundScope,
        ref DslArrayArena arena)
    {
        DslEvalOutcome accOutcome = EvaluateValue(chain.First, in scope, limits, ref budget, in boundScope, ref arena);
        if (accOutcome.Error is not null)
        {
            return accOutcome;
        }

        if (accOutcome.Value.IsArray)
        {
            int opPos = chain.Rest.Count > 0 ? chain.Rest[0].Position : GetNodePosition(chain.First);
            return DslEvalOutcome.Fail(
                CalculatorErrorCode.DomainError,
                opPos,
                "Binary arithmetic operators cannot be applied directly to an array; use arr_zip_add or arr_zip_mul.");
        }

        decimal acc = accOutcome.Value.Scalar;
        IReadOnlyList<BinaryChainTerm> rest = chain.Rest;
        for (int i = 0; i < rest.Count; i++)
        {
            BinaryChainTerm term = rest[i];
            if (!budget.TryConsume(1))
            {
                return DslEvalOutcome.Fail(CalculatorErrorCode.BudgetExceeded, term.Position, "Execution budget exceeded.");
            }

            DslEvalOutcome rightOutcome = EvaluateValue(term.Right, in scope, limits, ref budget, in boundScope, ref arena);
            if (rightOutcome.Error is not null)
            {
                return rightOutcome;
            }

            if (rightOutcome.Value.IsArray)
            {
                return DslEvalOutcome.Fail(
                    CalculatorErrorCode.DomainError,
                    term.Position,
                    "Binary arithmetic operators cannot be applied directly to an array; use arr_zip_add or arr_zip_mul.");
            }

            decimal right = rightOutcome.Value.Scalar;
            if (term.Operator is BinaryOperator.Divide or BinaryOperator.Modulo && right == 0m)
            {
                return DslEvalOutcome.Fail(CalculatorErrorCode.DivisionByZero, term.Position, $"Division by zero at position {term.Position}.");
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
                    BinaryOperator.Less => acc < right ? 1m : 0m,
                    BinaryOperator.LessEqual => acc <= right ? 1m : 0m,
                    BinaryOperator.Greater => acc > right ? 1m : 0m,
                    BinaryOperator.GreaterEqual => acc >= right ? 1m : 0m,
                    BinaryOperator.Equal => acc == right ? 1m : 0m,
                    BinaryOperator.NotEqual => acc != right ? 1m : 0m,
                    _ => throw new InvalidOperationException($"Unsupported binary operator '{term.Operator}'."),
                };
            }
            catch (OverflowException)
            {
                return DslEvalOutcome.Fail(CalculatorErrorCode.ArithmeticOverflow, term.Position, $"Arithmetic overflow at position {term.Position}.");
            }
        }

        return DslEvalOutcome.Ok(acc);
    }

    private static DslEvalOutcome EvaluatePower(
        PowerNode power,
        in DslEvalScope scope,
        CalculatorLimits limits,
        ref DslExecutionBudget budget,
        in BoundVariableScope boundScope,
        ref DslArrayArena arena)
    {
        DslEvalOutcome baseOutcome = EvaluateValue(power.Base, in scope, limits, ref budget, in boundScope, ref arena);
        if (baseOutcome.Error is not null)
        {
            return baseOutcome;
        }

        if (baseOutcome.Value.IsArray)
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.DomainError, power.CaretPosition, "Power operator cannot be applied to an array.");
        }

        DslEvalOutcome expOutcome = EvaluateValue(power.Exponent, in scope, limits, ref budget, in boundScope, ref arena);
        if (expOutcome.Error is not null)
        {
            return expOutcome;
        }

        if (expOutcome.Value.IsArray)
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.DomainError, power.CaretPosition, "Power exponent cannot be an array.");
        }

        decimal a = baseOutcome.Value.Scalar;
        decimal b = expOutcome.Value.Scalar;

        if (b != decimal.Truncate(b))
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.NonIntegerExponent, power.CaretPosition, $"Exponent must be an integer at position {power.CaretPosition}.");
        }

        if (Math.Abs(b) > limits.MaxIntegerExponent)
        {
            return DslEvalOutcome.Fail(
                CalculatorErrorCode.ExponentOutOfRange,
                power.CaretPosition,
                $"Exponent magnitude exceeds {limits.MaxIntegerExponent} at position {power.CaretPosition}.");
        }

        return CalculatorArrayFunctions.ToEvalOutcome(EvaluateIntegerPower(power.CaretPosition, a, b));
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

    private static DslEvalOutcome EvaluateCall(
        CallNode call,
        in DslEvalScope scope,
        CalculatorLimits limits,
        ref DslExecutionBudget budget,
        in BoundVariableScope boundScope,
        ref DslArrayArena arena)
    {
        if (!FunctionLookup.TryGetValue(call.Name, out CalculatorFunctionInfo? info))
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.UnknownFunction, call.NamePosition, $"Unknown function '{call.Name}'.", call.Name);
        }

        int maxArgs = info.IsStatisticalVariadic
            ? limits.MaxStatisticalFunctionArguments
            : info.IsVariadic
                ? limits.MaxFunctionArguments
                : limits.MaxFunctionArguments < CalculatorLimits.Default.MaxFunctionArguments
                    ? Math.Min(info.MaxArguments, limits.MaxFunctionArguments)
                    : info.MaxArguments;

        int argCount = call.Arguments.Count;
        bool mayBeArrayLowered =
            (argCount == 1 && CalculatorArrayFunctions.AcceptsSingleArrayArgument(call.Name) && CanNodeProduceArray(call.Arguments[0], in scope)) ||
            (argCount == 2 && CalculatorArrayFunctions.AcceptsScalarPlusArrayArgument(call.Name) && CanNodeProduceArray(call.Arguments[1], in scope));

        if (!mayBeArrayLowered && (argCount < info.MinArguments || argCount > maxArgs))
        {
            // The count check runs before the arguments are evaluated. When an argument names something that does not exist, that is the
            // real mistake (and the only reason the argument could not be proven to be an array), so it is reported first.
            if (TryFindUnresolvedIdentifierInCall(call, in scope, in boundScope, new List<string>(), out IdentifierNode? unresolved))
            {
                return DslEvalOutcome.Fail(CalculatorErrorCode.UndefinedName, unresolved.Position, $"Undefined name '{unresolved.Name}'.", unresolved.Name);
            }

            return DslEvalOutcome.Fail(
                CalculatorErrorCode.WrongArgumentCount,
                call.NamePosition,
                $"Function '{call.Name}' expects {info.MinArguments}..{maxArgs} arguments, got {argCount}.",
                call.Name);
        }

        if (CalculatorHigherOrderFunctions.IsHigherOrderFunction(call.Name))
        {
            return CalculatorHigherOrderFunctions.Evaluate(call, in scope, limits, ref budget, in boundScope, ref arena);
        }

        if (call.Name is "if_gt" or "if_ge" or "if_eq")
        {
            return EvaluateLazyConditional(call, in scope, limits, ref budget, in boundScope, ref arena);
        }

        Span<DslValue> rawArgs = argCount <= CalculatorLimits.MaxAllowedFunctionArguments
            ? stackalloc DslValue[argCount]
            : throw new InvalidOperationException($"Argument count {argCount} exceeds stack allocation bound.");

        bool hasArrayArg = false;
        for (int i = 0; i < argCount; i++)
        {
            DslEvalOutcome argOutcome = EvaluateValue(call.Arguments[i], in scope, limits, ref budget, in boundScope, ref arena);
            if (argOutcome.Error is not null)
            {
                return argOutcome;
            }

            rawArgs[i] = argOutcome.Value;
            hasArrayArg |= argOutcome.Value.IsArray;
        }

        if (mayBeArrayLowered)
        {
            if (hasArrayArg)
            {
                return CalculatorArrayFunctions.EvaluateArrayLoweredVariadic(
                    call.Name,
                    call.NamePosition,
                    info,
                    rawArgs,
                    limits,
                    ref budget,
                    in arena);
            }

            if (argCount < info.MinArguments || argCount > maxArgs)
            {
                return DslEvalOutcome.Fail(
                    CalculatorErrorCode.WrongArgumentCount,
                    call.NamePosition,
                    $"Function '{call.Name}' expects {info.MinArguments}..{maxArgs} arguments, got {argCount}.",
                    call.Name);
            }
        }

        if (CalculatorArrayFunctions.TryEvaluate(call.Name, call.NamePosition, rawArgs, ref arena, out DslEvalOutcome arrayOpOutcome))
        {
            return arrayOpOutcome;
        }

        if (CalculatorMatrixFunctions.TryEvaluateValue(call.Name, call.NamePosition, rawArgs, ref budget, ref arena, out DslEvalOutcome matrixValueOutcome))
        {
            return matrixValueOutcome;
        }

        if (CalculatorDiagnosticsAndStatsFunctions.TryEvaluateValue(call.Name, call.NamePosition, rawArgs, ref budget, ref arena, out DslEvalOutcome diagOutcome))
        {
            return diagOutcome;
        }

        if (hasArrayArg)
        {
            return DslEvalOutcome.Fail(
                CalculatorErrorCode.DomainError,
                call.NamePosition,
                $"Function '{call.Name}' does not accept array arguments.",
                call.Name);
        }

        Span<decimal> args = stackalloc decimal[argCount];
        for (int i = 0; i < argCount; i++)
        {
            args[i] = rawArgs[i].Scalar;
        }

        return CalculatorArrayFunctions.ToEvalOutcome(EvaluateScalarCall(call.Name, call.NamePosition, args, limits, ref budget));
    }

    internal static CalculatorOutcome EvaluateScalarSliceCall(
        string name,
        int position,
        scoped ReadOnlySpan<decimal> values,
        CalculatorLimits limits,
        ref DslExecutionBudget budget)
        => EvaluateScalarCall(name, position, values, limits, ref budget);

    private static bool CanNodeProduceArray(CalcNode node, in DslEvalScope scope) => node switch
    {
        ArrayLiteralNode => true,
        IdentifierNode id => scope.ArrayNames is not null && scope.ArrayNames.ContainsKey(id.Name),
        CallNode fn => CalculatorArrayFunctions.ReturnsArray(fn.Name) ||
            (fn.Name is "if_gt" or "if_ge" or "if_eq" &&
             fn.Arguments.Count >= 4 &&
             (CanNodeProduceArray(fn.Arguments[2], in scope) || CanNodeProduceArray(fn.Arguments[3], in scope))),
        TernaryNode ternary => CanNodeProduceArray(ternary.WhenTrue, in scope) || CanNodeProduceArray(ternary.WhenFalse, in scope),
        _ => false,
    };

    private static DslEvalOutcome EvaluateLogicalBinary(
        LogicalBinaryNode logical,
        in DslEvalScope scope,
        CalculatorLimits limits,
        ref DslExecutionBudget budget,
        in BoundVariableScope boundScope,
        ref DslArrayArena arena)
    {
        DslEvalOutcome leftOutcome = EvaluateValue(logical.Left, in scope, limits, ref budget, in boundScope, ref arena);
        if (leftOutcome.Error is not null)
        {
            return leftOutcome;
        }

        if (leftOutcome.Value.IsArray)
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.DomainError, logical.Position, "Logical operators cannot be applied to an array.");
        }

        bool leftTrue = leftOutcome.Value.Scalar != 0m;
        if (logical.Operator == LogicalOperator.And && !leftTrue)
        {
            return DslEvalOutcome.Ok(0m);
        }

        if (logical.Operator == LogicalOperator.Or && leftTrue)
        {
            return DslEvalOutcome.Ok(1m);
        }

        DslEvalOutcome rightOutcome = EvaluateValue(logical.Right, in scope, limits, ref budget, in boundScope, ref arena);
        if (rightOutcome.Error is not null)
        {
            return rightOutcome;
        }

        if (rightOutcome.Value.IsArray)
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.DomainError, logical.Position, "Logical operators cannot be applied to an array.");
        }

        return DslEvalOutcome.Ok(rightOutcome.Value.Scalar != 0m ? 1m : 0m);
    }

    private static DslEvalOutcome EvaluateTernary(
        TernaryNode ternary,
        in DslEvalScope scope,
        CalculatorLimits limits,
        ref DslExecutionBudget budget,
        in BoundVariableScope boundScope,
        ref DslArrayArena arena)
    {
        DslEvalOutcome condOutcome = EvaluateValue(ternary.Condition, in scope, limits, ref budget, in boundScope, ref arena);
        if (condOutcome.Error is not null)
        {
            return condOutcome;
        }

        if (condOutcome.Value.IsArray)
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.DomainError, ternary.QuestionPosition, "Ternary condition cannot be an array.");
        }

        CalcNode selectedBranch = condOutcome.Value.Scalar != 0m ? ternary.WhenTrue : ternary.WhenFalse;
        return EvaluateValue(selectedBranch, in scope, limits, ref budget, in boundScope, ref arena);
    }

    private static DslEvalOutcome EvaluateSubscript(
        SubscriptNode subscript,
        in DslEvalScope scope,
        CalculatorLimits limits,
        ref DslExecutionBudget budget,
        in BoundVariableScope boundScope,
        ref DslArrayArena arena)
    {
        DslEvalOutcome targetOutcome = EvaluateValue(subscript.Target, in scope, limits, ref budget, in boundScope, ref arena);
        if (targetOutcome.Error is not null)
        {
            return targetOutcome;
        }

        if (!targetOutcome.Value.IsArray)
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.DomainError, subscript.OpenBracketPosition, "Subscript operator requires a 1D array target.");
        }

        DslEvalOutcome indexOutcome = EvaluateValue(subscript.Index, in scope, limits, ref budget, in boundScope, ref arena);
        if (indexOutcome.Error is not null)
        {
            return indexOutcome;
        }

        if (indexOutcome.Value.IsArray)
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.DomainError, subscript.OpenBracketPosition, "Subscript index must be a scalar integer.");
        }

        ReadOnlySpan<decimal> slice = arena.GetSlice(targetOutcome.Value.Array);
        if (slice.Length == 0 || !DslMathValidation.TryReadInt32InRange(indexOutcome.Value.Scalar, 0, slice.Length - 1, out int idx))
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.DomainError, subscript.OpenBracketPosition, "Array subscript index out of bounds.");
        }

        return DslEvalOutcome.Ok(slice[idx]);
    }

    private static DslEvalOutcome EvaluateLazyConditional(
        CallNode call,
        in DslEvalScope scope,
        CalculatorLimits limits,
        ref DslExecutionBudget budget,
        in BoundVariableScope boundScope,
        ref DslArrayArena arena)
    {
        DslEvalOutcome aOutcome = EvaluateValue(call.Arguments[0], in scope, limits, ref budget, in boundScope, ref arena);
        if (aOutcome.Error is not null)
        {
            return aOutcome;
        }

        DslEvalOutcome bOutcome = EvaluateValue(call.Arguments[1], in scope, limits, ref budget, in boundScope, ref arena);
        if (bOutcome.Error is not null)
        {
            return bOutcome;
        }

        if (aOutcome.Value.IsArray || bOutcome.Value.IsArray)
        {
            return DslEvalOutcome.Fail(CalculatorErrorCode.DomainError, call.NamePosition, $"Function '{call.Name}' requires scalar comparison operands.", call.Name);
        }

        decimal a = aOutcome.Value.Scalar;
        decimal b = bOutcome.Value.Scalar;
        bool takeTrue;
        if (call.Name == "if_gt")
        {
            takeTrue = a > b;
        }
        else if (call.Name == "if_ge")
        {
            takeTrue = a >= b;
        }
        else if (call.Arguments.Count >= 5)
        {
            DslEvalOutcome epsOutcome = EvaluateValue(call.Arguments[4], in scope, limits, ref budget, in boundScope, ref arena);
            if (epsOutcome.Error is not null)
            {
                return epsOutcome;
            }

            if (epsOutcome.Value.IsArray || epsOutcome.Value.Scalar < 0m)
            {
                return DslEvalOutcome.Fail(CalculatorErrorCode.DomainError, call.NamePosition, "if_eq requires a non-negative scalar tolerance.", call.Name);
            }

            try
            {
                takeTrue = Math.Abs(checked(a - b)) <= epsOutcome.Value.Scalar;
            }
            catch (OverflowException)
            {
                return DslEvalOutcome.Fail(CalculatorErrorCode.ArithmeticOverflow, call.NamePosition, $"Arithmetic overflow in '{call.Name}' at position {call.NamePosition}.");
            }
        }
        else
        {
            takeTrue = a == b;
        }

        return EvaluateValue(call.Arguments[takeTrue ? 2 : 3], in scope, limits, ref budget, in boundScope, ref arena);
    }

    /// <summary>
    /// True when <paramref name="name"/> would be found by <see cref="EvaluateIdentifier"/> (steps 2 and 3: bound variables, caller scalars, caller arrays, built-in constants).
    /// The non-finite pseudo-literals <c>nan</c> and <c>inf</c> exist (and are rejected with their own error when evaluated), so they are not "undefined".
    /// </summary>
    private static bool IsIdentifierResolvable(string name, in DslEvalScope scope, in BoundVariableScope boundScope)
        => name is "nan" or "inf"
            || (boundScope.Count > 0 && boundScope.TryGetValue(name, out _))
            || scope.ScalarNames.ContainsKey(name)
            || (scope.ArrayNames is not null && scope.ArrayNames.ContainsKey(name))
            || TryResolveBuiltInConstant(name, out _);

    /// <summary>
    /// Depth-first, left-to-right (pre-order) search for the first identifier of the subtrees that cannot be resolved. Cold path: used only to choose the error code of a failing argument-count check.
    /// A nested higher-order call binds its dummy variables inside its body (argument 0) only; the dummy declaration (argument 1) is skipped, so those names are not reported.
    /// </summary>
    private static bool TryFindUnresolvedIdentifier(
        IReadOnlyList<CalcNode> nodes,
        in DslEvalScope scope,
        in BoundVariableScope boundScope,
        List<string> dummyNames,
        out IdentifierNode? unresolved)
    {
        for (int i = 0; i < nodes.Count; i++)
        {
            if (TryFindUnresolvedIdentifier(nodes[i], in scope, in boundScope, dummyNames, out unresolved))
            {
                return true;
            }
        }

        unresolved = null;
        return false;
    }

    private static bool TryFindUnresolvedIdentifier(
        CalcNode node,
        in DslEvalScope scope,
        in BoundVariableScope boundScope,
        List<string> dummyNames,
        out IdentifierNode? unresolved)
    {
        switch (node)
        {
            case IdentifierNode id:
                if (!dummyNames.Contains(id.Name) && !IsIdentifierResolvable(id.Name, in scope, in boundScope))
                {
                    unresolved = id;
                    return true;
                }

                break;

            case UnaryNode unary:
                return TryFindUnresolvedIdentifier(unary.Operand, in scope, in boundScope, dummyNames, out unresolved);

            case PowerNode power:
                return TryFindUnresolvedIdentifier(power.Base, in scope, in boundScope, dummyNames, out unresolved)
                    || TryFindUnresolvedIdentifier(power.Exponent, in scope, in boundScope, dummyNames, out unresolved);

            case BinaryChainNode chain:
                if (TryFindUnresolvedIdentifier(chain.First, in scope, in boundScope, dummyNames, out unresolved))
                {
                    return true;
                }

                for (int i = 0; i < chain.Rest.Count; i++)
                {
                    if (TryFindUnresolvedIdentifier(chain.Rest[i].Right, in scope, in boundScope, dummyNames, out unresolved))
                    {
                        return true;
                    }
                }

                break;

            case ArrayLiteralNode array:
                return TryFindUnresolvedIdentifier(array.Elements, in scope, in boundScope, dummyNames, out unresolved);

            case LogicalBinaryNode logical:
                return TryFindUnresolvedIdentifier(logical.Left, in scope, in boundScope, dummyNames, out unresolved)
                    || TryFindUnresolvedIdentifier(logical.Right, in scope, in boundScope, dummyNames, out unresolved);

            case TernaryNode ternary:
                return TryFindUnresolvedIdentifier(ternary.Condition, in scope, in boundScope, dummyNames, out unresolved)
                    || TryFindUnresolvedIdentifier(ternary.WhenTrue, in scope, in boundScope, dummyNames, out unresolved)
                    || TryFindUnresolvedIdentifier(ternary.WhenFalse, in scope, in boundScope, dummyNames, out unresolved);

            case SubscriptNode subscript:
                return TryFindUnresolvedIdentifier(subscript.Target, in scope, in boundScope, dummyNames, out unresolved)
                    || TryFindUnresolvedIdentifier(subscript.Index, in scope, in boundScope, dummyNames, out unresolved);

            case CallNode call:
                return TryFindUnresolvedIdentifierInCall(call, in scope, in boundScope, dummyNames, out unresolved);
        }

        unresolved = null;
        return false;
    }

    private static bool TryFindUnresolvedIdentifierInCall(
        CallNode call,
        in DslEvalScope scope,
        in BoundVariableScope boundScope,
        List<string> dummyNames,
        out IdentifierNode? unresolved)
    {
        if (!CalculatorHigherOrderFunctions.IsHigherOrderFunction(call.Name) || call.Arguments.Count < 2)
        {
            return TryFindUnresolvedIdentifier(call.Arguments, in scope, in boundScope, dummyNames, out unresolved);
        }

        // Argument 0 is the body, argument 1 declares the dummy variable(s) (an identifier, or a literal array of identifiers for the N-dimensional forms), the rest are ordinary arguments.
        // ode_rk4 / ode_rk45 additionally declare the state variables as the literal array in argument 2.
        int declared = dummyNames.Count;
        if (call.Arguments[1] is IdentifierNode single)
        {
            dummyNames.Add(single.Name);
        }
        else if (call.Arguments[1] is ArrayLiteralNode many)
        {
            foreach (CalcNode element in many.Elements)
            {
                if (element is IdentifierNode dummy)
                {
                    dummyNames.Add(dummy.Name);
                }
            }
        }

        int firstOrdinaryArgument = 2;
        if (call.Name == "arr_reduce" && call.Arguments.Count >= 3)
        {
            firstOrdinaryArgument = 3;
            if (call.Arguments[2] is IdentifierNode itemId)
            {
                dummyNames.Add(itemId.Name);
            }
        }
        else if (CalculatorHigherOrderFunctions.DeclaresStateVariables(call.Name) && call.Arguments.Count >= 3)
        {
            firstOrdinaryArgument = 3;
            if (call.Arguments[2] is ArrayLiteralNode stateVariables)
            {
                foreach (CalcNode element in stateVariables.Elements)
                {
                    if (element is IdentifierNode state)
                    {
                        dummyNames.Add(state.Name);
                    }
                }
            }
        }

        bool found = TryFindUnresolvedIdentifier(call.Arguments[0], in scope, in boundScope, dummyNames, out unresolved);
        dummyNames.RemoveRange(declared, dummyNames.Count - declared);
        if (found)
        {
            return true;
        }

        for (int i = firstOrdinaryArgument; i < call.Arguments.Count; i++)
        {
            if (TryFindUnresolvedIdentifier(call.Arguments[i], in scope, in boundScope, dummyNames, out unresolved))
            {
                return true;
            }
        }

        unresolved = null;
        return false;
    }

    private static CalculatorOutcome EvaluateScalarCall(
        string name,
        int position,
        scoped ReadOnlySpan<decimal> args,
        CalculatorLimits limits,
        ref DslExecutionBudget budget)
    {
        try
        {
            switch (name)
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
                    return EvaluateClamp(position, args[0], args[1], args[2]);
                case "floor":
                    return Ok(decimal.Floor(args[0]));
                case "ceil":
                    return Ok(decimal.Ceiling(args[0]));
                case "trunc":
                    return Ok(decimal.Truncate(args[0]));
                case "round":
                    return EvaluateRound(position, args[0], args[1]);
                case "change_ratio":
                    return EvaluateChangeRatio(position, args[0], args[1]);
                case "mod":
                    return EvaluateMod(position, args[0], args[1]);
                case "sum":
                    return Ok(SumArgs(args));
                case "avg":
                    return Ok(checked(SumArgs(args) / args.Length));
                case "frac":
                    return Ok(checked(args[0] - decimal.Truncate(args[0])));
                case "sqrt":
                    return CalculatorTranscendentals.EvaluateSqrt(position, args[0]);
                case "ln":
                    return CalculatorTranscendentals.EvaluateLn(position, args[0]);
                case "exp":
                    return CalculatorTranscendentals.EvaluateExp(position, args[0]);
                case "pow":
                    return CalculatorTranscendentals.EvaluatePow(position, args[0], args[1], limits);
                default:
                    if (CalculatorElementaryFunctions.TryEvaluate(name, position, args, out CalculatorOutcome elemOutcome))
                    {
                        return elemOutcome;
                    }

                    if (CalculatorSpecialFunctions.TryEvaluate(name, position, args, ref budget, out CalculatorOutcome specOutcome))
                    {
                        return specOutcome;
                    }

                    if (CalculatorDistributionFunctions.TryEvaluate(name, position, args, ref budget, out CalculatorOutcome distOutcome))
                    {
                        return distOutcome;
                    }

                    if (CalculatorMatrixFunctions.TryEvaluate(name, position, args, out CalculatorOutcome matrixOutcome))
                    {
                        return matrixOutcome;
                    }

                    throw new InvalidOperationException($"Unimplemented calculator function '{name}'.");
            }
        }
        catch (OverflowException)
        {
            return Fail(CalculatorErrorCode.ArithmeticOverflow, position, $"Arithmetic overflow in '{name}' at position {position}.");
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
