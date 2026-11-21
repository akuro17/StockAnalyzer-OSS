#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Calculator;
using Xunit;

namespace StockAnalyzer.Core.Tests.Calculator;

/// <summary>
/// DSL v5 review-adoption T9: the calculator results against reference values computed by an independent implementation (NumPy / SciPy / the closed form of the
/// definition) for fixed inputs, including scaled and translated variants. The values are embedded in <see cref="CalculatorDslV5ReferenceData"/>
/// (generated; the header names the Python, NumPy and SciPy versions and the time of the owner's run of <c>docs/sa_reference_check_dsl_v5</c>).
/// <para>
/// Tolerance policy (relative to <c>max(1, |reference|)</c> unless the case is absolute; the measured worst case of every group is at least 10 times smaller):
/// 1e-10 for the closed-form and kernel functions; 1e-12 for the decimal-exact ones (<c>arr_rank</c>, <c>cvar_emp</c>, <c>max_drawdown</c>, <c>c_mul_*</c>);
/// 1e-9 for the matrix functions (the Hilbert matrix has a condition number of about 1.5e4), <c>jb_test</c> (the shifted sample cancels), <c>bs_implied_vol</c>
/// (Brent with tol 1e-10) and <c>c_pow_*</c>; 1e-8 for <c>ode_rk4</c> (a method error, 400 steps); <c>ode_rk45</c> 20 x its <c>tol</c> (the tolerance is local, not global);
/// the minimizers are absolute: <c>opt_brent</c> / <c>opt_golden</c> 1e-6 (the resolution of a function-value comparison is sqrt(eps) = 1.5e-8) and
/// <c>opt_nelder_mead</c> / <c>opt_bfgs</c> 50 sqrt(eps max(1, f*)) (at least 5 x 1e-9 |x*| for a coordinate of 1e5, the relative stop of Nelder-Mead).
/// Eigen, singular and QR vectors are compared up to a sign per column (or row); R of the QR decomposition by its absolute values.
/// A Gauss rule approximates a non-polynomial integral, so the calculator is compared with the value of the rule itself (SciPy nodes and weights), and, where
/// the rule is exact (polynomial integrand of degree up to 2n - 1), also with the exact integral.
/// </para>
/// </summary>
public sealed class CalculatorDslV5ReferenceTests
{
    private static readonly IReadOnlyDictionary<string, decimal> Empty =
        new Dictionary<string, decimal>(StringComparer.Ordinal);

    private static readonly CalculatorLimits Heavy = new()
    {
        MaxExecutionOps = 800_000,
        MaxExpressionChars = 4096,
        MaxTokens = 2048,
        MaxArrayLiteralElements = 512,
    };

    private static readonly Dictionary<string, ReferenceCase> ById = CalculatorDslV5ReferenceData.Cases.ToDictionary(c => c.Id, StringComparer.Ordinal);

    public static IEnumerable<object[]> CaseIds() => CalculatorDslV5ReferenceData.Cases.Select(c => new object[] { c.Id });

    private static double[] Evaluate(string expression)
    {
        ExpressionCalculator.CalculatorBatchResult result = ExpressionCalculator.EvaluateBatch(
            Empty,
            null,
            new[] { new ExpressionCalculator.CalculatorBatchItem("r", expression) },
            Heavy)[0];
        Assert.True(result.IsOk, $"{expression} => {result.Error?.Code}: {result.Error?.Message}");
        return result.IsArray ? result.ArrayValues!.Select(v => (double)v).ToArray() : [(double)result.ScalarValue];
    }

    private static void AssertClose(string label, double[] actual, double[] expected, double tolerance, bool absolute)
    {
        Assert.True(actual.Length == expected.Length, $"{label}: {actual.Length} values, expected {expected.Length}.");
        for (int i = 0; i < expected.Length; i++)
        {
            double difference = Math.Abs(actual[i] - expected[i]);
            double allowed = absolute ? tolerance : tolerance * Math.Max(1.0, Math.Abs(expected[i]));
            Assert.True(difference <= allowed, $"{label}[{i}]: actual {actual[i]:R}, reference {expected[i]:R}, difference {difference:E2} > {allowed:E2}.");
        }
    }

    /// <summary>Matches the sign of every column (<paramref name="byColumn"/>) or row of the row-major matrix <paramref name="actual"/> to the reference.</summary>
    private static double[] AlignSigns(double[] actual, double[] reference, int rows, int columns, bool byColumn)
    {
        double[] aligned = (double[])actual.Clone();
        int groups = byColumn ? columns : rows;
        int length = byColumn ? rows : columns;
        for (int g = 0; g < groups; g++)
        {
            double dot = 0.0;
            for (int k = 0; k < length; k++)
            {
                int index = byColumn ? (k * columns) + g : (g * columns) + k;
                dot += actual[index] * reference[index];
            }

            if (dot < 0.0)
            {
                for (int k = 0; k < length; k++)
                {
                    int index = byColumn ? (k * columns) + g : (g * columns) + k;
                    aligned[index] = -actual[index];
                }
            }
        }

        return aligned;
    }

    [Theory]
    [MemberData(nameof(CaseIds))]
    public void CalculatorMatchesTheReferenceValue(string id)
    {
        ReferenceCase reference = ById[id];
        double[] actual = Evaluate(reference.Expression);

        switch (reference.Kind)
        {
            case ReferenceKind.ColumnsUpToSign:
                Assert.Equal(reference.Rows * reference.Columns, actual.Length);
                AssertClose(id, AlignSigns(actual, reference.Reference, reference.Rows, reference.Columns, byColumn: true), reference.Reference, reference.Tolerance, reference.Absolute);
                return;
            case ReferenceKind.RowsUpToSign:
                Assert.Equal(reference.Rows * reference.Columns, actual.Length);
                AssertClose(id, AlignSigns(actual, reference.Reference, reference.Rows, reference.Columns, byColumn: false), reference.Reference, reference.Tolerance, reference.Absolute);
                return;
            case ReferenceKind.AbsoluteValues:
                AssertClose(id, actual.Select(Math.Abs).ToArray(), reference.Reference.Select(Math.Abs).ToArray(), reference.Tolerance, reference.Absolute);
                return;
        }

        if (reference.RuleValue is double rule)
        {
            // A Gauss rule: the calculator must reproduce the rule; where the rule is exact it must also reproduce the exact integral.
            AssertClose(id + " (rule)", actual, [rule], reference.Tolerance, reference.Absolute);
            if (reference.RuleIsExact)
            {
                AssertClose(id + " (exact integral)", actual, reference.Reference, reference.Tolerance, reference.Absolute);
            }

            return;
        }

        AssertClose(id, actual, reference.Reference, reference.Tolerance, reference.Absolute);
    }

    [Fact]
    public void TheEmbeddedDataCoversEveryDomainOfTheExtension()
    {
        string[] functions = CalculatorDslV5ReferenceData.Cases.Select(c => c.Function).Distinct().ToArray();

        // 79 of the 83 functions of the extension have an independent reference; if_gt, if_ge, if_eq and step are comparisons whose definition is the contract itself.
        Assert.True(functions.Length >= 79, $"only {functions.Length} functions are covered.");
        Assert.Equal(CalculatorDslV5ReferenceData.Cases.Length, CalculatorDslV5ReferenceData.Cases.Select(c => c.Id).Distinct().Count());
    }
}
