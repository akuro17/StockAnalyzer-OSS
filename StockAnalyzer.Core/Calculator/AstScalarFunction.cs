using System;
using System.Collections.Generic;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models.Indicators;

namespace StockAnalyzer.Core.Calculator;

/// <summary>
/// Zero-allocation <see cref="IScalarFunction"/> bridge wrapping a parsed calculator AST expression
/// and stack-bound dummy variable slot without cloning the caller's variable dictionary.
/// </summary>
internal readonly struct AstScalarFunction : IScalarFunction
{
    private readonly CalcNode _body;
    private readonly string _variableName;
    private readonly IReadOnlyDictionary<string, decimal> _names;
    private readonly CalculatorLimits _limits;
    private readonly BoundVariableScope _parentScope;

    public AstScalarFunction(
        CalcNode body,
        string variableName,
        IReadOnlyDictionary<string, decimal> names,
        CalculatorLimits limits,
        in BoundVariableScope parentScope)
    {
        _body = body;
        _variableName = variableName;
        _names = names;
        _limits = limits;
        _parentScope = parentScope;
    }

    public bool TryEvaluate(double x, ref DslExecutionBudget budget, out double y)
    {
        decimal? decX = IndicatorCalculationHelper.ToDecimalOrNull(x);
        if (decX is not { } validX)
        {
            budget.LastError = double.IsNaN(x) ? MathErrorCode.NonFiniteResult : MathErrorCode.ArithmeticOverflow;
            y = double.NaN;
            return false;
        }

        if (!_parentScope.TryPush(_variableName, validX, out BoundVariableScope nextScope))
        {
            budget.LastError = MathErrorCode.BudgetExceeded;
            y = double.NaN;
            return false;
        }

        CalculatorOutcome outcome = CalculatorEvaluator.Evaluate(_body, _names, _limits, ref budget, in nextScope);
        if (outcome.Error is { } err)
        {
            FunctorFailureBridge.Record(ref budget, err);
            y = double.NaN;
            return false;
        }

        y = (double)outcome.Value!.Value;
        return true;
    }
}

/// <summary>
/// Zero-allocation <see cref="INdScalarFunction"/> bridge wrapping a parsed calculator AST expression
/// and binding up to <see cref="DslMonteCarloMath.MaxDimensions"/> dummy variables without heap cloning.
/// </summary>
internal readonly struct AstNdScalarFunction : INdScalarFunction
{
    private readonly CalcNode _body;
    private readonly IReadOnlyList<string> _variableNames;
    private readonly DslEvalScope _scope;
    private readonly CalculatorLimits _limits;
    private readonly BoundVariableScope _parentScope;

    public AstNdScalarFunction(
        CalcNode body,
        IReadOnlyList<string> variableNames,
        in DslEvalScope scope,
        CalculatorLimits limits,
        in BoundVariableScope parentScope)
    {
        _body = body;
        _variableNames = variableNames;
        // Strip outer batch-arena array handles because AST integrands evaluate against an isolated local arena.
        _scope = new DslEvalScope(scope.ScalarNames, null);
        _limits = limits;
        _parentScope = parentScope;
    }

    public int Dimension => _variableNames.Count;

    public bool TryEvaluate(scoped ReadOnlySpan<double> x, ref DslExecutionBudget budget, out double y)
    {
        if (x.Length != _variableNames.Count)
        {
            budget.LastError = MathErrorCode.DimensionMismatch;
            y = double.NaN;
            return false;
        }

        BoundVariableScope currentScope = _parentScope;
        for (int i = 0; i < _variableNames.Count; i++)
        {
            decimal? decVal = IndicatorCalculationHelper.ToDecimalOrNull(x[i]);
            if (decVal is not { } validVal)
            {
                budget.LastError = double.IsNaN(x[i]) ? MathErrorCode.NonFiniteResult : MathErrorCode.ArithmeticOverflow;
                y = double.NaN;
                return false;
            }

            if (!currentScope.TryPush(_variableNames[i], validVal, out BoundVariableScope nextScope))
            {
                budget.LastError = MathErrorCode.BudgetExceeded;
                y = double.NaN;
                return false;
            }

            currentScope = nextScope;
        }

        CalculatorOutcome outcome = CalculatorEvaluator.EvaluateWithScope(_body, in _scope, _limits, ref budget, in currentScope);
        if (outcome.Error is { } err)
        {
            FunctorFailureBridge.Record(ref budget, err);
            y = double.NaN;
            return false;
        }

        y = (double)outcome.Value!.Value;
        return true;
    }
}

/// <summary>
/// Carries the original <see cref="CalculatorError"/> of a failed higher-order function body through the numerical kernels
/// (which only know <see cref="MathErrorCode"/>) back to the evaluator, so the wire code, position, message and token of the body error are not lost.
/// </summary>
internal static class FunctorFailureBridge
{
    /// <summary>Stores <paramref name="error"/> in the budget and marks the last error as a functor failure.</summary>
    public static void Record(ref DslExecutionBudget budget, CalculatorError error)
    {
        budget.LastFunctorFailure = DslFunctorFailure.Of((int)error.Code, error.Position, error.Message, error.Token);
        budget.LastError = MathErrorCode.FunctorFailure;
    }

    /// <summary>Clears the stored detail; called when a higher-order function starts and after the detail was consumed.</summary>
    public static void Clear(ref DslExecutionBudget budget) => budget.LastFunctorFailure = DslFunctorFailure.Unset;

    /// <summary>
    /// Converts a failed kernel result into the evaluator's outcome. A stored body failure is returned unchanged and consumed;
    /// <see cref="MathErrorCode.FunctorFailure"/> without a stored detail (a defect, never expected) is reported as a domain error of the call itself.
    /// </summary>
    public static CalculatorOutcome ToOutcome(MathErrorCode error, ref DslExecutionBudget budget, int callPosition, string callName)
    {
        if (error == MathErrorCode.FunctorFailure && budget.LastFunctorFailure.IsSet)
        {
            DslFunctorFailure stored = budget.LastFunctorFailure;
            Clear(ref budget);
            return new CalculatorOutcome(null, new CalculatorError((CalculatorErrorCode)stored.Code, stored.Position, stored.Message, stored.Token));
        }

        Clear(ref budget);
        return CalculatorElementaryFunctions.FromMathError(
            error == MathErrorCode.FunctorFailure ? MathErrorCode.DomainError : error,
            callPosition,
            callName);
    }
}
