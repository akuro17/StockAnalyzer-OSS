using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Drawing;
using StockAnalyzer.Core.Drawing.Values;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Core.Tests.Drawing;

public sealed class DrawingValueCatalogTests
{
    private static readonly string[] ExpectedNames =
    {
        "TrendLine", "Ray", "HorizontalLine", "VerticalLine", "Arrow", "PriceLabel",
        "FibonacciRetracement", "FibonacciFan", "FibonacciExpansion", "FibonacciChannel", "ParallelChannel",
        "GannFan", "GannBox", "SineLine", "TimeCycles", "Pitchfork",
        "AngleTool", "GannSquareOfNine", "TargetPriceProjection", "LongPosition", "ShortPosition", "RegressionTrend", "AnchoredVWAP", "ClassicPivotPoints",
        "CrossLine", "DatePriceRange",
    };

    private static readonly int[] ExpectedPointCounts = { 2, 2, 1, 1, 2, 1, 2, 2, 3, 3, 3, 2, 2, 2, 2, 3, 2, 1, 3, 3, 3, 2, 1, 1, 1, 2 };

    private static readonly string[] CandleTools = { "RegressionTrend", "AnchoredVWAP", "ClassicPivotPoints", "DatePriceRange" };

    /// <summary>The one bar-based tool whose calculator accepts a missing bar list (the GUI object calculates before any bar is known): it still gives the figures that need no bars.</summary>
    private const string BarOptionalTool = "DatePriceRange";

    [Fact]
    public void TheNamesOtherLayersBranchOn_AreTheCatalogNames_SoARenameCannotLeaveThemBehind()
    {
        string[] named =
        {
            DrawingValueCatalog.TrendLineName, DrawingValueCatalog.RayName, DrawingValueCatalog.HorizontalLineName, DrawingValueCatalog.VerticalLineName,
            DrawingValueCatalog.ParallelChannelName, DrawingValueCatalog.LongPositionName, DrawingValueCatalog.ShortPositionName,
        };

        Assert.All(named, name => Assert.Contains(name, ExpectedNames));
        Assert.All(named, name => Assert.True(DrawingValueCatalog.TryGet(name, out DrawingValueDescriptor descriptor) && descriptor.Name == name));
    }

    [Fact]
    public void All_HoldsTheTwentySixToolsOfTheSpecificationInOrder_WithUniqueNames()
    {
        Assert.Equal(ExpectedNames, DrawingValueCatalog.All.Select(d => d.Name).ToArray());
        Assert.Equal(ExpectedPointCounts, DrawingValueCatalog.All.Select(d => d.PointCount).ToArray());
        Assert.Equal(CandleTools, DrawingValueCatalog.All.Where(d => d.RequiresCandles).Select(d => d.Name).ToArray());
        Assert.Equal(ExpectedNames.Length, DrawingValueCatalog.All.Select(d => d.Name).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void ABarBasedTool_GivenNoBars_ReturnsNoValues_AndEveryOtherToolIgnoresTheBars()
    {
        var anchors = new[] { new DrawingAnchor(new DateTime(2026, 1, 5), 100m), new DrawingAnchor(new DateTime(2026, 1, 20), 150m), new DrawingAnchor(new DateTime(2026, 1, 15), 130m) };
        foreach (DrawingValueDescriptor descriptor in DrawingValueCatalog.All)
        {
            var points = anchors.Take(descriptor.PointCount).ToArray();
            var withoutBars = new DrawingValueRequest(points, new DateTime(2026, 1, 20), 123.456m, IndicatorColor.Gray, new DrawingValueOptions());
            var withBars = withoutBars with { Candles = DrawingValueTestCandles.Daily() };

            if (descriptor.Name == BarOptionalTool)
            {
                // GUI parity: no special empty branch. Without bars the bar counts are 0 and the bar-derived figures are undefined; with bars they are defined.
                IReadOnlyList<DrawingCalculatedValue> none = descriptor.Calculate(withoutBars);
                IReadOnlyList<DrawingCalculatedValue> some = descriptor.Calculate(withBars);
                Assert.Equal(DatePriceRangeValues.ValueCount, none.Count);
                Assert.Equal(DatePriceRangeValues.ValueCount, some.Count);
                Assert.Equal(0m, none.Single(v => v.Key == "BarCount").NumericValue);
                Assert.True(some.Single(v => v.Key == "BarCount").NumericValue > 0m);
                Assert.Null(none[DatePriceRangeValues.HighestHighIndex].NumericValue);
                Assert.NotNull(some[DatePriceRangeValues.HighestHighIndex].NumericValue);
            }
            else if (descriptor.RequiresCandles)
            {
                Assert.Empty(descriptor.Calculate(withoutBars));
                Assert.NotEmpty(descriptor.Calculate(withBars));
            }
            else
            {
                Assert.Equal(descriptor.Calculate(withoutBars), descriptor.Calculate(withBars));
            }
        }
    }

    [Fact]
    public void DefaultOptions_AreTheChartObjectDefaults_AndOnlyTheDefaultValueIsAllZero()
    {
        var options = new DrawingValueOptions();

        Assert.Equal(2.0m, options.StdDevMultiplier);
        Assert.Equal(1.0m, options.Band1Multiplier);
        Assert.Equal(2.0m, options.Band2Multiplier);
        Assert.Equal(PriceType.Typical, options.PriceSource);
        Assert.Equal(PivotPeriod.Day, options.PivotPeriod);
        Assert.Equal(PivotLineExtendMode.None, options.PivotExtendMode);
        Assert.False(options.ShowExtensions || options.ExtendRight || options.ShowBand1 || options.ShowBand2);
        Assert.Null(options.IndicatorValueAtEntry);
        Assert.Equal(options, new DrawingValueOptions(false));
        Assert.Equal(0m, default(DrawingValueOptions).StdDevMultiplier);
    }

    [Fact]
    public void TryGet_IsOrdinalAndCaseSensitive_AndRejectsNullEmptyAndUnknownNames()
    {
        Assert.True(DrawingValueCatalog.TryGet("FibonacciRetracement", out DrawingValueDescriptor found));
        Assert.Equal("FibonacciRetracement", found.Name);

        Assert.False(DrawingValueCatalog.TryGet("fibonacciretracement", out _));
        Assert.True(DrawingValueCatalog.TryGet("Pitchfork", out DrawingValueDescriptor pitchfork));
        Assert.Equal(3, pitchfork.PointCount);
        Assert.False(DrawingValueCatalog.TryGet("pitchfork", out _));
        Assert.False(DrawingValueCatalog.TryGet("", out _));
        Assert.False(DrawingValueCatalog.TryGet(null, out _));
    }

    [Fact]
    public void EveryDescriptor_CalculatesWithItsOwnPointCount_AndOnlyFibonacciRetracementReadsTheExtensionOption_AndOnlyPitchforkReadsTheKind()
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
            var plain = new DrawingValueRequest(anchors.Take(descriptor.PointCount).ToArray(), timestamp, 123.456m, IndicatorColor.Gray, new DrawingValueOptions(), DrawingValueTestCandles.Daily());
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

            var schiff = plain with { Options = new DrawingValueOptions(PitchforkKind: PitchforkKind.Schiff) };
            var schiffValues = descriptor.Calculate(schiff);
            if (descriptor.Name == "Pitchfork")
            {
                Assert.NotEqual(plainValues, schiffValues);
                Assert.Equal("Schiff", schiffValues.Single(v => v.Key == "ForkType").FormattedText);
                Assert.Equal("Andrews", plainValues.Single(v => v.Key == "ForkType").FormattedText);
            }
            else
            {
                Assert.Equal(plainValues, schiffValues);
            }
        }
    }
}
