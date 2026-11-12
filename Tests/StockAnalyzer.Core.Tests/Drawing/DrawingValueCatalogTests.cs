using System;
using System.Linq;
using StockAnalyzer.Core.Drawing;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Core.Tests.Drawing;

public sealed class DrawingValueCatalogTests
{
    private static readonly string[] ExpectedNames =
    {
        "TrendLine", "Ray", "HorizontalLine", "VerticalLine", "Arrow", "PriceLabel",
        "FibonacciRetracement", "FibonacciFan", "FibonacciExpansion", "FibonacciChannel", "ParallelChannel",
        "GannFan", "GannBox", "SineLine", "TimeCycles",
    };

    private static readonly int[] ExpectedPointCounts = { 2, 2, 1, 1, 2, 1, 2, 2, 3, 3, 3, 2, 2, 2, 2 };

    [Fact]
    public void All_HoldsTheFifteenToolsOfTheSpecificationInOrder_WithUniqueNames()
    {
        Assert.Equal(ExpectedNames, DrawingValueCatalog.All.Select(d => d.Name).ToArray());
        Assert.Equal(ExpectedPointCounts, DrawingValueCatalog.All.Select(d => d.PointCount).ToArray());
        Assert.Equal(ExpectedNames.Length, DrawingValueCatalog.All.Select(d => d.Name).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void TryGet_IsOrdinalAndCaseSensitive_AndRejectsNullEmptyAndUnknownNames()
    {
        Assert.True(DrawingValueCatalog.TryGet("FibonacciRetracement", out DrawingValueDescriptor found));
        Assert.Equal("FibonacciRetracement", found.Name);

        Assert.False(DrawingValueCatalog.TryGet("fibonacciretracement", out _));
        Assert.False(DrawingValueCatalog.TryGet("Pitchfork", out _));
        Assert.False(DrawingValueCatalog.TryGet("", out _));
        Assert.False(DrawingValueCatalog.TryGet(null, out _));
    }

    [Fact]
    public void EveryDescriptor_CalculatesWithItsOwnPointCount_AndOnlyFibonacciRetracementReadsTheExtensionOption()
    {
        var t1 = new DateTime(2026, 1, 1);
        var anchors = new[]
        {
            new DrawingAnchor(t1, 100m),
            new DrawingAnchor(t1.AddDays(10), 150m),
            new DrawingAnchor(t1.AddDays(7.5), 130m),
        };
        var timestamp = new DateTime(2026, 1, 6, 12, 0, 0);

        foreach (DrawingValueDescriptor descriptor in DrawingValueCatalog.All)
        {
            var plain = new DrawingValueRequest(anchors.Take(descriptor.PointCount).ToArray(), timestamp, 123.456m, IndicatorColor.Gray, new DrawingValueOptions());
            var extended = plain with { Options = new DrawingValueOptions(ShowExtensions: true) };

            var plainValues = descriptor.Calculate(plain);
            var extendedValues = descriptor.Calculate(extended);

            Assert.NotEmpty(plainValues);
            if (descriptor.Name == "FibonacciRetracement")
            {
                Assert.True(extendedValues.Count > plainValues.Count);
            }
            else
            {
                Assert.Equal(plainValues, extendedValues);
            }
        }
    }
}
