using System.Collections.Generic;

namespace StockAnalyzer.Core.Calculator;

internal enum UnaryOperator
{
    Plus,
    Minus,
}

internal enum BinaryOperator
{
    Add,
    Subtract,
    Multiply,
    Divide,
    Modulo,
}

internal readonly record struct BinaryChainTerm(BinaryOperator Operator, int Position, CalcNode Right);

internal abstract record CalcNode;

internal sealed record NumberNode(decimal Value, int Position) : CalcNode;

internal sealed record IdentifierNode(string Name, int Position) : CalcNode;

internal sealed record UnaryNode(UnaryOperator Operator, int Position, CalcNode Operand) : CalcNode;

internal sealed record PowerNode(CalcNode Base, int CaretPosition, CalcNode Exponent) : CalcNode;

/// <summary>
/// Flat iterative representation for left-associative additive and multiplicative chains,
/// keeping parser and evaluator call stacks O(1) across long flat expressions.
/// </summary>
internal sealed record BinaryChainNode(CalcNode First, IReadOnlyList<BinaryChainTerm> Rest) : CalcNode;

internal sealed record CallNode(string Name, int NamePosition, IReadOnlyList<CalcNode> Arguments) : CalcNode;
