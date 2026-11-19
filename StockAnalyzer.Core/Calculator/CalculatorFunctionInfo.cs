namespace StockAnalyzer.Core.Calculator;

/// <summary>
/// Metadata describing a built-in function supported by <see cref="ExpressionCalculator"/>.
/// </summary>
public sealed record CalculatorFunctionInfo(
    string Name,
    int MinArguments,
    int MaxArguments,
    string Signature,
    bool IsVariadic = false,
    bool IsStatisticalVariadic = false);
