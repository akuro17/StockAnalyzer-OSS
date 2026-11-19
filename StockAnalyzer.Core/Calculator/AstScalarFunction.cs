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
            budget.LastError = MapToMathError(err.Code);
            y = double.NaN;
            return false;
        }

        y = (double)outcome.Value!.Value;
        return true;
    }

    internal static MathErrorCode MapToMathError(CalculatorErrorCode code) => code switch
    {
        CalculatorErrorCode.BudgetExceeded => MathErrorCode.BudgetExceeded,
        CalculatorErrorCode.DivisionByZero => MathErrorCode.DivisionByZero,
        CalculatorErrorCode.ArithmeticOverflow => MathErrorCode.ArithmeticOverflow,
        CalculatorErrorCode.NonFiniteResult => MathErrorCode.NonFiniteResult,
        CalculatorErrorCode.ConvergenceFailure => MathErrorCode.ConvergenceFailure,
        CalculatorErrorCode.SingularMatrix => MathErrorCode.SingularMatrix,
        CalculatorErrorCode.DimensionMismatch => MathErrorCode.DimensionMismatch,
        _ => MathErrorCode.DomainError,
    };
}
