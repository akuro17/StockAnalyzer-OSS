#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Analysis;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Analysis;

namespace StockAnalyzer.Core.Tests.TestHelpers;

/// <summary>
/// The scatter metric settings of a test: the shipped defaults, each one settable. A thin layer over the shared
/// <see cref="PythonIpcTestSettingsBase"/> stub; it re-implements the interface only to override the default interface members.
/// </summary>
public sealed class ScatterMetricTestSettings : PythonIpcTestSettingsBase, IStockAnalyzerSettings
{
    public int ScatterMaxRegisteredMetrics { get; set; } = ScatterMetricLimits.DefaultMaxRegisteredMetrics;
    public int ScatterMaxMetricOffset { get; set; } = ScatterMetricLimits.DefaultMaxOffset;
    public int ScatterMaxParallelism { get; set; } = ScatterMetricLimits.DefaultMaxParallelism;
    public int ScatterInputDebounceMilliseconds { get; set; } = ScatterMetricLimits.DefaultInputDebounceMilliseconds;
}

/// <summary>Builders for scatter metric tests: definitions made the way the registration screen makes them, and a deterministic bar series.</summary>
public static class ScatterMetricTestKit
{
    private static readonly ScreenerCatalogProvider Catalog = new();

    /// <summary>A fresh file path in the temp folder (the file does not exist yet).</summary>
    public static string NewTempFilePath() => Path.Combine(Path.GetTempPath(), $"scatter_metrics_test_{Guid.NewGuid():N}.json");

    /// <summary>An Indicator metric as the registration screen builds it: the first output series of the catalog, the given parameters.</summary>
    public static ScatterMetricDefinition Indicator(IndicatorType type, int offset = 0, string? output = null, params (string Key, object Value)[] parameters)
    {
        var source = new ScreenerIndicatorSideConfig
        {
            IndicatorType = type,
            OutputName = output ?? Catalog.GetOutputSeriesNames(type, IndicatorFactory.Default).First(),
            CategoryType = ScreenerItemCategoryType.Indicator,
            TimeFrame = TimeFrame.D1,
            Offset = offset,
        };
        foreach ((string key, object value) in parameters)
        {
            source.Parameters[key] = value;
        }

        return new ScatterMetricDefinition { Source = source };
    }

    /// <summary>A Column metric as the registration screen builds it: the catalog short name in <c>OutputName</c>, the description in <c>CustomDisplayName</c>.</summary>
    public static ScatterMetricDefinition Column(ScreenerCatalogItem item, int offset = 0) => new()
    {
        Source = new ScreenerIndicatorSideConfig
        {
            IndicatorType = IndicatorType.SMA,
            OutputName = item.ShortName,
            CustomDisplayName = item.DisplayName,
            CategoryType = ScreenerItemCategoryType.Column,
            TimeFrame = TimeFrame.D1,
            Offset = offset,
        },
    };

    /// <summary>The Column of the real Screener catalog with the given <c>ColumnMemberName</c> (Close, ChangePercent, MarketCap, ...).</summary>
    public static ScreenerCatalogItem CatalogColumn(string memberName) => Catalog.GetCatalogItems(IndicatorFactory.Default)
        .First(item => item.CategoryType == ScreenerItemCategoryType.Column && item.ColumnMemberName == memberName);

    /// <summary>Sets the time frame of <paramref name="metric"/> and returns it, so a Week or Month metric is built in one expression.</summary>
    public static ScatterMetricDefinition OnFrame(ScatterMetricDefinition metric, TimeFrame frame)
    {
        metric.Source.TimeFrame = frame;
        return metric;
    }

    /// <summary>
    /// What the evaluator hands to the Screener extractor for a Week or Month metric: a copy of the source whose time frame is D1 (the extractor fails closed on any
    /// other), read over the bars of the metric's own time frame. The metric itself is not changed. Test oracle for the evaluator's value.
    /// </summary>
    public static ScreenerIndicatorSideConfig DaySourceOf(ScatterMetricDefinition metric)
    {
        ScreenerIndicatorSideConfig source = metric.Source.Clone();
        source.TimeFrame = TimeFrame.D1;
        return source;
    }

    /// <summary>The first numeric (eligible) Column of the real catalog.</summary>
    public static ScreenerCatalogItem FirstNumericColumn() => Catalog.GetCatalogItems(IndicatorFactory.Default)
        .First(item => item.CategoryType == ScreenerItemCategoryType.Column && ScatterMetricEligibility.IsEligible(item));

    /// <summary>A deterministic daily bar series: a seeded random walk with a valid High/Low envelope and positive volume.</summary>
    public static List<CandleData> Bars(int count, int seed = 20261021, decimal start = 100m)
    {
        var random = new Random(seed);
        var bars = new List<CandleData>(count);
        decimal close = start;
        DateTime day = new(2024, 1, 1);
        for (int i = 0; i < count; i++)
        {
            decimal open = close;
            close = Math.Max(1m, open + (decimal)((random.NextDouble() - 0.5) * 4.0));
            decimal high = Math.Max(open, close) + (decimal)(random.NextDouble() * 1.5);
            decimal low = Math.Max(0.5m, Math.Min(open, close) - (decimal)(random.NextDouble() * 1.5));
            bars.Add(new CandleData(day, open, high, low, close, 1_000_000L + random.Next(0, 500_000)));
            day = day.AddDays(1);
        }

        return bars;
    }
}
