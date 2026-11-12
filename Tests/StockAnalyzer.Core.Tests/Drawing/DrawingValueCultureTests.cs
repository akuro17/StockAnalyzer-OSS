using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using StockAnalyzer.Core.Drawing;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Core.Tests.Drawing;

/// <summary>The value texts of the drawing calculators never depend on the culture of the calling thread (they use DrawingValueFormat.Culture).</summary>
public sealed class DrawingValueCultureTests
{
    // de-DE and fr-FR: other decimal separators; ja-JP: other percent and number patterns; th-TH: Buddhist calendar (the year of a formatted date differs).
    private static readonly string[] OtherCultures = { "de-DE", "fr-FR", "ja-JP", "th-TH" };

    private static readonly DateTime T1 = new(2026, 1, 1);

    private static IReadOnlyList<DrawingCalculatedValue> Calculate(DrawingValueDescriptor descriptor, bool showExtensions)
    {
        var anchors = new[]
        {
            new DrawingAnchor(T1, 100m),
            new DrawingAnchor(T1.AddDays(10), 150m),
            new DrawingAnchor(T1.AddDays(7.5), 130m),
        };
        var request = new DrawingValueRequest(
            anchors.Take(descriptor.PointCount).ToArray(),
            new DateTime(2026, 1, 6, 12, 0, 0),
            123.456m,
            IndicatorColor.Gray,
            new DrawingValueOptions(showExtensions));
        return descriptor.Calculate(request);
    }

    private static T WithThreadCulture<T>(string cultureName, Func<T> action)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
        try
        {
            return action();
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void TheChosenCultures_DoDifferFromEnUs_SoTheComparisonCanFail()
    {
        Assert.Equal(",", CultureInfo.GetCultureInfo("de-DE").NumberFormat.NumberDecimalSeparator);
        Assert.Equal(".", DrawingValueFormat.Culture.NumberFormat.NumberDecimalSeparator);
        Assert.Equal("en-US", DrawingValueFormat.Culture.Name);
        Assert.Equal("2569", new DateTime(2026, 1, 1).ToString("yyyy", CultureInfo.GetCultureInfo("th-TH")));
    }

    /// <summary>
    /// The stated reason for DrawingValueFormat.Culture not being InvariantCulture (SA_ARCHITECTURE_RULES, Headless Drawing Values): the percent pattern of the
    /// two cultures differs, and the level labels use the P1 specifier. If this test fails, that sentence is wrong and must be reported before it is changed.
    /// </summary>
    [Fact]
    public void ThePercentSpecifier_GivesADifferentTextUnderInvariantCultureThanUnderEnUs()
    {
        float level = 0.236f;

        string enUs = string.Create(DrawingValueFormat.Culture, $"{level:P1}");
        string invariant = string.Create(CultureInfo.InvariantCulture, $"{level:P1}");

        Assert.Equal("23.6%", enUs);
        Assert.NotEqual(enUs, invariant);
    }

    [Fact]
    public void EveryDescriptor_GivesTheSameValuesUnderEveryThreadCulture_AsUnderEnUs()
    {
        foreach (DrawingValueDescriptor descriptor in DrawingValueCatalog.All)
        {
            foreach (bool showExtensions in descriptor.Name == "FibonacciRetracement" ? new[] { false, true } : new[] { false })
            {
                IReadOnlyList<DrawingCalculatedValue> reference = WithThreadCulture("en-US", () => Calculate(descriptor, showExtensions));
                Assert.NotEmpty(reference);

                foreach (string cultureName in OtherCultures)
                {
                    IReadOnlyList<DrawingCalculatedValue> actual = WithThreadCulture(cultureName, () => Calculate(descriptor, showExtensions));

                    Assert.True(reference.Count == actual.Count, $"{descriptor.Name} {cultureName}: {reference.Count} values under en-US, {actual.Count} under {cultureName}.");
                    for (int i = 0; i < reference.Count; i++)
                    {
                        Assert.True(reference[i] == actual[i], $"{descriptor.Name} {cultureName} value {i} ({reference[i].Key}): '{reference[i].FormattedText}' under en-US, '{actual[i].FormattedText}' under {cultureName}.");
                    }
                }
            }
        }
    }
}
