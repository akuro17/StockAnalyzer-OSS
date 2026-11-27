using System;
using System.Buffers;
using System.Collections.Generic;
using StockAnalyzer.Core.MathUtils;

namespace StockAnalyzer.Core.Calculator;

internal static partial class CalculatorHigherOrderFunctions
{
    /// <summary>
    /// <c>arr_zip_rolling_map(expr, winVars, arrs, w[, fill])</c>: slides synchronized zero-copy <see cref="DslArrayHandle"/> windows
    /// of length <c>w</c> across <c>K</c> equal-length arrays (<c>1 &lt;= K &lt;= BoundArrayScope.MaxBoundArrays</c>)
    /// and evaluates scalar <c>expr</c> with each window bound in <see cref="BoundArrayScope"/>.
    /// When <c>fill</c> is provided, preserves input length <c>N</c> (padding warmup indices <c>0..w-2</c> and <c>N &lt; w</c> with <c>fill</c>).
    /// </summary>
    private static DslEvalOutcome EvaluateArrayZipRollingMap(
        CallNode call,
        in DslEvalScope scope,
        CalculatorLimits limits,
        ref DslExecutionBudget budget,
        in BoundVariableScope boundScope,
        ref DslArrayArena arena)
    {
        IReadOnlyList<CalcNode> args = call.Arguments;
        bool hasFill = args.Count >= 5;
        if (args[1] is not ArrayLiteralNode winVarsLiteral ||
            winVarsLiteral.Elements.Count < 1 ||
            winVarsLiteral.Elements.Count > BoundArrayScope.MaxBoundArrays)
        {
            return DslEvalOutcome.Fail(
                CalculatorErrorCode.DomainError,
                CalculatorEvaluator.GetNodePosition(args[1]),
                $"'{call.Name}' requires a literal array of 1..{BoundArrayScope.MaxBoundArrays} window variable identifiers.",
                call.Name);
        }

        if (!TryReadIdentifierList(call, winVarsLiteral, null, out string[] winVarNames, out DslEvalOutcome idErr))
        {
            return idErr;
        }

        int k = winVarNames.Length;
        if (args[2] is not ArrayLiteralNode arrsLiteral)
        {
            return DslEvalOutcome.Fail(
                CalculatorErrorCode.DomainError,
                CalculatorEvaluator.GetNodePosition(args[2]),
                $"'{call.Name}' requires a literal array of 1D array expressions as its third argument.",
                call.Name);
        }

        if (arrsLiteral.Elements.Count != k)
        {
            return DslEvalOutcome.Fail(
                CalculatorErrorCode.DimensionMismatch,
                CalculatorEvaluator.GetNodePosition(args[2]),
                $"'{call.Name}' has {k} window variables but {arrsLiteral.Elements.Count} array expressions.",
                call.Name);
        }

        Span<DslArrayHandle> arrHandles = stackalloc DslArrayHandle[k];
        int n = 0;
        for (int j = 0; j < k; j++)
        {
            DslEvalOutcome arrOutcome = CalculatorEvaluator.EvaluateValue(arrsLiteral.Elements[j], in scope, limits, ref budget, in boundScope, ref arena);
            if (arrOutcome.Error is not null)
            {
                return arrOutcome;
            }

            if (!arrOutcome.Value.IsArray)
            {
                return DslEvalOutcome.Fail(
                    CalculatorErrorCode.DomainError,
                    CalculatorEvaluator.GetNodePosition(arrsLiteral.Elements[j]),
                    $"'{call.Name}' requires every element of its third argument to be a 1D array.",
                    call.Name);
            }

            arrHandles[j] = arrOutcome.Value.Array;
            if (j == 0)
            {
                n = arrHandles[j].Length;
            }
            else if (arrHandles[j].Length != n)
            {
                return CalculatorMatrixFunctions.FailMath(MathErrorCode.DimensionMismatch, call.NamePosition, call.Name);
            }
        }

        if (!TryEvaluateScalarArg(args[3], in scope, limits, ref budget, in boundScope, ref arena, call.Name, out decimal windowDec, out CalculatorOutcome winErr))
        {
            return CalculatorArrayFunctions.ToEvalOutcome(winErr);
        }

        decimal fillVal = 0m;
        if (hasFill &&
            !TryEvaluateScalarArg(args[4], in scope, limits, ref budget, in boundScope, ref arena, call.Name, out fillVal, out CalculatorOutcome fillErr))
        {
            return CalculatorArrayFunctions.ToEvalOutcome(fillErr);
        }

        int w;
        if (hasFill)
        {
            if (!DslMathValidation.TryReadInt32InRange(windowDec, 1, DslArrayArena.MaxArrayLength, out w))
            {
                return DslEvalOutcome.Fail(
                    CalculatorErrorCode.DomainError,
                    call.NamePosition,
                    $"'{call.Name}' requires integer window in [1, {DslArrayArena.MaxArrayLength}].",
                    call.Name);
            }
        }
        else if (n == 0 || !DslMathValidation.TryReadInt32InRange(windowDec, 1, n, out w))
        {
            return DslEvalOutcome.Fail(
                CalculatorErrorCode.DomainError,
                call.NamePosition,
                $"'{call.Name}' requires non-empty equal-length arrays and integer window in [1, len].",
                call.Name);
        }

        if (scope.BoundArrays.Count + k > BoundArrayScope.MaxBoundArrays)
        {
            return DslEvalOutcome.Fail(
                CalculatorErrorCode.BudgetExceeded,
                call.NamePosition,
                "Bound array variable depth exceeded.",
                call.Name);
        }

        decimal normalizedFill = CalculatorArrayFunctions.NormalizeZero(fillVal);
        if (hasFill && n == 0)
        {
            return AllocateCombinatorResult(call, ref arena, ReadOnlySpan<decimal>.Empty);
        }

        int outLen = hasFill ? n : n - w + 1;
        decimal[]? rented = null;
        Span<decimal> results = outLen <= HybridStackAllocLimit
            ? stackalloc decimal[HybridStackAllocLimit].Slice(0, outLen)
            : (rented = ArrayPool<decimal>.Shared.Rent(outLen)).AsSpan(0, outLen);

        try
        {
            if (hasFill && n < w)
            {
                if (!budget.TryConsume(1))
                {
                    return DslEvalOutcome.Fail(CalculatorErrorCode.BudgetExceeded, call.NamePosition, "Execution budget exceeded.", call.Name);
                }

                results.Fill(normalizedFill);
                return AllocateCombinatorResult(call, ref arena, results);
            }

            int loopBaseCursor = arena.Cursor;
            for (int j = 0; j < k; j++)
            {
                if (arrHandles[j].Offset + arrHandles[j].Length > loopBaseCursor)
                {
                    throw new InvalidOperationException("Arena cursor invariant violated before zip rolling map.");
                }
            }

            BoundVariableScope maskedBoundScope = boundScope;
            for (int j = 0; j < k; j++)
            {
                maskedBoundScope = BoundVariableScope.RemoveByName(in maskedBoundScope, winVarNames[j]);
            }

            int writeOffset = 0;
            if (hasFill)
            {
                int warmupEnd = w - 1;
                results.Slice(0, warmupEnd).Fill(normalizedFill);
                writeOffset = warmupEnd;
            }

            int windowSteps = n - w + 1;
            for (int t = 0; t < windowSteps; t++)
            {
                if (!budget.TryConsume(1))
                {
                    return DslEvalOutcome.Fail(CalculatorErrorCode.BudgetExceeded, call.NamePosition, "Execution budget exceeded.", call.Name);
                }

                BoundArrayScope nextArrays = scope.BoundArrays;
                for (int j = 0; j < k; j++)
                {
                    var winHandle = new DslArrayHandle(arrHandles[j].Offset + t, w);
                    if (!nextArrays.TryPush(winVarNames[j], winHandle, out nextArrays))
                    {
                        return DslEvalOutcome.Fail(CalculatorErrorCode.BudgetExceeded, call.NamePosition, "Bound array variable depth exceeded.", call.Name);
                    }
                }

                DslEvalScope iterScope = scope with { BoundArrays = nextArrays };
                int stepCursor = arena.Cursor;
                DslEvalOutcome stepOutcome = CalculatorEvaluator.EvaluateValue(args[0], in iterScope, limits, ref budget, in maskedBoundScope, ref arena);
                arena.RestoreCursor(stepCursor);

                if (stepOutcome.Error is not null)
                {
                    return stepOutcome;
                }

                if (stepOutcome.Value.IsArray)
                {
                    return BodyMustBeScalarFailure(call);
                }

                results[writeOffset + t] = CalculatorArrayFunctions.NormalizeZero(stepOutcome.Value.Scalar);
            }

            return AllocateCombinatorResult(call, ref arena, results);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<decimal>.Shared.Return(rented, clearArray: false);
            }
        }
    }
}
