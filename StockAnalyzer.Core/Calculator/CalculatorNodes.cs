using System.Collections.Generic;

namespace StockAnalyzer.Core.Calculator;

internal enum UnaryOperator
{
    Plus,
    Minus,
    LogicalNot,
}

internal enum BinaryOperator
{
    Add,
    Subtract,
    Multiply,
    Divide,
    Modulo,
    Less,
    LessEqual,
    Greater,
    GreaterEqual,
    Equal,
    NotEqual,
}

internal enum LogicalOperator
{
    And,
    Or,
}

internal readonly record struct BinaryChainTerm(BinaryOperator Operator, int Position, CalcNode Right);

internal abstract record CalcNode;

internal sealed record NumberNode(decimal Value, int Position) : CalcNode;

internal sealed record IdentifierNode(string Name, int Position) : CalcNode;

internal sealed record UnaryNode(UnaryOperator Operator, int Position, CalcNode Operand) : CalcNode;

internal sealed record PowerNode(CalcNode Base, int CaretPosition, CalcNode Exponent) : CalcNode;

/// <summary>
/// Flat iterative representation for left-associative additive, multiplicative, relational, and equality chains,
/// keeping parser and evaluator call stacks O(1) across long flat expressions.
/// </summary>
internal sealed record BinaryChainNode(CalcNode First, IReadOnlyList<BinaryChainTerm> Rest) : CalcNode;

internal sealed record LogicalBinaryNode(LogicalOperator Operator, int Position, CalcNode Left, CalcNode Right) : CalcNode;

internal sealed record TernaryNode(CalcNode Condition, int QuestionPosition, CalcNode WhenTrue, int ColonPosition, CalcNode WhenFalse) : CalcNode;

internal sealed record CallNode(string Name, int NamePosition, IReadOnlyList<CalcNode> Arguments) : CalcNode;

internal sealed record ArrayLiteralNode(int OpenBracketPosition, IReadOnlyList<CalcNode> Elements) : CalcNode;

internal sealed record SubscriptNode(CalcNode Target, int OpenBracketPosition, CalcNode Index) : CalcNode;
