using System;
using System.Linq;
using System.Numerics;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Tests.Common;
using Xunit;

namespace StockAnalyzer.Core.Tests.MathUtils;

/// <summary>
/// Pins <see cref="OrderStatisticsMath"/> to the full-sort definition: the selection returns exactly the value a sorted copy holds, and the
/// type 7 quantile, median, IQR and MAD equal the formulas evaluated on the sorted copy. Both numeric types (decimal and double) share one implementation.
/// Specification: docs/indicators_formulas.md, record indicators_formulas_0594, subject OrderStatisticsMath.
/// </summary>
public class OrderStatisticsMathTests
{
    private static readonly int[] Lengths = { 1, 2, 3, 4, 5, 64, 65, 512, 513 };
    private static readonly string[] Patterns = { "random", "ascending", "descending", "equal", "duplicates", "organpipe" };

    private static T[] Make<T>(string pattern, int n, int seed)
        where T : struct, INumber<T>
    {
        var random = new Random(seed);
        var data = new T[n];
        for (int i = 0; i < n; i++)
        {
            double v = pattern switch
            {
                "random" => (random.NextDouble() * 200.0) - 100.0,
                "ascending" => i * 0.25,
                "descending" => (n - i) * 0.25,
                "equal" => 42.5,
                "duplicates" => random.Next(0, 5) * 1.5,
                "organpipe" => Math.Min(i, n - 1 - i) * 0.5,
                _ => throw new ArgumentOutOfRangeException(nameof(pattern)),
            };
            data[i] = T.CreateChecked(v);
        }

        return data;
    }

    private static int[] IndexesToCheck(int n) =>
        n <= 65
            ? Enumerable.Range(0, n).ToArray()
            : new[] { 0, 1, n / 4, n / 2, n - 2, n - 1 };

    private static void SelectEqualsTheSortedCopy<T>()
        where T : struct, INumber<T>
    {
        foreach (int n in Lengths)
        {
            foreach (string pattern in Patterns)
            {
                T[] source = Make<T>(pattern, n, 1000 + n);
                T[] sorted = (T[])source.Clone();
                Array.Sort(sorted);
                foreach (int k in IndexesToCheck(n))
                {
                    T[] work = (T[])source.Clone();
                    T selected = OrderStatisticsMath.Select<T>(work, k);
                    Assert.True(sorted[k] == selected, $"{typeof(T).Name} n={n} {pattern} k={k}: expected {sorted[k]} actual {selected}");
                    Assert.True(work[k] == selected, "the value must stand at index k");
                    for (int i = 0; i < k; i++)
                    {
                        Assert.True(work[i] <= selected, $"{typeof(T).Name} n={n} {pattern} k={k}: element {i} is greater than the k-th");
                    }

                    for (int i = k + 1; i < n; i++)
                    {
                        Assert.True(work[i] >= selected, $"{typeof(T).Name} n={n} {pattern} k={k}: element {i} is smaller than the k-th");
                    }

                    Array.Sort(work);
                    Assert.True(sorted.AsSpan().SequenceEqual(work), "the multiset must be unchanged");
                }
            }
        }
    }

    [Fact]
    public void Select_Decimal_EqualsTheFullSort() => SelectEqualsTheSortedCopy<decimal>();

    [Fact]
    public void Select_Double_EqualsTheFullSort() => SelectEqualsTheSortedCopy<double>();

    private static T ReferenceQuantile<T>(T[] sorted, T p)
        where T : struct, INumber<T>, IFloatingPoint<T>
    {
        int n = sorted.Length;
        T h = p * T.CreateChecked(n - 1);
        T floor = T.Floor(h);
        int k = int.CreateChecked(floor);
        T gamma = h - floor;
        T next = sorted[Math.Min(k + 1, n - 1)];
        return ((T.One - gamma) * sorted[k]) + (gamma * next);
    }

    private static void QuantileEqualsTheSortedFormula<T>()
        where T : struct, INumber<T>, IFloatingPoint<T>
    {
        double[] probabilities = { 0.0, 0.01, 0.25, 0.3, 0.5, 0.75, 0.9, 0.95, 1.0 };
        foreach (int n in Lengths)
        {
            foreach (string pattern in Patterns)
            {
                T[] source = Make<T>(pattern, n, 2000 + n);
                T[] sorted = (T[])source.Clone();
                Array.Sort(sorted);
                foreach (double probability in probabilities)
                {
                    T p = T.CreateChecked(probability);
                    T[] work = (T[])source.Clone();
                    T? actual = OrderStatisticsMath.QuantileType7<T>(work, p);
                    Assert.NotNull(actual);
                    Assert.True(ReferenceQuantile(sorted, p) == actual!.Value, $"{typeof(T).Name} n={n} {pattern} p={probability}");
                }
            }
        }
    }

    [Fact]
    public void QuantileType7_Decimal_EqualsTheSortedFormula() => QuantileEqualsTheSortedFormula<decimal>();

    [Fact]
    public void QuantileType7_Double_EqualsTheSortedFormula() => QuantileEqualsTheSortedFormula<double>();

    [Fact]
    public void Median_IsTheMiddleValueOrTheMeanOfTheTwoMiddleValues()
    {
        Assert.Equal(3m, OrderStatisticsMath.Median<decimal>(new[] { 5m, 1m, 3m, 4m, 2m }));
        Assert.Equal(2.5m, OrderStatisticsMath.Median<decimal>(new[] { 4m, 1m, 3m, 2m }));
        Assert.Equal(7.25m, OrderStatisticsMath.Median<decimal>(new[] { 7.25m }));
        Assert.Equal(2.5, OrderStatisticsMath.Median<double>(new[] { 4.0, 1.0, 3.0, 2.0 }));
    }

    [Fact]
    public void QuantileType7_HandValues()
    {
        // {1,2,3,4}: h = p * 3; p = 0.25 -> 0.75 -> 1 + 0.75 * (2 - 1) = 1.75; p = 0.75 -> 2.25 -> 3 + 0.25 * (4 - 3) = 3.25.
        Assert.Equal(1.75m, OrderStatisticsMath.QuantileType7<decimal>(new[] { 4m, 2m, 1m, 3m }, 0.25m));
        Assert.Equal(3.25m, OrderStatisticsMath.QuantileType7<decimal>(new[] { 4m, 2m, 1m, 3m }, 0.75m));
        Assert.Equal(1m, OrderStatisticsMath.QuantileType7<decimal>(new[] { 4m, 2m, 1m, 3m }, 0m));
        Assert.Equal(4m, OrderStatisticsMath.QuantileType7<decimal>(new[] { 4m, 2m, 1m, 3m }, 1m));
        Assert.Equal(1.5m, OrderStatisticsMath.InterquartileRange<decimal>(new[] { 4m, 2m, 1m, 3m }));
    }

    [Fact]
    public void MedianAbsoluteDeviation_HandValue_IsRobustToTheOutlier()
    {
        // {1,2,3,4,100}: median 3, |deviations| 2,1,0,1,97 -> sorted 0,1,1,2,97 -> MAD 1.
        var data = new[] { 100m, 1m, 3m, 4m, 2m };
        var scratch = new decimal[data.Length];
        Assert.Equal(1m, OrderStatisticsMath.MedianAbsoluteDeviation<decimal>(data, scratch));
    }

    [Fact]
    public void MedianAbsoluteDeviation_EqualsTheDefinitionOnTheSortedCopy()
    {
        foreach (int n in Lengths)
        {
            double[] source = Make<double>("random", n, 3000 + n);
            double[] sorted = (double[])source.Clone();
            Array.Sort(sorted);
            double median = ReferenceQuantile(sorted, 0.5);
            double[] deviations = sorted.Select(v => Math.Abs(v - median)).ToArray();
            Array.Sort(deviations);
            double expected = ReferenceQuantile(deviations, 0.5);

            double[] work = (double[])source.Clone();
            double? actual = OrderStatisticsMath.MedianAbsoluteDeviation<double>(work, new double[n]);
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void MadToSigma_IsTheInverseOfTheThirdQuartileOfTheNormal_AndTheSameLiteralInBothTypes()
    {
        Assert.Equal(1.0 / SpecialFunctionsMath.NormalQuantile(0.75), OrderStatisticsMath.MadToSigma, 14);
        Assert.Equal(1.482602218505602m, OrderStatisticsMath.MadToSigmaDecimal);
        Assert.Equal(OrderStatisticsMath.MadToSigma, (double)OrderStatisticsMath.MadToSigmaDecimal);
    }

    [Fact]
    public void UndefinedInputs_ReturnNull()
    {
        Assert.Null(OrderStatisticsMath.Median<double>(Array.Empty<double>()));
        Assert.Null(OrderStatisticsMath.Median<decimal>(Array.Empty<decimal>()));
        Assert.Null(OrderStatisticsMath.QuantileType7<double>(new[] { 1.0, double.NaN, 3.0 }, 0.5));
        Assert.Null(OrderStatisticsMath.QuantileType7<double>(new[] { 1.0, 2.0, 3.0 }, double.NaN));
        Assert.Null(OrderStatisticsMath.QuantileType7<double>(new[] { 1.0, 2.0, 3.0 }, -0.01));
        Assert.Null(OrderStatisticsMath.QuantileType7<double>(new[] { 1.0, 2.0, 3.0 }, 1.01));
        Assert.Null(OrderStatisticsMath.QuantileType7<decimal>(new[] { 1m, 2m, 3m }, 1.01m));
        Assert.Null(OrderStatisticsMath.InterquartileRange<double>(Array.Empty<double>()));
        Assert.Null(OrderStatisticsMath.MedianAbsoluteDeviation<double>(Array.Empty<double>(), Array.Empty<double>()));
    }

    [Fact]
    public void DecimalOverflow_ReturnsNullInsteadOfThrowing()
    {
        // Q(0.75) = MaxValue and Q(0.25) = MinValue, so the difference exceeds the decimal range.
        var data = new[] { decimal.MinValue, decimal.MinValue, decimal.MaxValue, decimal.MaxValue };
        Assert.Null(OrderStatisticsMath.InterquartileRange<decimal>(data));
    }

    [Fact]
    public void Select_WithAnIndexOutsideTheBuffer_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => OrderStatisticsMath.Select<double>(new[] { 1.0, 2.0 }, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => OrderStatisticsMath.Select<double>(new[] { 1.0, 2.0 }, -1));
        Assert.Throws<ArgumentException>(() => OrderStatisticsMath.MedianAbsoluteDeviation<double>(new[] { 1.0, 2.0 }, new double[1]));
    }

    [Fact]
    public void Select_AllocatesNothing()
    {
        var work = new double[512];
        var source = Make<double>("random", 512, 7);
        source.CopyTo(work, 0);
        OrderStatisticsMath.Select<double>(work, 256);

        long allocated = AllocationProbe.Minimum(() =>
        {
            for (int round = 0; round < 20; round++)
            {
                source.CopyTo(work, 0);
                OrderStatisticsMath.Select<double>(work, 256);
                OrderStatisticsMath.QuantileType7<double>(work, 0.9);
            }
        });

        Assert.True(allocated == 0, $"The kernels allocated {allocated} bytes in every one of {AllocationProbe.Samples} samples.");
    }
}
