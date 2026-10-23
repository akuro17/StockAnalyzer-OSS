using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// A Column condition must read the metric its column names, whichever of the column's names the condition stores. The registration dialog stores the
/// description in CustomDisplayName and the short name in OutputName (<c>IndicatorRegistrationViewModel</c>), the extractor used to match that text by
/// keywords, and measured with distinct probe values that read no value for 39 columns and another metric for 26 of the 79 numeric columns.
/// </summary>
public class ScreenerColumnLookupTests
{
    private const int FirstProbeValue = 1001;

    private static readonly IReadOnlyList<CandleData> Bars = new List<CandleData>
    {
        new(new DateTime(2026, 3, 2), 10m, 12m, 9m, 11m, 100),
    };

    /// <summary>A metadata object whose numeric properties each hold a different whole number, and the number each property holds.</summary>
    private static (TickerMetadata Metadata, Dictionary<string, decimal> Values) ProbeMetadata()
    {
        object boxed = TickerMetadata.Unknown;
        var values = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        int next = FirstProbeValue;
        foreach (PropertyInfo property in typeof(TickerMetadata).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.SetMethod is null) continue;
            Type type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            object? value = type == typeof(decimal) ? (decimal)next
                : type == typeof(long) ? (long)next
                : type == typeof(int) ? next
                : type == typeof(double) ? (double)next
                : null;
            if (value is null) continue;
            property.SetValue(boxed, value);
            values[property.Name] = next++;
        }

        return ((TickerMetadata)boxed, values);
    }

    public static IEnumerable<object[]> NumericColumns()
    {
        var (_, values) = ProbeMetadata();
        return new ScreenerCatalogProvider().GetCatalogItems()
            .Where(i => i.CategoryType == ScreenerItemCategoryType.Column && i.ColumnMemberName is { } member && values.ContainsKey(member))
            .Select(i => new object[] { i.ShortName });
    }

    private static ScreenerCatalogItem Item(string shortName)
        => new ScreenerCatalogProvider().GetCatalogItems().Single(i => i.CategoryType == ScreenerItemCategoryType.Column && i.ShortName == shortName);

    [Fact]
    public void TheCatalogHasNumericColumnsToCheck() => Assert.True(NumericColumns().Count() > 70);

    [Theory]
    [MemberData(nameof(NumericColumns))]
    public void ADialogRegisteredColumnReadsThePropertyItNames(string shortName)
    {
        var (metadata, values) = ProbeMetadata();
        ScreenerCatalogItem item = Item(shortName);
        var side = new ScreenerIndicatorSideConfig { CategoryType = ScreenerItemCategoryType.Column, OutputName = item.ShortName, CustomDisplayName = item.DisplayName };

        Assert.Equal(values[item.ColumnMemberName!], ScreenerValueExtractor.Default.ExtractValueNullable(side, Bars, metadata));
    }

    [Theory]
    [MemberData(nameof(NumericColumns))]
    public void AColumnStoredByItsShortNameOrItsMemberNameReadsTheSameProperty(string shortName)
    {
        var (metadata, values) = ProbeMetadata();
        ScreenerCatalogItem item = Item(shortName);
        decimal expected = values[item.ColumnMemberName!];

        var byShortName = new ScreenerIndicatorSideConfig { CategoryType = ScreenerItemCategoryType.Column, OutputName = item.ShortName };
        var byMember = new ScreenerIndicatorSideConfig { CategoryType = ScreenerItemCategoryType.Column, OutputName = item.ColumnMemberName! };
        var byDescription = new ScreenerIndicatorSideConfig { CategoryType = ScreenerItemCategoryType.Column, CustomDisplayName = item.DisplayName };

        Assert.Equal(expected, ScreenerValueExtractor.Default.ExtractValueNullable(byShortName, Bars, metadata));
        Assert.Equal(expected, ScreenerValueExtractor.Default.ExtractValueNullable(byMember, Bars, metadata));
        Assert.Equal(expected, ScreenerValueExtractor.Default.ExtractValueNullable(byDescription, Bars, metadata));
    }

    [Fact]
    public void ACalculatedColumnStillFallsBackToItsCalculationWhenTheStoredValueIsMissing()
    {
        TickerMetadata metadata = TickerMetadata.Unknown with { CurrentPrice = 100m, BookValue = 50m };
        ScreenerCatalogItem item = Item("P/B (Live)");
        var side = new ScreenerIndicatorSideConfig { CategoryType = ScreenerItemCategoryType.Column, OutputName = item.ShortName, CustomDisplayName = item.DisplayName };

        Assert.Equal(2m, ScreenerValueExtractor.Default.ExtractValueNullable(side, Bars, metadata));
    }

    [Fact]
    public void AColumnWhoseValueIsMissingReadsNoValueInsteadOfAnotherMetric()
    {
        TickerMetadata metadata = TickerMetadata.Unknown with { TotalRevenue = 999m, Ebitda = 777m };
        foreach (string name in new[] { "Gross Margin", "Trailing P/E", "Market Cap / Employee", "EV/EBITDA" })
        {
            ScreenerCatalogItem item = Item(name);
            var side = new ScreenerIndicatorSideConfig { CategoryType = ScreenerItemCategoryType.Column, OutputName = item.ShortName, CustomDisplayName = item.DisplayName };

            Assert.Null(ScreenerValueExtractor.Default.ExtractValueNullable(side, Bars, metadata));
        }
    }

    [Fact]
    public void ANameThatIsNoColumnKeepsTheKeywordLookup()
    {
        TickerMetadata metadata = TickerMetadata.Unknown with { ReturnOnEquity = 0.15m };
        var side = new ScreenerIndicatorSideConfig { CategoryType = ScreenerItemCategoryType.Column, OutputName = "ROE" };

        Assert.Equal(0.15m, ScreenerValueExtractor.Default.ExtractValueNullable(side, Bars, metadata));
    }
}
