using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.ReadOnly;
using Xunit;

namespace StockAnalyzer.Core.Tests.ReadOnly;

public sealed class TickerFilterEvaluatorTests
{
    private static CandleData Bar(decimal close) => new(new DateTime(2026, 1, 2), close, close, close, close, 100);

    private static FilterRule Rule(string field, string op, string value, bool compareToField = false)
        => new() { Field = field, Operator = op, Value = value, IsCompareToField = compareToField };

    [Fact]
    public void Constructor_RejectsANullGateway()
    {
        Assert.Throws<ArgumentNullException>(() => new TickerFilterEvaluator(null!));
    }

    [Fact]
    public void NeedsCandles_IsFalseForNoRulesAndForMetadataOnlyRules()
    {
        Assert.False(TickerFilterEvaluator.NeedsCandles(null));
        Assert.False(TickerFilterEvaluator.NeedsCandles(Array.Empty<FilterRule>()));
        Assert.False(TickerFilterEvaluator.NeedsCandles(new[] { Rule("Sector", "==", "Technology"), Rule("Tag", "Contains", "AI") }));
    }

    [Fact]
    public void NeedsCandles_IsTrueWhenTheFieldOrTheComparedFieldIsCandleDependent()
    {
        Assert.True(TickerFilterEvaluator.NeedsCandles(new[] { Rule("Sector", "==", "Technology"), Rule("Close", ">", "1") }));
        Assert.True(TickerFilterEvaluator.NeedsCandles(new[] { Rule("TrailingEps", ">", "Close", compareToField: true) }));
        // A candle-dependent name in Value is only a field reference when the rule compares to a field.
        Assert.False(TickerFilterEvaluator.NeedsCandles(new[] { Rule("Sector", "==", "Close") }));
    }

    [Fact]
    public async Task FilterAsync_WithNoRules_ReturnsEverySymbolSortedAndReadsMetadataButNoBars()
    {
        var gateway = new CountingTickerGateway();
        var evaluator = new TickerFilterEvaluator(gateway);

        var result = await evaluator.FilterAsync(new[] { "ZZ", "AA", "MM" }, Array.Empty<FilterRule>());

        Assert.Equal(new[] { "AA", "MM", "ZZ" }, result);
        // The short-circuit for "no rules" belongs to the caller (TickerGroupResolver); the evaluator still builds each row.
        Assert.Equal(3, gateway.MetadataCallCount);
        Assert.Equal(0, gateway.CandlesCallCount);
    }

    [Fact]
    public async Task FilterAsync_AllRulesMustHold()
    {
        var gateway = new CountingTickerGateway
        {
            Metadata = new()
            {
                ["A"] = new TickerMetadata { Sector = "Technology" },
                ["B"] = new TickerMetadata { Sector = "Technology" },
                ["C"] = new TickerMetadata { Sector = "Energy" },
            },
            Candles = new() { ["A"] = new() { Bar(200m) }, ["B"] = new() { Bar(50m) }, ["C"] = new() { Bar(300m) } },
        };
        var evaluator = new TickerFilterEvaluator(gateway);

        var result = await evaluator.FilterAsync(new[] { "A", "B", "C" }, new[] { Rule("Sector", "==", "Technology"), Rule("Close", ">", "100") });

        Assert.Equal(new[] { "A" }, result);
    }

    [Fact]
    public async Task FilterAsync_ReturnsOrdinalOrderWhateverTheInputOrder()
    {
        var gateway = new CountingTickerGateway
        {
            Candles = new() { ["B"] = new() { Bar(9m) }, ["A-B"] = new() { Bar(9m) }, ["AB"] = new() { Bar(9m) } },
        };
        var evaluator = new TickerFilterEvaluator(gateway);

        var result = await evaluator.FilterAsync(new[] { "B", "AB", "A-B" }, new[] { Rule("Close", ">", "1") });

        Assert.Equal(new[] { "A-B", "AB", "B" }, result);
    }

    [Fact]
    public async Task FilterAsync_MetadataOnlyRules_NeverReadBars()
    {
        var gateway = new CountingTickerGateway
        {
            Metadata = new() { ["A"] = new TickerMetadata { Sector = "Technology" }, ["B"] = new TickerMetadata { Sector = "Energy" } },
        };
        var evaluator = new TickerFilterEvaluator(gateway);

        var result = await evaluator.FilterAsync(new[] { "A", "B" }, new[] { Rule("Sector", "==", "Technology") });

        Assert.Equal(new[] { "A" }, result);
        Assert.Equal(2, gateway.MetadataCallCount);
        Assert.Equal(0, gateway.CandlesCallCount);
    }

    [Fact]
    public async Task FilterAsync_SecondCallWithTheSameSymbols_ReadsNothingAgain()
    {
        var gateway = new CountingTickerGateway { Candles = new() { ["A"] = new() { Bar(200m) }, ["B"] = new() { Bar(5m) } } };
        var evaluator = new TickerFilterEvaluator(gateway);

        await evaluator.FilterAsync(new[] { "A", "B" }, new[] { Rule("Close", ">", "100") });
        int metadataAfterFirst = gateway.MetadataCallCount;
        int candlesAfterFirst = gateway.CandlesCallCount;
        var second = await evaluator.FilterAsync(new[] { "A", "B" }, new[] { Rule("Close", "<", "100") });

        Assert.Equal(new[] { "B" }, second);
        Assert.Equal(metadataAfterFirst, gateway.MetadataCallCount);
        Assert.Equal(candlesAfterFirst, gateway.CandlesCallCount);
    }

    [Fact]
    public async Task GetRowAsync_WithoutCandles_BuildsAMetadataOnlyRow()
    {
        var gateway = new CountingTickerGateway { Metadata = new() { ["A"] = new TickerMetadata { Sector = "Technology" } } };
        var evaluator = new TickerFilterEvaluator(gateway);

        var row = await evaluator.GetRowAsync("A", needsCandles: false);

        Assert.Equal("Technology", row.Sector);
        Assert.Equal(0m, row.Close);
        Assert.Equal(0, gateway.CandlesCallCount);
    }

    [Fact]
    public async Task GetRowAsync_WithCandles_ReadsTheLastTwoDailyBars()
    {
        var gateway = new CountingTickerGateway { Candles = new() { ["A"] = new() { Bar(10m), Bar(12m) } } };
        var evaluator = new TickerFilterEvaluator(gateway);

        var row = await evaluator.GetRowAsync("A", needsCandles: true);

        Assert.Equal(12m, row.Close);
        Assert.Equal(2m, row.Change);
        Assert.Equal(2, gateway.LastRequestedBarCount);
    }

    [Fact]
    public async Task GetRowAsync_AfterARowWithCandles_ServesThatRowEvenWhenCandlesAreNotNeeded()
    {
        var gateway = new CountingTickerGateway { Candles = new() { ["A"] = new() { Bar(12m) } } };
        var evaluator = new TickerFilterEvaluator(gateway);

        var withCandles = await evaluator.GetRowAsync("A", needsCandles: true);
        var withoutCandles = await evaluator.GetRowAsync("A", needsCandles: false);

        Assert.Same(withCandles, withoutCandles);
        Assert.Equal(1, gateway.MetadataCallCount);
    }

    [Fact]
    public async Task GetRowAsync_SameRowInstanceOnRepeatedCalls()
    {
        var gateway = new CountingTickerGateway { Candles = new() { ["A"] = new() { Bar(12m) } } };
        var evaluator = new TickerFilterEvaluator(gateway);

        var first = await evaluator.GetRowAsync("A", needsCandles: true);
        var second = await evaluator.GetRowAsync("a", needsCandles: true);

        Assert.Same(first, second);
        Assert.Equal(1, gateway.CandlesCallCount);
    }

    [Fact]
    public async Task GetRowAsync_ArgumentExceptionFromTheBarRead_YieldsARowWithoutBars()
    {
        var gateway = new CountingTickerGateway { CandleFailure = _ => new ArgumentException("bad symbol") };
        var evaluator = new TickerFilterEvaluator(gateway);

        var row = await evaluator.GetRowAsync("A", needsCandles: true);

        Assert.Equal(0m, row.Close);
        Assert.Null(row.LastUpdatedUtc);
    }

    [Fact]
    public async Task GetRowAsync_DataUnavailableFromTheBarRead_Propagates()
    {
        var gateway = new CountingTickerGateway { CandleFailure = _ => new DataUnavailableException("unreadable") };
        var evaluator = new TickerFilterEvaluator(gateway);

        await Assert.ThrowsAsync<DataUnavailableException>(() => evaluator.GetRowAsync("A", needsCandles: true));
    }

    [Fact]
    public async Task HasStoredBarsAsync_ReportsWhetherABarIsStored_AndReadsEachSymbolOnce()
    {
        var gateway = new CountingTickerGateway { Candles = new() { ["A"] = new() { Bar(12m) } } };
        var evaluator = new TickerFilterEvaluator(gateway);

        Assert.True(await evaluator.HasStoredBarsAsync("A"));
        Assert.True(await evaluator.HasStoredBarsAsync("a"));
        Assert.False(await evaluator.HasStoredBarsAsync("NONE"));
        Assert.False(await evaluator.HasStoredBarsAsync("NONE"));

        Assert.Equal(new[] { "A", "NONE" }, gateway.CandleSymbols);
    }

    [Fact]
    public async Task HasStoredBarsAsync_ThenAFilterWithACandleRule_ReadsTheBarsOnce()
    {
        var gateway = new CountingTickerGateway { Candles = new() { ["A"] = new() { Bar(10m), Bar(12m) } } };
        var evaluator = new TickerFilterEvaluator(gateway);

        bool hasBars = await evaluator.HasStoredBarsAsync("A");
        var matched = await evaluator.FilterAsync(new[] { "A" }, new[] { Rule("Close", ">", "11") });

        Assert.True(hasBars);
        Assert.Equal(new[] { "A" }, matched);
        Assert.Equal(1, gateway.CandlesCallCount);
        Assert.Equal(1, gateway.MetadataCallCount);
    }

    [Fact]
    public async Task HasStoredBarsAsync_AfterARowWithCandles_ReadsNothingAgain()
    {
        var gateway = new CountingTickerGateway { Candles = new() { ["A"] = new() { Bar(12m) } } };
        var evaluator = new TickerFilterEvaluator(gateway);
        await evaluator.GetRowAsync("A", needsCandles: true);

        bool hasBars = await evaluator.HasStoredBarsAsync("A");

        Assert.True(hasBars);
        Assert.Equal(1, gateway.CandlesCallCount);
    }

    [Fact]
    public async Task HasStoredBarsAsync_DataUnavailableFromTheBarRead_Propagates()
    {
        var gateway = new CountingTickerGateway { CandleFailure = _ => new DataUnavailableException("unreadable") };
        var evaluator = new TickerFilterEvaluator(gateway);

        await Assert.ThrowsAsync<DataUnavailableException>(() => evaluator.HasStoredBarsAsync("A"));
    }

    [Fact]
    public async Task FilterAsync_CancelledToken_ThrowsBeforeReadingTheFirstSymbol()
    {
        var gateway = new CountingTickerGateway();
        var evaluator = new TickerFilterEvaluator(gateway);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => evaluator.FilterAsync(new[] { "A" }, new[] { Rule("Close", ">", "1") }, cts.Token));

        Assert.Equal(0, gateway.MetadataCallCount);
        Assert.Equal(0, gateway.CandlesCallCount);
    }

    [Fact]
    public async Task FilterAsync_RejectsNullArguments()
    {
        var evaluator = new TickerFilterEvaluator(new CountingTickerGateway());

        await Assert.ThrowsAsync<ArgumentNullException>(() => evaluator.FilterAsync(null!, Array.Empty<FilterRule>()));
        await Assert.ThrowsAsync<ArgumentNullException>(() => evaluator.FilterAsync(Array.Empty<string>(), null!));
    }

    [Fact]
    public async Task FilterAsync_UnknownField_MatchesNothing()
    {
        var gateway = new CountingTickerGateway { Candles = new() { ["A"] = new() { Bar(9m) } } };
        var evaluator = new TickerFilterEvaluator(gateway);

        var result = await evaluator.FilterAsync(new[] { "A" }, new[] { Rule("NoSuchColumn", ">", "0") });

        Assert.Empty(result);
        Assert.Equal(Array.Empty<string>(), result.ToArray());
    }
}
