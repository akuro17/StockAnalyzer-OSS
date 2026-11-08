using StockAnalyzer.Core.MathUtils;

namespace StockAnalyzer.Core.Models;

/// <summary>
/// Immutable representation of a parsed synthetic/custom formula symbol expression (e.g. 7203-T / 7267-T).
/// </summary>
public readonly record struct SyntheticSymbolExpression(
    string LeftSymbol,
    string RightSymbol,
    SeriesOperation Operation,
    char OperatorChar,
    bool IsOperatorSupported,
    string CanonicalExpression);
