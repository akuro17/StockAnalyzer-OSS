using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Indicators;

/// <summary>
/// N-1 (constraint check of WebAiExpansion plan 03, owner decision 2026-11-14: A, then D after the production-path check).
/// An indicator instance that is REUSED (the screener keeps one per type and thread) is given an empty candle list after a calculation with data.
/// <para>
/// Measured finding: the base <c>Calculate</c> returns an empty result before the indicator body runs and clears only the Main list, so the SECONDARY public lists of the
/// instance (ShortStop, VAH, VAL, Signal, Histogram, PercentD) keep the previous run's data. The same structure exists in MACD and Keltner Channel (their secondary lists are
/// cleared inside the body). No production code reads those instance properties: consumers read the returned result object (IndicatorOutputSeriesResolver.TryResolveSeries),
/// and ScreenerValueExtractor returns null for an empty candle list before it reaches an indicator (ScreenerValueExtractor.cs, ExtractValueNullable). The backlog entry
/// future_improvements_20261114_plan03_followups records the optional base-class hook.
/// </para>
/// These tests pin what production relies on: the RESULT of an empty-input call is empty in every series, and the screener value does not leak across calls.
/// </summary>
public class PlanThreeEmptyInputAfterDataTests
{
    public static IEnumerable<object[]> PlanThreeTypes() => new[]
    {
        IndicatorType.ChandelierExit, IndicatorType.T3, IndicatorType.McGinleyDynamic, IndicatorType.TSI,
        IndicatorType.StochRSI, IndicatorType.KVO, IndicatorType.VolumeProfileLevels,
    }.Select(t => new object[] { t });

    private static List<CoreCandleData> Candles(int count)
    {
        var candles = new List<CoreCandleData>();
        for (int i = 0; i < count; i++)
        {
            decimal price = 100m + 10m * (decimal)Math.Sin(i * 0.3) + (i % 7) * 0.25m;
            candles.Add(new CoreCandleData(new DateTime(2025, 1, 1).AddDays(i), price, price + 1.5m, price - 1.25m, price + 0.5m, 1000 + (i % 13) * 100));
        }
        return candles;
    }

    [Theory]
    [MemberData(nameof(PlanThreeTypes))]
    public void EmptyInputAfterData_GivesAnEmptyResultWithEverySeriesEmpty(IndicatorType type)
    {
        var indicator = IndicatorFactory.CreateStatic(type);
        Assert.NotNull(indicator);

        var first = indicator!.Calculate(Candles(300));
        Assert.True(first.IsSuccessful);
        Assert.NotEmpty(first.MainValues);

        var second = indicator.Calculate(new List<CoreCandleData>());

        Assert.True(second.IsSuccessful);
        Assert.Empty(second.MainValues);
        foreach (string name in second.SeriesNamesList)
        {
            Assert.Empty(second.GetSeries(name));
        }
    }

    [Theory]
    [MemberData(nameof(PlanThreeTypes))]
    public void TheScreenerGivesNoValueForEmptyInputAfterData_AndTheNextCalculationIsUnaffected(IndicatorType type)
    {
        var extractor = new ScreenerValueExtractor();
        var config = new ScreenerIndicatorSideConfig { IndicatorType = type, Parameters = new Dictionary<string, object>() };
        var candles = Candles(300);

        decimal? before = extractor.ExtractValueNullable(config, candles);
        decimal? empty = extractor.ExtractValueNullable(config, new List<CoreCandleData>());
        decimal? after = extractor.ExtractValueNullable(config, candles);

        Assert.NotNull(before);
        Assert.Null(empty);
        Assert.Equal(before, after);
    }
}
