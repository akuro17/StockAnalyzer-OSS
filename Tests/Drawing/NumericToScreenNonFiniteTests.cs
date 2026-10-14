using System;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Tests.Drawing;

/// <summary>
/// <c>NumericToScreen(x, y)</c> converts the price axis through <c>(decimal)y</c>, which throws <see cref="OverflowException"/> for NaN, infinities and
/// values of 2^96 or more. Every drawing tool and renderer calls it on the render path, so one non-representable price must yield a NaN screen Y
/// (which the callers already tolerate) instead of an exception. Finite, representable input keeps its previous result.
/// </summary>
public class NumericToScreenNonFiniteTests
{
    private static readonly DateTime MinTime = new(2026, 1, 1);
    private static readonly DateTime MaxTime = new(2026, 1, 30);

    public static TheoryData<string> TransformKinds => new() { "Linear", "Generic" };

    public static TheoryData<string, double> NonRepresentablePrices()
    {
        var data = new TheoryData<string, double>();
        foreach (string kind in new[] { "Linear", "Generic" })
        {
            foreach (double y in new[]
            {
                double.NaN, double.PositiveInfinity, double.NegativeInfinity,
                double.MaxValue, double.MinValue,
                (double)decimal.MaxValue, (double)decimal.MinValue,   // both are +-2^96 and do not fit into a decimal
            })
            {
                data.Add(kind, y);
            }
        }

        return data;
    }

    private static ICoordinateTransform Create(string kind, decimal minPrice = 50m, decimal maxPrice = 200m)
    {
        if (kind == "Linear")
        {
            return new LinearCoordinateTransform(MinTime, MaxTime, minPrice, maxPrice, 1000, 500);
        }

        var generic = new GenericCoordinateTransform(ChartAxisMode.GaplessTime, 1000, 500);
        generic.UpdateRange(MinTime, MaxTime, minPrice, maxPrice);
        return generic;
    }

    [Theory]
    [MemberData(nameof(NonRepresentablePrices))]
    public void NumericToScreen_NonRepresentablePrice_ReturnsNaNYWithoutThrowing(string kind, double y)
    {
        var transform = Create(kind);

        var point = transform.NumericToScreen(10, y);

        Assert.True(double.IsNaN(point.Y));
    }

    public static TheoryData<string, double> RepresentableButExtremePrices()
    {
        // The largest double below 2^96 still converts to a decimal, but the axis arithmetic (price - minimum price) can leave the decimal range.
        double belowLimit = Math.BitDecrement((double)decimal.MaxValue);
        var data = new TheoryData<string, double>();
        foreach (string kind in new[] { "Linear", "Generic" })
        {
            foreach (double y in new[] { belowLimit, -belowLimit, 1e-30, -1e-30, double.Epsilon, 1e27, -1e27 })
            {
                data.Add(kind, y);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(RepresentableButExtremePrices))]
    public void NumericToScreen_ExtremeButRepresentablePrice_NeverThrows(string kind, double y)
    {
        var transform = Create(kind);

        var exception = Record.Exception(() => transform.NumericToScreen(10, y));

        Assert.Null(exception);
    }

    [Theory]
    [MemberData(nameof(TransformKinds))]
    public void NumericToScreen_PriceWhoseAxisArithmeticOverflows_ReturnsNaNY(string kind)
    {
        // The price converts, but price - minimum price (an axis whose minimum is above about 4e13) is below decimal.MinValue.
        var transform = Create(kind, minPrice: 1e14m, maxPrice: 2e14m);

        var point = transform.NumericToScreen(10, -Math.BitDecrement((double)decimal.MaxValue));

        Assert.True(double.IsNaN(point.Y));
    }

    [Theory]
    [MemberData(nameof(TransformKinds))]
    public void NumericToScreen_NonRepresentablePrice_KeepsTheXCoordinate(string kind)
    {
        var transform = Create(kind);

        var valid = transform.NumericToScreen(10, 100.0);
        var invalid = transform.NumericToScreen(10, double.NaN);

        Assert.Equal(valid.X, invalid.X);
    }

    [Theory]
    [MemberData(nameof(TransformKinds))]
    public void NumericToScreen_FinitePrice_MatchesTheChartPointPath(string kind)
    {
        var transform = Create(kind);

        foreach (double y in new[] { 0.0, -3.0, 50.0, 100.5, 150.0, 199.99, 1_000_000.0 })
        {
            var expected = transform.ChartToScreen(new ChartPoint(MinTime, (decimal)y)).Y;

            Assert.Equal(expected, transform.NumericToScreen(10, y).Y);
        }
    }
}
