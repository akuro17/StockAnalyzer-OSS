#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using StockAnalyzer.Core.Calculator;
using Xunit;

namespace StockAnalyzer.Core.Tests.Calculator;

/// <summary>
/// DSL v5 review-adoption T7 (improvement task, plan W-19 / W-25 / W-35): characterisation of the rank kernels (a plain counting reference), identities and
/// invariances of the correlation and test functions, and parallel / repeated evaluation giving bit-identical results.
/// </summary>
public sealed class CalculatorDslV5PropertyTests
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

    /// <summary>The scalar or the array result of one expression as a flat list (a failure fails the test).</summary>
    private static decimal[] Eval(string expression, CalculatorLimits? limits = null)
    {
        ExpressionCalculator.CalculatorBatchResult result = ExpressionCalculator.EvaluateBatch(
            Empty,
            null,
            new[] { new ExpressionCalculator.CalculatorBatchItem("r", expression) },
            limits ?? Heavy)[0];
        Assert.True(result.IsOk, $"{expression} => {result.Error?.Code}: {result.Error?.Message}");
        return result.IsArray ? result.ArrayValues!.ToArray() : [result.ScalarValue];
    }

    private static string Vector(IEnumerable<decimal> values)
        => "[" + string.Join(", ", values.Select(v => v.ToString(CultureInfo.InvariantCulture))) + "]";

    private static decimal[] RandomValues(Random random, int count, int distinct)
        => Enumerable.Range(0, count).Select(_ => (decimal)random.Next(0, distinct) + (random.Next(0, 4) * 0.25m)).ToArray();

    // ---------------------------------------------------------------- ranks: a counting reference

    /// <summary>1-based rank with ties sharing the mean of the ranks they span: <c>1 + #(less) + (#(equal) - 1) / 2</c>.</summary>
    private static decimal[] ReferenceRanks(decimal[] values, bool descending)
        => values.Select(v =>
        {
            int better = values.Count(w => descending ? w > v : w < v);
            int equal = values.Count(w => w == v);
            return 1m + better + ((equal - 1) / 2m);
        }).ToArray();

    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(2, 5, 3)]
    [InlineData(17, 60, 6)]
    [InlineData(63, 200, 30)]
    [InlineData(99, 512, 400)]
    [InlineData(5, 512, 3)]
    public void ArrRankMatchesTheCountingReferenceAscendingAndDescending(int seed, int count, int distinct)
    {
        decimal[] values = RandomValues(new Random(seed), count, distinct);
        string literal = Vector(values);

        Assert.Equal(ReferenceRanks(values, descending: false), Eval($"arr_rank({literal})"));
        Assert.Equal(ReferenceRanks(values, descending: false), Eval($"arr_rank({literal}, 0)"));
        Assert.Equal(ReferenceRanks(values, descending: true), Eval($"arr_rank({literal}, 1)"));
    }

    [Fact]
    public void ArrRankHandlesAllEqualAndAlreadySortedInputs()
    {
        Assert.Equal(new[] { 2.5m, 2.5m, 2.5m, 2.5m }, Eval("arr_rank([7, 7, 7, 7])"));
        Assert.Equal(new[] { 1m, 2m, 3m, 4m, 5m }, Eval("arr_rank([1, 2, 3, 4, 5])"));
        Assert.Equal(new[] { 5m, 4m, 3m, 2m, 1m }, Eval("arr_rank([1, 2, 3, 4, 5], 1)"));
        Assert.Equal(new[] { 1.5m, 1.5m, 3m, 4.5m, 4.5m }, Eval("arr_rank([2, 2, 5, 9, 9])"));
    }

    [Theory]
    [InlineData(3, 40, 8)]
    [InlineData(11, 150, 20)]
    [InlineData(23, 250, 100)]
    public void SpearmanIsThePearsonCorrelationOfTheAverageRanks(int seed, int count, int distinct)
    {
        var random = new Random(seed);
        string x = Vector(RandomValues(random, count, distinct));
        string y = Vector(RandomValues(random, count, distinct));

        decimal spearman = Eval($"corr_spearman({x}, {y})")[0];
        decimal viaRanks = Eval($"corr_pearson(arr_rank({x}), arr_rank({y}))")[0];

        Assert.Equal(viaRanks, spearman);
    }

    // ---------------------------------------------------------------- ccf identities

    [Fact]
    public void CcfAtLagZeroIsThePearsonCorrelationAndSwappingTheSeriesNegatesTheLag()
    {
        var random = new Random(41);
        string x = Vector(RandomValues(random, 60, 30));
        string y = Vector(RandomValues(random, 60, 30));

        Assert.Equal(Eval($"corr_pearson({x}, {y})")[0], Eval($"ccf({x}, {y}, 0)")[0]);

        foreach (int lag in new[] { 1, 2, 7, 30, 59, -1, -5, -59 })
        {
            Assert.Equal(Eval($"ccf({x}, {y}, {lag})")[0], Eval($"ccf({y}, {x}, {-lag})")[0]);
        }
    }

    // ---------------------------------------------------------------- invariance under an affine change of units

    private static readonly decimal[] SampleX = [3.1m, 4.7m, 2.2m, 8.9m, 5.5m, 6.1m, 1.4m, 7.3m, 9.8m, 4.4m, 5.9m, 3.6m];
    private static readonly decimal[] SampleY = [2.0m, 4.1m, 1.9m, 7.2m, 6.0m, 5.3m, 2.8m, 8.1m, 8.4m, 3.9m, 6.6m, 2.5m];

    private static string Affine(decimal[] values, decimal scale, decimal shift)
        => Vector(values.Select(v => (scale * v) + shift));

    [Theory]
    [InlineData("0.00000001", "0")]
    [InlineData("0.0001", "1000")]
    [InlineData("1", "0")]
    [InlineData("10000", "-250000")]
    [InlineData("100000000", "1000000")]
    public void CorrelationsAndTheirStatisticsDoNotDependOnTheUnitOfTheSeries(string scaleText, string shiftText)
    {
        decimal scale = decimal.Parse(scaleText, CultureInfo.InvariantCulture);
        decimal shift = decimal.Parse(shiftText, CultureInfo.InvariantCulture);
        string x = Vector(SampleX);
        string y = Vector(SampleY);
        string xa = Affine(SampleX, scale, shift);
        string ya = Affine(SampleY, scale, shift);

        AssertClose(Eval($"corr_pearson({x}, {y})"), Eval($"corr_pearson({xa}, {ya})"), 1e-9);
        AssertClose(Eval($"corr_spearman({x}, {y})"), Eval($"corr_spearman({xa}, {ya})"), 1e-12);
        AssertClose(Eval($"corr_kendall({x}, {y})"), Eval($"corr_kendall({xa}, {ya})"), 1e-12);
        AssertClose(Eval($"ccf({x}, {y}, 2)"), Eval($"ccf({xa}, {ya}, 2)"), 1e-9);
        AssertClose(Eval($"ccf({x}, {y}, -3)"), Eval($"ccf({xa}, {ya}, -3)"), 1e-9);

        // The one-sample t statistic, p-value and df are unchanged when the hypothesised mean follows the unit change.
        decimal mu0 = (scale * 5m) + shift;
        AssertClose(Eval($"ttest_1samp({x}, 5)"), Eval($"ttest_1samp({xa}, {mu0.ToString(CultureInfo.InvariantCulture)})"), 1e-9);
        AssertClose(Eval($"ttest_2samp({x}, {y})"), Eval($"ttest_2samp({xa}, {ya})"), 1e-9);
    }

    [Theory]
    [InlineData("0.00001", "0")]
    [InlineData("1", "100000")]
    [InlineData("1000000", "0")]
    public void NormalityAndSerialDependenceTestsDoNotDependOnTheUnitOfTheSeries(string scaleText, string shiftText)
    {
        decimal scale = decimal.Parse(scaleText, CultureInfo.InvariantCulture);
        decimal shift = decimal.Parse(shiftText, CultureInfo.InvariantCulture);
        string x = Vector(SampleX);
        string xa = Affine(SampleX, scale, shift);

        AssertClose(Eval($"jb_test({x})"), Eval($"jb_test({xa})"), 1e-7);
        AssertClose(Eval($"ljung_box({x}, 3)"), Eval($"ljung_box({xa}, 3)"), 1e-7);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(50)]
    [InlineData(-300)]
    public void PolynomialRootsFollowATranslationOfTheVariable(int translation)
    {
        // (x - t - 1)(x - t - 2)(x - t - 4) and (x - t - 1)(x - t - 2)(x - t - 3)(x - t - 5): the roots are the base roots plus t.
        decimal t = translation;
        decimal[] cubic = Poly(1m + t, 2m + t, 4m + t);
        decimal[] quartic = Poly(1m + t, 2m + t, 3m + t, 5m + t);

        AssertRoots(new[] { 1.0, 2.0, 4.0 }.Select(r => r + translation).ToArray(), Eval($"solve_cubic({string.Join(", ", cubic.Select(Format))})"));
        AssertRoots(new[] { 1.0, 2.0, 3.0, 5.0 }.Select(r => r + translation).ToArray(), Eval($"solve_quartic({string.Join(", ", quartic.Select(Format))})"));
    }

    private static string Format(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    private static decimal[] Poly(params decimal[] roots)
    {
        decimal[] poly = [1m];
        foreach (decimal root in roots)
        {
            decimal[] next = new decimal[poly.Length + 1];
            for (int i = 0; i < poly.Length; i++)
            {
                next[i] += poly[i];
                next[i + 1] -= poly[i] * root;
            }

            poly = next;
        }

        return poly;
    }

    private static void AssertRoots(double[] expected, decimal[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.True(Math.Abs((double)actual[i] - expected[i]) <= 1e-6 * Math.Max(1.0, Math.Abs(expected[i])), $"root {i}: {actual[i]} vs {expected[i]}");
        }
    }

    private static void AssertClose(decimal[] expected, decimal[] actual, double relativeTolerance)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            double e = (double)expected[i];
            double a = (double)actual[i];
            Assert.True(Math.Abs(e - a) <= relativeTolerance * Math.Max(1.0, Math.Abs(e)), $"element {i}: expected {e}, actual {a} (relative tolerance {relativeTolerance})");
        }
    }

    [Fact]
    public void KsTwoSampleDocumentationExampleMatchesTheAsymptoticPValue()
    {
        // Documented example: two completely separated samples of five. D = 1; the asymptotic p-value is about 0.0038 while the exact permutation p-value
        // is 2 / C(10, 5) = 2 / 252 = 0.0079 (the asymptotic value is about half of it for such small samples).
        decimal[] ks = Eval("ks_2samp([1, 2, 3, 4, 5], [6, 7, 8, 9, 10])");
        Assert.Equal(1m, ks[0]);
        Assert.True(Math.Abs((double)ks[1] - 0.003792) < 2e-5, $"p = {ks[1]}");
        Assert.True(Math.Abs((2.0 / 252.0) / (double)ks[1] - 2.09) < 0.05);
    }

    // ---------------------------------------------------------------- parallel and repeated evaluation

    private static readonly string[] DeterminismExpressions =
    [
        "corr_spearman([3, 1, 2, 2, 5, 9, 9, 4], [1, 5, 2, 3, 3, 8, 7, 6])",
        "arr_rank([4, 4, 1, 9, 2, 2, 2, 7], 1)",
        "ccf([3, 1, 2, 2, 5, 9, 9, 4], [1, 5, 2, 3, 3, 8, 7, 6], -2)",
        "ttest_2samp([1, 2, 3, 4, 5.5], [2, 4, 6, 8, 10, 12])",
        "jb_test([1, 2, 3, 4, 5, 7, 9, 12, 20])",
        "ljung_box([1, 3, 2, 5, 4, 6, 5, 8, 7, 9], 3)",
        "ks_2samp([1, 2, 3, 4, 5], [6, 7, 8, 9, 10])",
        "solve_quartic(1, -11, 41, -61, 30)",
        "solve_cubic(1, -7, 14, -8)",
        "irr([-100, 30, 40, 50, 60])",
        "interp_cubic_spline([0, 1, 2, 3, 4], [0, 1, 8, 27, 64], 2.5)",
    ];

    [Fact]
    public void ParallelEvaluationGivesTheSameResultsAsSequentialEvaluation()
    {
        decimal[][] baseline = DeterminismExpressions.Select(e => Eval(e)).ToArray();
        var failures = new ConcurrentBag<string>();

        Parallel.For(0, 256, new ParallelOptions { MaxDegreeOfParallelism = 8 }, index =>
        {
            int which = index % DeterminismExpressions.Length;
            decimal[] result = Eval(DeterminismExpressions[which]);
            if (!result.SequenceEqual(baseline[which]))
            {
                failures.Add($"{DeterminismExpressions[which]}: [{string.Join(", ", result)}] vs [{string.Join(", ", baseline[which])}]");
            }
        });

        Assert.Empty(failures);
    }

    [Fact]
    public void ThousandRepeatedEvaluationsAreIdentical()
    {
        foreach (string expression in DeterminismExpressions)
        {
            decimal[] first = Eval(expression);
            for (int i = 0; i < 1000; i++)
            {
                Assert.True(first.SequenceEqual(Eval(expression)), $"{expression} changed on repetition {i}.");
            }
        }
    }
}
