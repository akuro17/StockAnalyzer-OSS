using System;
using StockAnalyzer.Core.MathUtils;

namespace StockAnalyzer.Core.Calculator;

/// <summary>
/// Evaluator dispatch for Phase 3 DSL scalar matrix projections (<c>det2</c>, <c>det3</c>, <c>solve2_x</c>, <c>solve2_y</c>).
/// </summary>
internal static class CalculatorMatrixFunctions
{
    public static bool TryEvaluate(
        string name,
        int position,
        scoped ReadOnlySpan<decimal> args,
        out CalculatorOutcome outcome)
    {
        switch (name)
        {
            case "det2":
                outcome = CalculatorElementaryFunctions.FromMathResult(
                    DslLinearAlgebraMath.Det2(args[0], args[1], args[2], args[3]),
                    position,
                    name);
                return true;

            case "det3":
                outcome = CalculatorElementaryFunctions.FromMathResult(
                    DslLinearAlgebraMath.Det3(
                        args[0], args[1], args[2],
                        args[3], args[4], args[5],
                        args[6], args[7], args[8]),
                    position,
                    name);
                return true;

            case "solve2_x":
            case "solve2_y":
                MathErrorCode err = DslLinearAlgebraMath.SolveLinearSystem2x2(
                    args[0], args[1], args[2], args[3], args[4], args[5],
                    out decimal x,
                    out decimal y);
                outcome = err == MathErrorCode.None
                    ? CalculatorEvaluator.Ok(name == "solve2_x" ? x : y)
                    : CalculatorElementaryFunctions.FromMathError(err, position, name);
                return true;

            default:
                outcome = default;
                return false;
        }
    }
}
