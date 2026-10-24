#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Analysis;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Analysis;
using StockAnalyzer.Core.Tests.TestHelpers;
using Xunit;
using Xunit.Abstractions;

namespace StockAnalyzer.Core.Tests.Analysis;

/// <summary>
/// Plan S2.4 / finding F-28 (owner decision B): the registration screen offers only the Columns that give a number. The extractor is measured again
/// here, independently of <see cref="ScatterMetricEligibility"/>, and the exclusion table must equal the measurement in both directions.
/// </summary>
public class ScatterMetricColumnAvailabilityTests
{
    private readonly ITestOutputHelper _output;

    public ScatterMetricColumnAvailabilityTests(ITestOutputHelper output) => _output = output;

    /// <summary>A metadata record with every nullable decimal / double / long / int property set to 1 (a "fully filled" ticker).</summary>
    private static TickerMetadata FullyFilledMetadata()
    {
        object boxed = new TickerMetadata("S", "Long", "US", "Sector", "Industry", "USD", 1m, 1m);
        foreach (PropertyInfo property in typeof(TickerMetadata).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanWrite) continue;
            Type type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            object? value = type == typeof(decimal) ? 1m
                : type == typeof(double) ? 1.0
                : type == typeof(long) ? 1L
                : type == typeof(int) ? 1
                : type == typeof(float) ? 1f
                : null;
            if (value != null) property.SetValue(boxed, value);
        }

        return (TickerMetadata)boxed;
    }

    /// <summary>Every non-text Column of the catalog (eligible or not), built the way the registration screen builds it.</summary>
    private static List<ScreenerCatalogItem> NonTextColumns()
        => new ScreenerCatalogProvider().GetCatalogItems(StockAnalyzer.Core.Models.Indicators.IndicatorFactory.Default)
            .Where(item => item.CategoryType == ScreenerItemCategoryType.Column
                           && ScreenerTextFields.ResolveFieldName(ScatterMetricTestKit.Column(item).Source) == null)
            .ToList();

    [Fact]
    public void TheExclusionTable_EqualsTheColumnsWithoutAValue_ForAFullyFilledTicker()
    {
        var items = NonTextColumns();
        var bars = ScatterMetricTestKit.Bars(60);
        TickerMetadata metadata = FullyFilledMetadata();

        var table = new StringBuilder();
        var empty = new List<string>();
        foreach (ScreenerCatalogItem item in items)
        {
            decimal? value = ScreenerValueExtractor.Default.ExtractValueNullable(ScatterMetricTestKit.Column(item).Source, bars, metadata);
            table.AppendLine($"{item.GroupName,-17} {item.ShortName,-28} member={item.ColumnMemberName,-24} value={(value?.ToString() ?? "NULL")}");
            if (value is null) empty.Add(item.ColumnMemberName ?? item.ShortName);
        }

        _output.WriteLine($"Non-text Columns: {items.Count}; without a value for a fully filled ticker: {empty.Count}");
        _output.WriteLine(table.ToString());
        Assert.NotEmpty(items);
        Assert.Equal(13, ScatterMetricEligibility.ColumnsWithoutAValue.Count);
        Assert.True(empty.OrderBy(m => m, StringComparer.Ordinal).SequenceEqual(ScatterMetricEligibility.ColumnsWithoutAValue.OrderBy(m => m, StringComparer.Ordinal)),
            "The Columns without a value changed. Measured: " + string.Join(", ", empty.OrderBy(m => m, StringComparer.Ordinal)));
    }

    [Fact]
    public void EveryOfferedColumnGivesAValue_AndTheExcludedOnesAreNotOffered()
    {
        var all = NonTextColumns();
        var offered = all.Where(ScatterMetricEligibility.IsEligible).ToList();
        var bars = ScatterMetricTestKit.Bars(60);
        TickerMetadata metadata = FullyFilledMetadata();

        Assert.Equal(all.Count - ScatterMetricEligibility.ColumnsWithoutAValue.Count, offered.Count);
        Assert.Equal(87, offered.Count);
        foreach (ScreenerCatalogItem item in offered)
        {
            Assert.NotNull(ScreenerValueExtractor.Default.ExtractValueNullable(ScatterMetricTestKit.Column(item).Source, bars, metadata));
        }

        Assert.All(all.Except(offered), item => Assert.Contains(item.ColumnMemberName ?? item.ShortName, ScatterMetricEligibility.ColumnsWithoutAValue));
    }

    [Fact]
    public void TheSharedRules_RefuseAnExcludedColumn_InEveryFormItCanBeStored_AndAcceptAnOfferedOne()
    {
        var factory = StockAnalyzer.Core.Models.Indicators.IndicatorFactory.Default;
        foreach (ScreenerCatalogItem item in NonTextColumns())
        {
            bool excluded = ScatterMetricEligibility.ColumnsWithoutAValue.Contains(item.ColumnMemberName ?? item.ShortName);
            var withDescription = ScatterMetricTestKit.Column(item);
            var shortNameOnly = ScatterMetricTestKit.Column(item);
            shortNameOnly.Source.CustomDisplayName = string.Empty;

            foreach (var metric in new[] { withDescription, shortNameOnly })
            {
                ScatterMetricInvalidReason reason = ScatterMetricRules.Validate(metric, 500, factory);
                Assert.True(
                    excluded ? reason == ScatterMetricInvalidReason.ColumnWithoutValue : reason == ScatterMetricInvalidReason.None,
                    $"{item.ColumnMemberName} ({(metric == withDescription ? "description" : "short name")}): {reason}");
            }
        }
    }

    [Fact]
    public void ADefinitionOfAnExcludedColumn_IsDroppedOnLoad_AndTheOfferedOnesStay()
    {
        var excluded = NonTextColumns().First(item => ScatterMetricEligibility.ColumnsWithoutAValue.Contains(item.ColumnMemberName ?? item.ShortName));
        string path = ScatterMetricTestKit.NewTempFilePath();
        try
        {
            var file = new ScatterMetricFile
            {
                Metrics = { ScatterMetricTestKit.Indicator(IndicatorType.RSI), ScatterMetricTestKit.Column(excluded), ScatterMetricTestKit.Column(ScatterMetricTestKit.FirstNumericColumn()) },
            };
            System.IO.File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(file));

            var registry = new ScatterMetricRegistry(new ScatterMetricTestSettings(), null, path);
            registry.Loaded.GetAwaiter().GetResult();

            Assert.Equal(2, registry.Snapshot().Count);
            Assert.DoesNotContain(registry.Snapshot(), m => m.Source.CustomDisplayName == excluded.DisplayName);
            Assert.Equal(1, registry.TakeLoadDroppedCount());
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }
}
