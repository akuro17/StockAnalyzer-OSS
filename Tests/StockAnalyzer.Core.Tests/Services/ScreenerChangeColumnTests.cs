using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Utilities;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// Pins the "Change" / "Change %" Column values of <see cref="ScreenerValueExtractor"/> through all three code paths
/// (CandleData list, CoreCandleData list, nullable CoreCandleData list), and proves they equal <see cref="PriceChange"/>.
/// </summary>
public class ScreenerChangeColumnTests
{
    // Closes 100, 110, 99, 0, 50. The open of every bar is deliberately far from its close: the open must never matter.
    private static readonly decimal[] Closes = { 100m, 110m, 99m, 0m, 50m };

    private static List<CandleData> Candles()
        => Closes.Select((c, i) => new CandleData(new DateTime(2026, 3, 2).AddDays(i), c + 7m, c + 9m, Math.Max(0m, c - 9m), c, 1000)).ToList();

    private static List<CoreCandleData> CoreCandles()
        => Closes.Select((c, i) => new CoreCandleData(new DateTime(2026, 3, 2).AddDays(i), c + 7m, c + 9m, Math.Max(0m, c - 9m), c, 1000)).ToList();

    private static ScreenerIndicatorSideConfig Column(string name, int offset)
        => new() { CategoryType = ScreenerItemCategoryType.Column, OutputName = name, Offset = offset };

    // offset, expected Change, expected Change %.  Latest bar index = 4.
    public static IEnumerable<object[]> Cases() => new[]
    {
        new object[] { 0, 50m, 0m },      // close 50, previous close 0: the amount is the difference, the percentage is 0
        new object[] { 1, -99m, -100m },  // close 0, previous close 99
        new object[] { 2, -11m, -10m },   // close 99, previous close 110
        new object[] { 3, 10m, 10m },     // close 110, previous close 100
        new object[] { 4, 0m, 0m },       // first bar: no previous bar
    };

    // An Offset beyond the series has no bar: the value is missing (null), not the latest bar's value (spec S1; it used to fall back to the latest bar).
    [Fact]
    public void Change_OffsetBeyondSeries_IsMissing()
    {
        var extractor = ScreenerValueExtractor.Default;

        foreach (string name in new[] { "Change", "Change %" })
        {
            Assert.Null(extractor.ExtractValueNullable(Column(name, 9), CoreCandles(), default(TickerMetadata)));
            Assert.Null(extractor.ExtractValueNullable(Column(name, 9), Candles()));
            Assert.Equal(0m, extractor.ExtractValue(Column(name, 9), Candles()));
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Change_AllPaths_AreMeasuredFromPreviousClose(int offset, decimal expectedChange, decimal expectedPercent)
    {
        var extractor = ScreenerValueExtractor.Default;
        var candles = Candles();
        var coreCandles = CoreCandles();

        Assert.Equal(expectedChange, extractor.ExtractValue(Column("Change", offset), candles));
        Assert.Equal(expectedChange, extractor.ExtractValue(Column("Change", offset), coreCandles));
        Assert.Equal(expectedChange, extractor.ExtractValueNullable(Column("Change", offset), coreCandles, default(TickerMetadata)));

        foreach (string name in new[] { "Change %", "ChangePercent", "Ratio" })
        {
            Assert.Equal(expectedPercent, extractor.ExtractValue(Column(name, offset), candles));
            Assert.Equal(expectedPercent, extractor.ExtractValue(Column(name, offset), coreCandles));
            Assert.Equal(expectedPercent, extractor.ExtractValueNullable(Column(name, offset), coreCandles, default(TickerMetadata)));
        }
    }

    // The configuration the condition-registration dialog builds for a Column catalog item
    // (IndicatorRegistrationViewModel: OutputName = the item's short name, CustomDisplayName = the item's display name).
    private static ScreenerIndicatorSideConfig RegisteredColumn(string shortName, int offset = 0)
    {
        var item = new ScreenerCatalogProvider().GetCatalogItems()
            .Single(i => i.CategoryType == ScreenerItemCategoryType.Column && i.ShortName == shortName);
        return new ScreenerIndicatorSideConfig
        {
            CategoryType = ScreenerItemCategoryType.Column,
            OutputName = item.ShortName,
            CustomDisplayName = item.DisplayName,
            Offset = offset
        };
    }

    [Theory]
    [MemberData(nameof(RegisteredCases))]
    public void RegisteredColumn_ResolvesToTheCandleValue(string shortName, int offset, decimal expected)
    {
        var extractor = ScreenerValueExtractor.Default;

        Assert.Equal(expected, extractor.ExtractValue(RegisteredColumn(shortName, offset), Candles()));
        Assert.Equal(expected, extractor.ExtractValue(RegisteredColumn(shortName, offset), CoreCandles()));
        Assert.Equal(expected, extractor.ExtractValueNullable(RegisteredColumn(shortName, offset), CoreCandles(), default(TickerMetadata)));
    }

    // Latest bar (index 4): open 57, high 59, low 41, close 50, volume 1000, previous close 0.
    // Offset 3 (index 1): close 110, previous close 100.
    public static IEnumerable<object[]> RegisteredCases() => new[]
    {
        new object[] { "Close", 0, 50m },
        new object[] { "Open", 0, 57m },
        new object[] { "Volume", 0, 1000m },
        new object[] { "Change", 3, 10m },
        new object[] { "Change %", 3, 10m },
        new object[] { "Change", 0, 50m },
        new object[] { "Change %", 0, 0m },
    };

    // bar index, expected Change, expected Change %: the same bars as the offsets 0..4 of Cases (index = 4 - offset).
    public static IEnumerable<object[]> IndexCases() => new[]
    {
        new object[] { 4, 50m, 0m },
        new object[] { 3, -99m, -100m },
        new object[] { 2, -11m, -10m },
        new object[] { 1, 10m, 10m },
        new object[] { 0, 0m, 0m },
    };

    [Theory]
    [MemberData(nameof(IndexCases))]
    public void Change_BothCandleTypes_GiveTheSamePriceChange(int index, decimal expectedChange, decimal expectedPercent)
    {
        var candles = Candles();
        var coreCandles = CoreCandles();

        var fromStruct = PriceChange.FromPreviousClose(candles, index);
        var fromRecord = PriceChange.FromPreviousClose(coreCandles, index);

        Assert.Equal(fromStruct, fromRecord);
        Assert.Equal(expectedChange, fromStruct.Amount);
        Assert.Equal(expectedPercent, fromStruct.Percent);
    }
}
