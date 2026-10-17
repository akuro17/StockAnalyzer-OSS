#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Models.StoredPeriod;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using StockAnalyzer.Core.Services.StoredPeriod;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>The per-symbol bar counts of the Training Wizard warnings: the pure counting and the reader on real temporary Parquet files.</summary>
public sealed class StoredPeriodBarCountsTests : IDisposable
{
    private static readonly DateOnly First = RlSourceFixture.FirstDay;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sa_bar_counts_" + Guid.NewGuid().ToString("N"));

    public StoredPeriodBarCountsTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private static DateOnly Day(int offset) => First.AddDays(offset);
    private static StoredPeriodCalendar Calendar(string symbol, int first, int count)
        => new(symbol, Enumerable.Range(first, count).Select(Day).ToImmutableArray());

    // ----- Count -----

    [Fact]
    public void CountIncludesBothBounds_AndANullBoundIsUnbounded()
    {
        var dates = Calendar("A", 0, 10).Dates;   // days 0..9
        Assert.Equal(10, StoredPeriodBarCounts.Count(dates, null, null));
        Assert.Equal(4, StoredPeriodBarCounts.Count(dates, Day(3), Day(6)));
        Assert.Equal(7, StoredPeriodBarCounts.Count(dates, Day(3), null));
        Assert.Equal(7, StoredPeriodBarCounts.Count(dates, null, Day(6)));
        Assert.Equal(1, StoredPeriodBarCounts.Count(dates, Day(9), Day(9)));
    }

    [Fact]
    public void CountOfADateBetweenTwoBarsStartsAtTheNextBar_AndAnEmptyPeriodIsZero()
    {
        var dates = new[] { Day(0), Day(2), Day(4), Day(6) }.ToImmutableArray();   // gaps: a weekend-like hole
        Assert.Equal(2, StoredPeriodBarCounts.Count(dates, Day(3), Day(6)));
        Assert.Equal(2, StoredPeriodBarCounts.Count(dates, Day(1), Day(4)));
        Assert.Equal(0, StoredPeriodBarCounts.Count(dates, Day(5), Day(5)));
        Assert.Equal(0, StoredPeriodBarCounts.Count(dates, Day(6), Day(2)));         // start after end
        Assert.Equal(0, StoredPeriodBarCounts.Count(dates, Day(7), null));           // after every bar
        Assert.Equal(0, StoredPeriodBarCounts.Count(dates, null, Day(-1)));          // before every bar
        Assert.Equal(0, StoredPeriodBarCounts.Count(ImmutableArray<DateOnly>.Empty, null, null));
        Assert.Equal(0, StoredPeriodBarCounts.Count(default, null, null));
    }

    // ----- Build -----

    [Fact]
    public void BuildReportsTheRequiredStartTheMinimumStartAndTheCountsFromIt()
    {
        var calendars = new[] { Calendar("B", 20, 50), Calendar("A", 0, 100) };   // B: days 20..69, A: days 0..99
        var infos = StoredPeriodBarCounts.Build(calendars, requiredBars: 10, start: Day(40), end: null);

        Assert.Equal(new[] { "A", "B" }, infos.Select(i => i.Symbol));            // ordinal order whatever the input order
        var a = infos[0]; var b = infos[1];
        Assert.Equal((60, 100, Day(90)), (a.BarsInRange, a.BarsUpToEnd, a.RequiredStart!.Value));     // days 40..99 = 60 bars; 10th from the end = 90
        Assert.Equal((30, 50, Day(60)), (b.BarsInRange, b.BarsUpToEnd, b.RequiredStart!.Value));      // days 40..69 = 30 bars; 10th from the end = 60
        Assert.Equal(40, a.BarsFromMinimumStart);                                  // minimum start = day 60: days 60..99
        Assert.Equal(10, b.BarsFromMinimumStart);                                  // days 60..69
    }

    [Fact]
    public void BuildHonoursTheEndBound_AndAShortSymbolHasNoRequiredStart()
    {
        var calendars = new[] { Calendar("A", 0, 100), Calendar("SHORT", 0, 5) };
        var infos = StoredPeriodBarCounts.Build(calendars, requiredBars: 10, start: null, end: Day(49));
        var a = infos.Single(i => i.Symbol == "A");
        var shortOne = infos.Single(i => i.Symbol == "SHORT");
        Assert.Equal((50, 50, Day(40)), (a.BarsInRange, a.BarsUpToEnd, a.RequiredStart!.Value));
        Assert.Equal((5, 5, (DateOnly?)null), (shortOne.BarsInRange, shortOne.BarsUpToEnd, shortOne.RequiredStart));
        Assert.Equal(0, shortOne.BarsFromMinimumStart);                             // counted from the minimum start (day 40): all its 5 bars lie before it
    }

    [Fact]
    public void BuildWithoutAnyReachableSymbolHasNoMinimumStart_AndANonPositiveRequirementIsEmpty()
    {
        var infos = StoredPeriodBarCounts.Build(new[] { Calendar("S", 0, 3) }, 10, null, null);
        var only = Assert.Single(infos);
        Assert.Null(only.RequiredStart);
        Assert.Null(only.BarsFromMinimumStart);
        Assert.Empty(StoredPeriodBarCounts.Build(new[] { Calendar("S", 0, 3) }, 0, null, null));
        Assert.Empty(StoredPeriodBarCounts.Build(Array.Empty<StoredPeriodCalendar>(), 10, null, null));
    }

    // ----- the reader -----

    private static void Write(string folder, string symbol, int count, int offset = 0, int step = 1)
    {
        Directory.CreateDirectory(folder);
        RlSourceFixture.CreateParquet(Path.Combine(folder, symbol + ".parquet"),
            $"SELECT DATE '{First:yyyy-MM-dd}'+{step}*CAST(range+{offset} AS INTEGER) AS date,100::FLOAT AS open,110::FLOAT AS high,90::FLOAT AS low,100::FLOAT AS close,1000::BIGINT AS volume FROM range({count})");
    }

    private static StoredPeriodAdvisor Advisor(int maximumSymbols = StoredPeriodLimits.MaximumSymbols, MarketDataSettings? settings = null)
        => new(settings is null ? null : Microsoft.Extensions.Options.Options.Create(settings), TimeProvider.System, maximumSymbols);

    [Fact]
    public void TheReaderCountsTheStoredBarsOfEverySymbolWithoutAFinalityRule()
    {
        Write(_root, "AAA", 100);
        Write(_root, "BBB", 30, offset: 10);
        var infos = Advisor().ReadSymbolBars(new[] { "BBB", "AAA", "aaa", " " }, 10, Day(20), Day(60), _root, CancellationToken.None);

        Assert.Equal(new[] { "AAA", "BBB" }, infos.Select(i => i.Symbol));       // a repeated name (any case) and a blank name count once / not at all
        var a = infos[0]; var b = infos[1];
        Assert.Equal((41, 61, Day(51)), (a.BarsInRange, a.BarsUpToEnd, a.RequiredStart!.Value));      // days 20..60; days 0..60 = 61 bars
        Assert.Equal((20, 30, Day(30)), (b.BarsInRange, b.BarsUpToEnd, b.RequiredStart!.Value));      // B holds days 10..39 only: 30 bars, 20 of them from day 20; its 10th bar from the end is day 30
    }

    [Fact]
    public void AnUnreadableOrMissingSymbolIsLeftOut_AndTooManySymbolsReadNothing()
    {
        Write(_root, "AAA", 40);
        RlSourceFixture.CreateParquet(Path.Combine(_root, "BAD.parquet"),
            $"SELECT DATE '{First:yyyy-MM-dd}'+CAST(range AS INTEGER) AS date,100::FLOAT AS open,110::FLOAT AS high,90::FLOAT AS low,100::FLOAT AS close,1000::BIGINT AS volume FROM range(60) ORDER BY range DESC");
        var infos = Advisor().ReadSymbolBars(new[] { "BAD", "MISSING", "AAA" }, 10, null, null, _root, CancellationToken.None);
        Assert.Equal("AAA", Assert.Single(infos).Symbol);

        Assert.Empty(Advisor(maximumSymbols: 2).ReadSymbolBars(new[] { "AAA", "BBB", "CCC" }, 10, null, null, _root, CancellationToken.None));
        Assert.Empty(Advisor().ReadSymbolBars(new[] { "AAA" }, 0, null, null, _root, CancellationToken.None));
    }

    [Fact]
    public async Task TheAsyncMemberReadsTheFolderOfTheTimeframe_AndTheInterfaceDefaultReportsNothing()
    {
        string weekly = Path.Combine(_root, "W");
        Write(weekly, "AAA", 20, step: 7);
        var settings = new MarketDataSettings { WeeklyDataPath = weekly };
        IStoredPeriodAdvisor advisor = Advisor(settings: settings);

        var infos = await advisor.ReadSymbolBarsForTrainingAsync(new[] { "AAA" }, TrainingTimeframe.Weekly, 10, null, null, CancellationToken.None);
        Assert.Equal(20, Assert.Single(infos).BarsInRange);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => advisor.ReadSymbolBarsForTrainingAsync(
            new[] { "AAA" }, TrainingTimeframe.Weekly, 10, null, null, new CancellationToken(canceled: true)));

        IStoredPeriodAdvisor legacy = new LegacyAdvisor();
        Assert.Empty(await legacy.ReadSymbolBarsForTrainingAsync(new[] { "AAA" }, TrainingTimeframe.Daily, 10, null, null, CancellationToken.None));
    }

    /// <summary>An implementer written before the new member: it must still compile and report nothing for it.</summary>
    private sealed class LegacyAdvisor : IStoredPeriodAdvisor
    {
        public Task<StoredPeriodProposal> ProposeForRlAsync(string symbol, int requiredBars, DateOnly? endBoundInclusive, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task<DateOnly?> LatestFinalizedDateAsync(IReadOnlyList<string> symbols, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task<StoredPeriodProposal> ProposeForTrainingAsync(IReadOnlyList<string> symbols, TrainingTimeframe timeframe, int requiredBars,
            DateOnly? endBoundInclusive, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
