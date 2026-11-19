#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using StockAnalyzer.Core.Calculator;
using Xunit;

namespace StockAnalyzer.Core.Tests.Calculator;

public sealed class CalculatorRobustnessTests
{
    private static readonly IReadOnlyDictionary<string, decimal> Empty = new Dictionary<string, decimal>(StringComparer.Ordinal);

    [Fact]
    public void IndependentBigIntegerOracleMatches2000RandomIntegerExpressions()
    {
        var rng = new Random(20261118);

        for (int i = 0; i < 2000; i++)
        {
            (string expr, BigInteger expected) = GenerateExpr(rng, depth: 0);
            CalculatorOutcome outcome = ExpressionCalculator.Evaluate(expr, Empty, CalculatorLimits.Default);

            Assert.True(outcome.Error is null, $"Oracle expr #{i} '{expr}' failed: {outcome.Error}");
            Assert.Equal((decimal)expected, outcome.Value!.Value);
        }
    }

    private static (string Expr, BigInteger Value) GenerateExpr(Random rng, int depth)
    {
        if (depth >= 4 || (depth >= 1 && rng.Next(4) == 0))
        {
            int n = rng.Next(-50, 51);
            return n < 0 ? ($"({n})", new BigInteger(n)) : (n.ToString(System.Globalization.CultureInfo.InvariantCulture), new BigInteger(n));
        }

        (string leftExpr, BigInteger leftVal) = GenerateExpr(rng, depth + 1);
        (string rightExpr, BigInteger rightVal) = GenerateExpr(rng, depth + 1);
        int op = rng.Next(3);
        return op switch
        {
            0 => ($"({leftExpr}+{rightExpr})", leftVal + rightVal),
            1 => ($"({leftExpr}-{rightExpr})", leftVal - rightVal),
            _ => ($"({leftExpr}*{rightExpr})", leftVal * rightVal),
        };
    }

    [Fact]
    public void Fuzz10000RandomStringsNeverThrowsAndReturnsExclusiveOutcome()
    {
        const string alphabet = "0123456789.+-*/%^(), \t\nabcxyz_@";
        var rng = new Random(20261118);

        for (int i = 0; i < 10000; i++)
        {
            int len = rng.Next(0, 65);
            var sb = new StringBuilder(len);
            for (int j = 0; j < len; j++)
            {
                sb.Append(alphabet[rng.Next(alphabet.Length)]);
            }

            CalculatorOutcome outcome = ExpressionCalculator.Evaluate(sb.ToString(), Empty, CalculatorLimits.Default);
            Assert.True((outcome.Value is not null) ^ (outcome.Error is not null), $"Invalid outcome for fuzz #{i}");
        }
    }

    [Fact]
    public void LongFlatAdditiveChainOf1000TermsEvaluatesWithoutStackOverflow()
    {
        var sb = new StringBuilder("1");
        for (int i = 1; i < 1000; i++)
        {
            sb.Append("+1");
        }

        var wideLimits = new CalculatorLimits
        {
            MaxExpressionChars = CalculatorLimits.MaxAllowedExpressionChars,
            MaxTokens = CalculatorLimits.MaxAllowedTokens,
        };

        CalculatorOutcome outcome = ExpressionCalculator.Evaluate(sb.ToString(), Empty, wideLimits);
        Assert.Null(outcome.Error);
        Assert.Equal(1000m, outcome.Value);
    }

    [Fact]
    public void ParallelEvaluationOfAllVectorsMatchesSequentialResults()
    {
        IReadOnlyList<object?[]> vectors = ExpressionCalculatorVectorTests.AllVectors;
        var sequential = new CalculatorOutcome[vectors.Count];
        for (int i = 0; i < vectors.Count; i++)
        {
            string expr = (string)vectors[i][1]!;
            var vars = (IReadOnlyDictionary<string, decimal>?)vectors[i][2] ?? Empty;
            sequential[i] = ExpressionCalculator.Evaluate(expr, vars, CalculatorLimits.Default);
        }

        var parallel = new CalculatorOutcome[vectors.Count];
        Parallel.For(0, vectors.Count, i =>
        {
            string expr = (string)vectors[i][1]!;
            var vars = (IReadOnlyDictionary<string, decimal>?)vectors[i][2] ?? Empty;
            parallel[i] = ExpressionCalculator.Evaluate(expr, vars, CalculatorLimits.Default);
        });

        for (int i = 0; i < vectors.Count; i++)
        {
            Assert.Equal(sequential[i], parallel[i]);
        }
    }
}
