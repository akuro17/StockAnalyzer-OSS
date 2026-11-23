using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models.Indicators;

namespace StockAnalyzer.Core.Calculator;

internal static partial class CalculatorHigherOrderFunctions
{
    public const int MaxStateDimension = 8;
    // 32 * sizeof(decimal) = 512 bytes stack frame cap for potentially recursive higher-order calls.
    private const int HybridStackAllocLimit = 32;

    /// <summary>
    /// <c>arr_scan_state(stepExprs, stateVars, itemVar, arr, initState, outputIndex)</c> and
    /// <c>arr_reduce_state(stepExprs, stateVars, itemVar, arr, initState)</c>:
    /// deterministic multi-state recurrent fold over a 1D array without modifying <see cref="BoundVariableScope"/>
    /// semantics for existing combinators.
    /// </summary>
    private static DslEvalOutcome EvaluateArrayScanOrReduceState(
        CallNode call,
        in DslEvalScope scope,
        CalculatorLimits limits,
        ref DslExecutionBudget budget,
        in BoundVariableScope boundScope,
        ref DslArrayArena arena,
        bool isScan)
    {
        IReadOnlyList<CalcNode> args = call.Arguments;
        if (args[0] is not ArrayLiteralNode stepLiteral ||
            stepLiteral.Elements.Count < 1 ||
            stepLiteral.Elements.Count > MaxStateDimension)
        {
            return DslEvalOutcome.Fail(
                CalculatorErrorCode.DomainError,
                CalculatorEvaluator.GetNodePosition(args[0]),
                $"'{call.Name}' requires a literal array of 1..{MaxStateDimension} state transition expressions as its first argument.",
                call.Name);
        }

        int stateDim = stepLiteral.Elements.Count;
        if (args[1] is not ArrayLiteralNode stateVarsLiteral)
        {
            return DslEvalOutcome.Fail(
                CalculatorErrorCode.DomainError,
                CalculatorEvaluator.GetNodePosition(args[1]),
                $"'{call.Name}' requires a literal array of state variable identifiers as its second argument.",
                call.Name);
        }

        if (stateVarsLiteral.Elements.Count != stateDim)
        {
            return DslEvalOutcome.Fail(
                CalculatorErrorCode.DimensionMismatch,
                CalculatorEvaluator.GetNodePosition(args[1]),
                $"'{call.Name}' has {stateDim} transition expressions but {stateVarsLiteral.Elements.Count} state variables.",
                call.Name);
        }

        if (!TryReadDummyIdentifier(args[2], call.Name, out string itemVarName, out DslEvalOutcome itemErr))
        {
            return itemErr;
        }

        if (!TryReadIdentifierList(call, stateVarsLiteral, itemVarName, out string[] stateNames, out DslEvalOutcome stateNameErr))
        {
            return stateNameErr;
        }

        DslEvalOutcome arrOutcome = CalculatorEvaluator.EvaluateValue(args[3], in scope, limits, ref budget, in boundScope, ref arena);
        if (arrOutcome.Error is not null)
        {
            return arrOutcome;
        }

        if (!arrOutcome.Value.IsArray)
        {
            return DslEvalOutcome.Fail(
                CalculatorErrorCode.DomainError,
                CalculatorEvaluator.GetNodePosition(args[3]),
                $"'{call.Name}' requires a 1D array as its fourth argument.",
                call.Name);
        }

        DslEvalOutcome initOutcome = CalculatorEvaluator.EvaluateValue(args[4], in scope, limits, ref budget, in boundScope, ref arena);
        if (initOutcome.Error is not null)
        {
            return initOutcome;
        }

        if (!initOutcome.Value.IsArray)
        {
            return DslEvalOutcome.Fail(
                CalculatorErrorCode.DomainError,
                CalculatorEvaluator.GetNodePosition(args[4]),
                $"'{call.Name}' requires a 1D initial state array as its fifth argument.",
                call.Name);
        }

        DslArrayHandle arrHandle = arrOutcome.Value.Array;
        DslArrayHandle initHandle = initOutcome.Value.Array;
        if (initHandle.Length != stateDim)
        {
            return CalculatorMatrixFunctions.FailMath(MathErrorCode.DimensionMismatch, call.NamePosition, call.Name);
        }

        int outputIndex = 0;
        if (isScan)
        {
            if (!TryEvaluateScalarArg(args[5], in scope, limits, ref budget, in boundScope, ref arena, call.Name, out decimal outIdxDec, out CalculatorOutcome idxErr))
            {
                return CalculatorArrayFunctions.ToEvalOutcome(idxErr);
            }

            if (!DslMathValidation.TryReadInt32InRange(outIdxDec, 0, stateDim - 1, out outputIndex))
            {
                return DslEvalOutcome.Fail(
                    CalculatorErrorCode.DomainError,
                    CalculatorEvaluator.GetNodePosition(args[5]),
                    $"outputIndex in '{call.Name}' must be an integer in [0, {stateDim - 1}].",
                    call.Name);
            }
        }

        int loopBaseCursor = arena.Cursor;
        if (arrHandle.Offset + arrHandle.Length > loopBaseCursor || initHandle.Offset + initHandle.Length > loopBaseCursor)
        {
            throw new InvalidOperationException("Arena cursor invariant violated before state scan/reduce.");
        }

        Span<decimal> curState = stackalloc decimal[stateDim];
        ReadOnlySpan<decimal> initSlice = arena.GetSlice(initHandle);
        for (int j = 0; j < stateDim; j++)
        {
            curState[j] = CalculatorArrayFunctions.NormalizeZero(initSlice[j]);
        }

        int n = arrHandle.Length;
        if (n == 0)
        {
            return isScan
                ? AllocateCombinatorResult(call, ref arena, ReadOnlySpan<decimal>.Empty)
                : AllocateCombinatorResult(call, ref arena, curState);
        }

        Span<decimal> nextState = stackalloc decimal[stateDim];
        decimal[]? rentedScan = null;
        Span<decimal> scanOut = isScan
            ? (n <= HybridStackAllocLimit
                ? stackalloc decimal[HybridStackAllocLimit].Slice(0, n)
                : (rentedScan = ArrayPool<decimal>.Shared.Rent(n)).AsSpan(0, n))
            : Span<decimal>.Empty;

        try
        {
            for (int i = 0; i < n; i++)
            {
                if (!budget.TryConsume(1))
                {
                    return DslEvalOutcome.Fail(CalculatorErrorCode.BudgetExceeded, call.NamePosition, "Execution budget exceeded.", call.Name);
                }

                decimal item = arena.GetSlice(arrHandle)[i];
                BoundVariableScope stepScope = boundScope;
                for (int j = 0; j < stateDim; j++)
                {
                    if (!stepScope.TryPush(stateNames[j], curState[j], out stepScope))
                    {
                        return DslEvalOutcome.Fail(CalculatorErrorCode.BudgetExceeded, call.NamePosition, "Bound variable depth exceeded.", call.Name);
                    }
                }

                if (!stepScope.TryPush(itemVarName, item, out stepScope))
                {
                    return DslEvalOutcome.Fail(CalculatorErrorCode.BudgetExceeded, call.NamePosition, "Bound variable depth exceeded.", call.Name);
                }

                int stepCursor = arena.Cursor;
                for (int j = 0; j < stateDim; j++)
                {
                    DslEvalOutcome stepOutcome = CalculatorEvaluator.EvaluateValue(stepLiteral.Elements[j], in scope, limits, ref budget, in stepScope, ref arena);
                    if (stepOutcome.Error is not null)
                    {
                        arena.RestoreCursor(stepCursor);
                        return stepOutcome;
                    }

                    if (stepOutcome.Value.IsArray)
                    {
                        arena.RestoreCursor(stepCursor);
                        return DslEvalOutcome.Fail(
                            CalculatorErrorCode.DomainError,
                            CalculatorEvaluator.GetNodePosition(stepLiteral.Elements[j]),
                            $"'{call.Name}' body must evaluate to a scalar.",
                            call.Name);
                    }

                    nextState[j] = CalculatorArrayFunctions.NormalizeZero(stepOutcome.Value.Scalar);
                }

                arena.RestoreCursor(stepCursor);
                nextState.CopyTo(curState);
                if (isScan)
                {
                    scanOut[i] = curState[outputIndex];
                }
            }

            return isScan
                ? AllocateCombinatorResult(call, ref arena, scanOut)
                : AllocateCombinatorResult(call, ref arena, curState);
        }
        finally
        {
            if (rentedScan is not null)
            {
                ArrayPool<decimal>.Shared.Return(rentedScan, clearArray: false);
            }
        }
    }

    /// <summary>
    /// <c>arr_rolling_map(expr, winVar, arr, window)</c>: slides a zero-copy <see cref="DslArrayHandle"/> window
    /// of length <c>window</c> over <c>arr</c> and evaluates scalar <c>expr</c> with <c>winVar</c> bound in <see cref="BoundArrayScope"/>.
    /// </summary>
    private static DslEvalOutcome EvaluateArrayRollingMap(
        CallNode call,
        in DslEvalScope scope,
        CalculatorLimits limits,
        ref DslExecutionBudget budget,
        in BoundVariableScope boundScope,
        ref DslArrayArena arena)
    {
        if (!TryReadDummyIdentifier(call.Arguments[1], call.Name, out string winVar, out DslEvalOutcome idErr))
        {
            return idErr;
        }

        DslEvalOutcome arrOutcome = CalculatorEvaluator.EvaluateValue(call.Arguments[2], in scope, limits, ref budget, in boundScope, ref arena);
        if (arrOutcome.Error is not null)
        {
            return arrOutcome;
        }

        if (!arrOutcome.Value.IsArray)
        {
            return DslEvalOutcome.Fail(
                CalculatorErrorCode.DomainError,
                CalculatorEvaluator.GetNodePosition(call.Arguments[2]),
                $"'{call.Name}' requires a 1D array as its third argument.",
                call.Name);
        }

        if (!TryEvaluateScalarArg(call.Arguments[3], in scope, limits, ref budget, in boundScope, ref arena, call.Name, out decimal windowDec, out CalculatorOutcome winErr))
        {
            return CalculatorArrayFunctions.ToEvalOutcome(winErr);
        }

        DslArrayHandle arrHandle = arrOutcome.Value.Array;
        int n = arrHandle.Length;
        if (n == 0 || !DslMathValidation.TryReadInt32InRange(windowDec, 1, n, out int w))
        {
            return DslEvalOutcome.Fail(
                CalculatorErrorCode.DomainError,
                call.NamePosition,
                $"'{call.Name}' requires a non-empty array and integer window in [1, len(arr)].",
                call.Name);
        }

        int loopBaseCursor = arena.Cursor;
        if (arrHandle.Offset + arrHandle.Length > loopBaseCursor)
        {
            throw new InvalidOperationException("Arena cursor invariant violated before rolling map.");
        }

        int m = n - w + 1;
        BoundVariableScope maskedBoundScope = BoundVariableScope.RemoveByName(in boundScope, winVar);
        decimal[]? rented = null;
        Span<decimal> results = m <= HybridStackAllocLimit
            ? stackalloc decimal[HybridStackAllocLimit].Slice(0, m)
            : (rented = ArrayPool<decimal>.Shared.Rent(m)).AsSpan(0, m);

        try
        {
            for (int t = 0; t < m; t++)
            {
                if (!budget.TryConsume(1))
                {
                    return DslEvalOutcome.Fail(CalculatorErrorCode.BudgetExceeded, call.NamePosition, "Execution budget exceeded.", call.Name);
                }

                var winHandle = new DslArrayHandle(arrHandle.Offset + t, w);
                if (!scope.BoundArrays.TryPush(winVar, winHandle, out BoundArrayScope nextArrays))
                {
                    return DslEvalOutcome.Fail(CalculatorErrorCode.BudgetExceeded, call.NamePosition, "Bound array variable depth exceeded.", call.Name);
                }

                DslEvalScope iterScope = scope with { BoundArrays = nextArrays };
                int stepCursor = arena.Cursor;
                DslEvalOutcome stepOutcome = CalculatorEvaluator.EvaluateValue(call.Arguments[0], in iterScope, limits, ref budget, in maskedBoundScope, ref arena);
                arena.RestoreCursor(stepCursor);

                if (stepOutcome.Error is not null)
                {
                    return stepOutcome;
                }

                if (stepOutcome.Value.IsArray)
                {
                    return BodyMustBeScalarFailure(call);
                }

                results[t] = CalculatorArrayFunctions.NormalizeZero(stepOutcome.Value.Scalar);
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

    /// <summary>
    /// <c>quad2d_gk(expr, xVar, yVar, ax, bx, ay, by)</c>: adaptive 2D Gauss-Kronrod G7/K15 quadrature.
    /// Validates dummy identifiers and all 4 scalar endpoints before applying degenerate interval shortcut.
    /// </summary>
    private static DslEvalOutcome EvaluateQuad2dGk(
        CallNode call,
        in DslEvalScope scope,
        CalculatorLimits limits,
        ref DslExecutionBudget budget,
        in BoundVariableScope boundScope,
        ref DslArrayArena arena)
    {
        if (!TryReadDistinctDummyPair(call, out string xVar, out string yVar, out DslEvalOutcome pairErr))
        {
            return pairErr;
        }

        IReadOnlyList<CalcNode> args = call.Arguments;
        if (!TryEvaluateScalarArg(args[3], in scope, limits, ref budget, in boundScope, ref arena, call.Name, out decimal ax, out CalculatorOutcome axErr))
            return CalculatorArrayFunctions.ToEvalOutcome(axErr);
        if (!TryEvaluateScalarArg(args[4], in scope, limits, ref budget, in boundScope, ref arena, call.Name, out decimal bx, out CalculatorOutcome bxErr))
            return CalculatorArrayFunctions.ToEvalOutcome(bxErr);
        if (!TryEvaluateScalarArg(args[5], in scope, limits, ref budget, in boundScope, ref arena, call.Name, out decimal ay, out CalculatorOutcome ayErr))
            return CalculatorArrayFunctions.ToEvalOutcome(ayErr);
        if (!TryEvaluateScalarArg(args[6], in scope, limits, ref budget, in boundScope, ref arena, call.Name, out decimal by, out CalculatorOutcome byErr))
            return CalculatorArrayFunctions.ToEvalOutcome(byErr);

        if (ax == bx || ay == by)
        {
            return DslEvalOutcome.Ok(0m);
        }

        DslEvalScope functorScope = scope with { ArenaSnapshot = arena.CreateSnapshot() };
        var fn2d = new AstBinaryScalarFunction(args[0], xVar, yVar, in functorScope, limits, in boundScope);
        MathResult<double> integral = DslQuadratureMath.Quad2dGk(
            fn2d,
            (double)ax,
            (double)bx,
            (double)ay,
            (double)by,
            ref budget);
        if (!integral.IsOk)
        {
            return CalculatorArrayFunctions.ToEvalOutcome(FunctorFailureBridge.ToOutcome(integral.Error, ref budget, call.NamePosition, call.Name));
        }

        return CalculatorArrayFunctions.ToEvalOutcome(
            CalculatorElementaryFunctions.FromMathResult(
                DslElementaryMath.NarrowDouble(integral.Value),
                call.NamePosition,
                call.Name));
    }

    private readonly struct AstBinaryScalarFunction : IBinaryScalarFunction
    {
        private readonly CalcNode _body;
        private readonly string _xVar;
        private readonly string _yVar;
        private readonly DslEvalScope _scope;
        private readonly CalculatorLimits _limits;
        private readonly BoundVariableScope _parentScope;

        public AstBinaryScalarFunction(
            CalcNode body,
            string xVar,
            string yVar,
            in DslEvalScope scope,
            CalculatorLimits limits,
            in BoundVariableScope parentScope)
        {
            _body = body;
            _xVar = xVar;
            _yVar = yVar;
            _scope = scope;
            _limits = limits;
            _parentScope = parentScope;
        }

        public bool TryEvaluate(double x, double y, ref DslExecutionBudget budget, out double z)
        {
            decimal? decX = IndicatorCalculationHelper.ToDecimalOrNull(x);
            decimal? decY = IndicatorCalculationHelper.ToDecimalOrNull(y);
            if (decX is not { } validX || decY is not { } validY)
            {
                budget.LastError = double.IsNaN(x) || double.IsNaN(y)
                    ? MathErrorCode.NonFiniteResult
                    : MathErrorCode.ArithmeticOverflow;
                z = double.NaN;
                return false;
            }

            if (!_parentScope.TryPush(_xVar, validX, out BoundVariableScope s1) ||
                !s1.TryPush(_yVar, validY, out BoundVariableScope s2))
            {
                budget.LastError = MathErrorCode.BudgetExceeded;
                z = double.NaN;
                return false;
            }

            CalculatorOutcome outcome = CalculatorEvaluator.EvaluateWithScope(_body, in _scope, _limits, ref budget, in s2);
            if (outcome.Error is { } err)
            {
                FunctorFailureBridge.Record(ref budget, err);
                z = double.NaN;
                return false;
            }

            z = (double)outcome.Value!.Value;
            return true;
        }
    }
}
