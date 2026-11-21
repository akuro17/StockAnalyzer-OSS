using System;
using System.Collections.Generic;

namespace StockAnalyzer.Core.Calculator;

/// <summary>
/// Recursive-descent parser with bounded nesting depth for group parentheses, function calls, unary prefixes, and right-associative power.
/// </summary>
internal sealed class CalculatorParser
{
    private readonly IReadOnlyList<CalcToken> _tokens;
    private readonly CalculatorLimits _limits;
    private int _position;
    private int _depth;

    private CalculatorParser(IReadOnlyList<CalcToken> tokens, CalculatorLimits limits)
    {
        _tokens = tokens;
        _limits = limits;
    }

    public static bool TryParse(
        IReadOnlyList<CalcToken> tokens,
        CalculatorLimits limits,
        out CalcNode? root,
        out CalculatorError? error)
    {
        var parser = new CalculatorParser(tokens, limits);
        if (!parser.TryParseAdditive(out root, out error))
        {
            return false;
        }

        if (parser.Current.Kind != CalcTokenKind.EndOfInput)
        {
            error = parser.SyntaxErrorAt(parser.Current);
            root = null;
            return false;
        }

        return true;
    }

    private CalcToken Current => _tokens[_position];

    private CalcToken Advance()
    {
        CalcToken token = _tokens[_position];
        if (_position + 1 < _tokens.Count)
        {
            _position++;
        }

        return token;
    }

    private bool TryEnterDepth(int tokenPosition, out CalculatorError? error)
    {
        if (_depth + 1 > _limits.MaxNestingDepth)
        {
            error = new CalculatorError(
                CalculatorErrorCode.TooDeep,
                tokenPosition,
                $"Nesting depth exceeds the limit of {_limits.MaxNestingDepth}.");
            return false;
        }

        _depth++;
        error = null;
        return true;
    }

    private bool TryParseAdditive(out CalcNode? node, out CalculatorError? error)
    {
        if (!TryParseMultiplicative(out CalcNode? first, out error))
        {
            node = null;
            return false;
        }

        List<BinaryChainTerm>? rest = null;
        while (Current.Kind is CalcTokenKind.Plus or CalcTokenKind.Minus)
        {
            CalcToken opToken = Advance();
            BinaryOperator op = opToken.Kind == CalcTokenKind.Plus ? BinaryOperator.Add : BinaryOperator.Subtract;
            if (!TryParseMultiplicative(out CalcNode? right, out error))
            {
                node = null;
                return false;
            }

            rest ??= new List<BinaryChainTerm>();
            rest.Add(new BinaryChainTerm(op, opToken.Position, right!));
        }

        node = rest is null ? first : new BinaryChainNode(first!, rest);
        return true;
    }

    private bool TryParseMultiplicative(out CalcNode? node, out CalculatorError? error)
    {
        if (!TryParseUnary(out CalcNode? first, out error))
        {
            node = null;
            return false;
        }

        List<BinaryChainTerm>? rest = null;
        while (Current.Kind is CalcTokenKind.Star or CalcTokenKind.Slash or CalcTokenKind.Percent)
        {
            CalcToken opToken = Advance();
            BinaryOperator op = opToken.Kind switch
            {
                CalcTokenKind.Star => BinaryOperator.Multiply,
                CalcTokenKind.Slash => BinaryOperator.Divide,
                _ => BinaryOperator.Modulo,
            };

            if (!TryParseUnary(out CalcNode? right, out error))
            {
                node = null;
                return false;
            }

            rest ??= new List<BinaryChainTerm>();
            rest.Add(new BinaryChainTerm(op, opToken.Position, right!));
        }

        node = rest is null ? first : new BinaryChainNode(first!, rest);
        return true;
    }

    private bool TryParseUnary(out CalcNode? node, out CalculatorError? error)
    {
        if (Current.Kind is CalcTokenKind.Plus or CalcTokenKind.Minus)
        {
            CalcToken opToken = Advance();
            if (!TryEnterDepth(opToken.Position, out error))
            {
                node = null;
                return false;
            }

            try
            {
                if (!TryParseUnary(out CalcNode? operand, out error))
                {
                    node = null;
                    return false;
                }

                UnaryOperator op = opToken.Kind == CalcTokenKind.Plus ? UnaryOperator.Plus : UnaryOperator.Minus;
                node = new UnaryNode(op, opToken.Position, operand!);
                return true;
            }
            finally
            {
                _depth--;
            }
        }

        return TryParsePower(out node, out error);
    }

    private bool TryParsePower(out CalcNode? node, out CalculatorError? error)
    {
        if (!TryParsePrimary(out CalcNode? baseNode, out error))
        {
            node = null;
            return false;
        }

        if (Current.Kind == CalcTokenKind.Caret)
        {
            CalcToken caretToken = Advance();
            if (!TryEnterDepth(caretToken.Position, out error))
            {
                node = null;
                return false;
            }

            try
            {
                if (!TryParseUnary(out CalcNode? exponent, out error))
                {
                    node = null;
                    return false;
                }

                node = new PowerNode(baseNode!, caretToken.Position, exponent!);
                return true;
            }
            finally
            {
                _depth--;
            }
        }

        node = baseNode;
        return true;
    }

    private bool TryParsePrimary(out CalcNode? node, out CalculatorError? error)
    {
        CalcToken token = Current;
        switch (token.Kind)
        {
            case CalcTokenKind.Number:
                Advance();
                node = new NumberNode(token.Value, token.Position);
                error = null;
                return true;

            case CalcTokenKind.Identifier:
                Advance();
                if (Current.Kind == CalcTokenKind.OpenParen)
                {
                    return TryParseCall(token, out node, out error);
                }

                node = new IdentifierNode(token.Text, token.Position);
                error = null;
                return true;

            case CalcTokenKind.OpenParen:
                Advance();
                if (!TryEnterDepth(token.Position, out error))
                {
                    node = null;
                    return false;
                }

                try
                {
                    if (!TryParseAdditive(out CalcNode? inner, out error))
                    {
                        node = null;
                        return false;
                    }

                    if (Current.Kind != CalcTokenKind.CloseParen)
                    {
                        error = SyntaxErrorAt(Current);
                        node = null;
                        return false;
                    }

                    Advance();
                    node = inner;
                    return true;
                }
                finally
                {
                    _depth--;
                }

            case CalcTokenKind.OpenBracket:
                return TryParseArrayLiteral(token, out node, out error);

            default:
                error = SyntaxErrorAt(token);
                node = null;
                return false;
        }
    }

    private bool TryParseArrayLiteral(CalcToken openBracket, out CalcNode? node, out CalculatorError? error)
    {
        Advance(); // consume '['
        if (!TryEnterDepth(openBracket.Position, out error))
        {
            node = null;
            return false;
        }

        try
        {
            var elements = new List<CalcNode>();
            if (Current.Kind != CalcTokenKind.CloseBracket)
            {
                while (true)
                {
                    if (elements.Count >= _limits.MaxArrayLiteralElements)
                    {
                        error = new CalculatorError(
                            CalculatorErrorCode.DomainError,
                            Current.Position,
                            $"Array literal exceeds the limit of {_limits.MaxArrayLiteralElements} elements.");
                        node = null;
                        return false;
                    }

                    if (!TryParseAdditive(out CalcNode? element, out error))
                    {
                        node = null;
                        return false;
                    }

                    elements.Add(element!);
                    if (Current.Kind == CalcTokenKind.Comma)
                    {
                        Advance();
                        continue;
                    }

                    break;
                }
            }

            if (Current.Kind != CalcTokenKind.CloseBracket)
            {
                error = SyntaxErrorAt(Current);
                node = null;
                return false;
            }

            Advance(); // consume ']'
            node = new ArrayLiteralNode(openBracket.Position, elements);
            return true;
        }
        finally
        {
            _depth--;
        }
    }

    private bool TryParseCall(CalcToken nameToken, out CalcNode? node, out CalculatorError? error)
    {
        CalcToken openParen = Advance(); // consume '('
        if (!TryEnterDepth(openParen.Position, out error))
        {
            node = null;
            return false;
        }

        try
        {
            var args = new List<CalcNode>();
            if (Current.Kind != CalcTokenKind.CloseParen)
            {
                while (true)
                {
                    if (!TryParseAdditive(out CalcNode? arg, out error))
                    {
                        node = null;
                        return false;
                    }

                    args.Add(arg!);
                    if (Current.Kind == CalcTokenKind.Comma)
                    {
                        Advance();
                        continue;
                    }

                    break;
                }
            }

            if (Current.Kind != CalcTokenKind.CloseParen)
            {
                error = SyntaxErrorAt(Current);
                node = null;
                return false;
            }

            Advance(); // consume ')'
            node = new CallNode(nameToken.Text, nameToken.Position, args);
            return true;
        }
        finally
        {
            _depth--;
        }
    }

    private CalculatorError SyntaxErrorAt(CalcToken token)
    {
        if (token.Kind == CalcTokenKind.EndOfInput)
        {
            return new CalculatorError(
                CalculatorErrorCode.SyntaxError,
                token.Position,
                $"Unexpected end of expression at position {token.Position}.");
        }

        string message = IsAdjacentScientificNotationSuffix(token)
            ? $"Unexpected token '{token.Text}' at position {token.Position}. Scientific notation is not supported; use decimal literals or * 10^n."
            : $"Unexpected token '{token.Text}' at position {token.Position}.";
        return new CalculatorError(CalculatorErrorCode.SyntaxError, token.Position, message);
    }

    private bool IsAdjacentScientificNotationSuffix(CalcToken token)
    {
        if (_position <= 0 || token.Kind != CalcTokenKind.Identifier)
        {
            return false;
        }

        CalcToken prev = _tokens[_position - 1];
        if (prev.Kind != CalcTokenKind.Number || prev.Position + prev.Text.Length != token.Position)
        {
            return false;
        }

        if (token.Text.Length >= 2 && (token.Text[0] is 'e' or 'E') && ExpressionCalculator.IsAsciiDigit(token.Text[1]))
        {
            return true;
        }

        return token.Text.Length == 1
            && (token.Text[0] is 'e' or 'E')
            && _position + 2 < _tokens.Count
            && _tokens[_position + 1].Kind is CalcTokenKind.Plus or CalcTokenKind.Minus
            && _tokens[_position + 1].Position == token.Position + 1
            && _tokens[_position + 2].Kind == CalcTokenKind.Number
            && _tokens[_position + 2].Position == _tokens[_position + 1].Position + 1;
    }
}
