#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Models.StoredPeriod;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using StockAnalyzer.Core.Services.StoredPeriod;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// The stored-period proposal: the pure arithmetic, the readers on real temporary Parquet files (finality, the end bound, skipped files, the three timeframe
/// folders) and the proof that the single-symbol RL minimum equals the panel minimum <see cref="RlPanelCatalogBuilder.MinimumCalendarLength"/>.
/// </summary>
public sealed class StoredPeriodTests : IDisposable
{
    private static readonly DateOnly First = RlSourceFixture.FirstDay;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sa_stored_period_" + Guid.NewGuid().ToString("N"));
    private string Daily => Path.Combine(_root, "Daily");
    private string Meta => Path.Combine(_root, "Metadata");

    public StoredPeriodTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static DateOnly Day(int offset) => First.AddDays(offset);
    private static StoredPeriodCalendar Calendar(string symbol, int first, int count)
        => new(symbol, Enumerable.Range(first, count).Select(Day).ToImmutableArray());

    /// <summary>The clock is at noon UTC of the day with the given offset, so that day is the exchange date of a UTC symbol.</summary>
    private static StoredPeriodAdvisor Advisor(int exchangeDayOffset, int maximumSymbols = StoredPeriodLimits.MaximumSymbols, MarketDataSettings? settings = null)
        => new(settings is null ? null : Options.Create(settings), new FixedClock(new DateTimeOffset(Day(exchangeDayOffset).ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero)), maximumSymbols);

    // ----- the pure arithmetic -----

    [Fact]
    public void NoCalendarMeansNoData_AndAnEmptyCalendarIsNotUsable()
    {
        Assert.Equal(StoredPeriodStatus.NoData, StoredPeriodMath.Propose(Array.Empty<StoredPeriodCalendar>(), 10).Status);
        Assert.Equal(StoredPeriodStatus.NoData, StoredPeriodMath.Propose(new[] { new StoredPeriodCalendar("A", ImmutableArray<DateOnly>.Empty) }, 10).Status);
    }

    [Theory]
    [InlineData(9, StoredPeriodStatus.InsufficientHistory)]   // one bar short of N = 10
    [InlineData(10, StoredPeriodStatus.Found)]                // exactly N
    [InlineData(11, StoredPeriodStatus.Found)]                // one more than N
    [InlineData(1, StoredPeriodStatus.InsufficientHistory)]   // a single date
    public void TheStatusFollowsTheBarCountAgainstTheRequirement(int count, StoredPeriodStatus expected)
    {
        var proposal = StoredPeriodMath.Propose(new[] { Calendar("A", 0, count) }, 10);
        Assert.Equal(expected, proposal.Status);
        Assert.Equal(Day(count - 1), proposal.Latest);
        // Exactly N bars lie in [Start, Latest] when the history is long enough; otherwise Start is the first stored date.
        Assert.Equal(count >= 10 ? Day(count - 10) : Day(0), proposal.Start);
        Assert.Equal(10, proposal.RequiredBars);
    }

    [Fact]
    public void LatestIsTheNewestDateOverTheSymbols_AndTheStartIsTheEarliestPerSymbolStart()
    {
        var proposal = StoredPeriodMath.Propose(new[] { Calendar("A", 0, 100), Calendar("B", 20, 50) }, 10);   // A ends at 99, B at 69
        Assert.Equal(StoredPeriodStatus.Found, proposal.Status);
        Assert.Equal(Day(99), proposal.Latest);
        Assert.Equal(Day(60), proposal.Start);                   // B starts at its 10th bar from the end (60), A at 90: the earlier one
        Assert.Empty(proposal.ShortSymbols);
        Assert.Equal(50, proposal.AvailableBars);
    }

    [Fact]
    public void AShortSymbolIsReportedFewestBarsFirstThenByOrdinalName_AndPullsTheStartBack()
    {
        var proposal = StoredPeriodMath.Propose(new[] { Calendar("ZZZ", 0, 5), Calendar("AAA", 40, 5), Calendar("MMM", 0, 50), Calendar("BBB", 30, 5) }, 10);
        Assert.Equal(StoredPeriodStatus.InsufficientHistory, proposal.Status);
        Assert.Equal(new[] { "AAA", "BBB", "ZZZ" }, proposal.ShortSymbols);   // 5 bars each: ordinal order
        Assert.Equal(5, proposal.AvailableBars);
        Assert.Equal(Day(0), proposal.Start);                                  // ZZZ's first date
        Assert.Equal(Day(49), proposal.Latest);
    }

    [Fact]
    public void TheShortestSymbolIsTheFirstShortOne()
    {
        var proposal = StoredPeriodMath.Propose(new[] { Calendar("B", 0, 7), Calendar("A", 0, 3), Calendar("C", 0, 20) }, 10);
        Assert.Equal(new[] { "A", "B" }, proposal.ShortSymbols);
        Assert.Equal(3, proposal.AvailableBars);
    }

    [Fact]
    public void TwoProposalsWithTheSameContentAreEqual_AndADifferentShortListIsNot()
    {
        var first = StoredPeriodMath.Propose(new[] { Calendar("A", 0, 5), Calendar("B", 0, 50) }, 10);
        var second = StoredPeriodMath.Propose(new[] { Calendar("B", 0, 50), Calendar("A", 0, 5) }, 10);
        Assert.Equal(new[] { "A" }, first.ShortSymbols);
        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.NotEqual(first, StoredPeriodMath.Propose(new[] { Calendar("A", 0, 5), Calendar("B", 0, 4) }, 10));
        Assert.Equal(StoredPeriodProposal.None(StoredPeriodStatus.NoData, 10), StoredPeriodProposal.None(StoredPeriodStatus.NoData, 10));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ARequirementThatIsNotPositiveIsNoProposal_EvenWithoutData(int required)
    {
        Assert.Equal(StoredPeriodStatus.NoProposal, StoredPeriodMath.Propose(new[] { Calendar("A", 0, 10) }, required).Status);
        Assert.Equal(StoredPeriodStatus.NoProposal, StoredPeriodMath.Propose(Array.Empty<StoredPeriodCalendar>(), required).Status);   // NoProposal is evaluated before NoData
    }

    [Fact]
    public void ALatestDateWithoutASuccessorIsNoProposal()
    {
        var calendar = new StoredPeriodCalendar("A", ImmutableArray.Create(DateOnly.MaxValue.AddDays(-1), DateOnly.MaxValue));
        Assert.Equal(StoredPeriodStatus.NoProposal, StoredPeriodMath.Propose(new[] { calendar }, 1).Status);
    }

    [Fact]
    public void TheResultDoesNotDependOnTheOrderOfTheInput()
    {
        var calendars = new[] { Calendar("C", 0, 30), Calendar("A", 5, 12), Calendar("B", 0, 8) };
        var expected = StoredPeriodMath.Propose(calendars, 10);
        foreach (var order in new[] { new[] { 0, 1, 2 }, new[] { 2, 1, 0 }, new[] { 1, 2, 0 } })
        {
            var actual = StoredPeriodMath.Propose(order.Select(i => calendars[i]).ToArray(), 10);
            Assert.Equal((expected.Status, expected.Start, expected.Latest, expected.AvailableBars), (actual.Status, actual.Start, actual.Latest, actual.AvailableBars));
            Assert.Equal(expected.ShortSymbols, actual.ShortSymbols);
        }
    }

    // ----- the readers on real Parquet files -----

    private void Write(string symbol, int count, int offset = 0, string quoteType = "EQUITY", bool metadata = true, string currency = "USD", string timezone = "UTC")
        => RlSourceFixture.WriteLocalSymbol(_root, symbol, Day(offset), count, currency, timezone, quoteType, metadata);

    private StoredPeriodProposal Rl(StoredPeriodAdvisor advisor, string symbol, int required, DateOnly? bound = null)
        => advisor.ProposeFinalized(new[] { symbol }, required, bound, Daily, Meta, CancellationToken.None);

    [Fact]
    public void ATrailingBarThatIsNotFinalIsDroppedAndTheSymbolStaysUsable()
    {
        Write("AAA", 100);                                               // days 0..99
        var proposal = Rl(Advisor(exchangeDayOffset: 99), "AAA", 10);    // the exchange date is day 99: that bar is not final yet
        Assert.Equal(StoredPeriodStatus.Found, proposal.Status);
        Assert.Equal(Day(98), proposal.Latest);
        Assert.Equal(Day(89), proposal.Start);                           // exactly 10 final bars: 89..98
        Assert.Equal(Day(99), Rl(Advisor(exchangeDayOffset: 100), "AAA", 10).Latest);   // a day later the bar is final
    }

    [Fact]
    public void ATypedEndBoundCountsTheBarsBackFromTheBound()
    {
        Write("AAA", 100);
        var advisor = Advisor(exchangeDayOffset: 200);
        var proposal = Rl(advisor, "AAA", 10, bound: Day(49));
        Assert.Equal((Day(49), Day(40)), (proposal.Latest!.Value, proposal.Start!.Value));
        Assert.Equal(StoredPeriodStatus.NoData, Rl(advisor, "AAA", 10, bound: Day(-1)).Status);   // a bound before all data
    }

    [Fact]
    public void ASymbolWithoutMetadataOrWithAnotherQuoteTypeOrWithoutAFileIsSkipped()
    {
        Write("AAA", 50);
        Write("NOMETA", 80, metadata: false);
        Write("ETF1", 90, quoteType: "ETF");
        var advisor = Advisor(exchangeDayOffset: 200);
        Assert.Equal(StoredPeriodStatus.NoData, Rl(advisor, "NOMETA", 10).Status);
        Assert.Equal(StoredPeriodStatus.NoData, Rl(advisor, "ETF1", 10).Status);
        Assert.Equal(StoredPeriodStatus.NoData, Rl(advisor, "MISSING", 10).Status);
        var several = advisor.ProposeFinalized(new[] { "NOMETA", "ETF1", "AAA", "MISSING" }, 10, null, Daily, Meta, CancellationToken.None);
        Assert.Equal((StoredPeriodStatus.Found, Day(49)), (several.Status, several.Latest!.Value));
    }

    [Fact]
    public void AFileWhoseDatesAreNotIncreasingIsSkippedNotAnError()
    {
        Write("AAA", 40);
        Directory.CreateDirectory(Daily); Directory.CreateDirectory(Meta);
        RlSourceFixture.CreateParquet(Path.Combine(Daily, "BAD.parquet"),
            $"SELECT DATE '{First:yyyy-MM-dd}'+CAST(range AS INTEGER) AS date,100::FLOAT AS open,110::FLOAT AS high,90::FLOAT AS low,100::FLOAT AS close,1000::BIGINT AS volume FROM range(60) ORDER BY range DESC");
        RlSourceFixture.CreateParquet(Path.Combine(Meta, "BAD.meta.parquet"), "SELECT 'USD' AS currency,'EQUITY' AS quote_type,'UTC' AS exchange_timezone_name");
        var proposal = Advisor(200).ProposeFinalized(new[] { "BAD", "AAA" }, 10, null, Daily, Meta, CancellationToken.None);
        Assert.Equal(Day(39), proposal.Latest);                          // only AAA contributed
    }

    [Fact]
    public void ASymbolThatStoppedTradingEarlyPullsTheStartBackWhileLatestComesFromTheNewestOne()
    {
        Write("OLD", 60);                                                // days 0..59
        Write("NEW", 100, offset: 30);                                   // days 30..129
        var proposal = Advisor(500).ProposeFinalized(new[] { "OLD", "NEW" }, 20, null, Daily, Meta, CancellationToken.None);
        Assert.Equal((Day(129), Day(40)), (proposal.Latest!.Value, proposal.Start!.Value));   // OLD starts at 40, NEW at 110
    }

    [Fact]
    public void MoreSymbolsThanTheCapAreTooManyAndNothingIsRead()
    {
        var advisor = Advisor(200, maximumSymbols: 2);
        var proposal = advisor.ProposeFinalized(new[] { "A", "B", "C" }, 10, null, Path.Combine(_root, "does-not-exist"), Path.Combine(_root, "none"), CancellationToken.None);
        Assert.Equal(StoredPeriodStatus.TooMany, proposal.Status);
        Assert.Equal(StoredPeriodStatus.TooMany, advisor.ProposeStored(new[] { "A", "B", "C" }, 0, null, _root, CancellationToken.None).Status);   // the cap wins over an invalid requirement
        Assert.Equal(StoredPeriodStatus.NoProposal, advisor.ProposeStored(new[] { "A" }, 0, null, _root, CancellationToken.None).Status);
    }

    [Fact]
    public void TheCapIsValidatedAgainstTheHardBand()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Advisor(1, maximumSymbols: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Advisor(1, maximumSymbols: (int)StoredPeriodLimits.MaximumSymbolsBand.HardMax + 1));
        Assert.True(StoredPeriodLimits.MaximumSymbolsBand.IsWithinStandard(StoredPeriodLimits.MaximumSymbols));
    }

    [Fact]
    public async Task TheTrainingFamilyReadsTheConfiguredFolderOfEachTimeframeWithoutAFinalityRule()
    {
        foreach (var (timeframe, folderName, count) in new[] { (TrainingTimeframe.Daily, "D", 30), (TrainingTimeframe.Weekly, "W", 20), (TrainingTimeframe.Monthly, "M", 12) })
        {
            string folder = Path.Combine(_root, folderName);
            Directory.CreateDirectory(folder);
            RlSourceFixture.CreateParquet(Path.Combine(folder, "AAA.parquet"),
                $"SELECT DATE '{First:yyyy-MM-dd}'+7*CAST(range AS INTEGER) AS date,100::FLOAT AS open,110::FLOAT AS high,90::FLOAT AS low,100::FLOAT AS close,1000::BIGINT AS volume FROM range({count})");
        }
        var settings = new MarketDataSettings { DailyDataPath = Path.Combine(_root, "D"), WeeklyDataPath = Path.Combine(_root, "W"), MonthlyDataPath = Path.Combine(_root, "M") };
        var advisor = Advisor(exchangeDayOffset: -1000, settings: settings);   // a clock before every bar: the finality rule would drop everything, the wizard family has none
        var daily = await advisor.ProposeForTrainingAsync(new[] { "AAA" }, TrainingTimeframe.Daily, 10, null, CancellationToken.None);
        var weekly = await advisor.ProposeForTrainingAsync(new[] { "AAA" }, TrainingTimeframe.Weekly, 10, null, CancellationToken.None);
        var monthly = await advisor.ProposeForTrainingAsync(new[] { "AAA" }, TrainingTimeframe.Monthly, 12, null, CancellationToken.None);
        Assert.Equal((StoredPeriodStatus.Found, Day(7 * 29)), (daily.Status, daily.Latest!.Value));
        Assert.Equal((StoredPeriodStatus.Found, Day(7 * 19)), (weekly.Status, weekly.Latest!.Value));
        Assert.Equal((StoredPeriodStatus.Found, Day(0)), (monthly.Status, monthly.Start!.Value));        // exactly N = 12 bars: the whole file
        var bounded = await advisor.ProposeForTrainingAsync(new[] { "AAA" }, TrainingTimeframe.Daily, 10, Day(7 * 14), CancellationToken.None);
        Assert.Equal((Day(7 * 14), Day(7 * 5)), (bounded.Latest!.Value, bounded.Start!.Value));
    }

    [Fact]
    public async Task TheCancelledTrainingProposalThrowsOperationCancelled()
    {
        Write("AAA", 100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Advisor(exchangeDayOffset: 99)
            .ProposeForTrainingAsync(new[] { "AAA" }, TrainingTimeframe.Daily, 5, null, new CancellationToken(true)));
    }

    // ----- the Portfolio RL end date and the single-symbol RL proposal, through the folder overloads -----

    [Fact]
    public async Task LatestFinalizedDateIsTheNewestFinalDateOverTheUsableSymbols()
    {
        Write("AAA", 100);                                   // days 0..99
        Write("BBB", 50, offset: 10);                        // days 10..59
        Write("ETF1", 120, quoteType: "ETF");                // not an equity: never counted
        var advisor = Advisor(exchangeDayOffset: 99);        // day 99 is the exchange date: not final yet
        Assert.Equal(Day(98), await advisor.LatestFinalizedDateAsync(new[] { "BBB", "ETF1", "AAA", "MISSING" }, Daily, Meta, CancellationToken.None));
        Assert.Equal(Day(59), await advisor.LatestFinalizedDateAsync(new[] { "BBB" }, Daily, Meta, CancellationToken.None));
        Assert.Equal(Day(99), await Advisor(exchangeDayOffset: 100).LatestFinalizedDateAsync(new[] { "AAA" }, Daily, Meta, CancellationToken.None));
    }

    [Fact]
    public async Task LatestFinalizedDateIsNullWithoutAUsableSymbolAboveTheCapAndWhenNothingIsFinal()
    {
        Write("AAA", 10);
        Write("ETF1", 10, quoteType: "ETF");
        Write("NOMETA", 10, metadata: false);
        var advisor = Advisor(exchangeDayOffset: 200);
        Assert.Null(await advisor.LatestFinalizedDateAsync(new[] { "ETF1", "NOMETA", "MISSING" }, Daily, Meta, CancellationToken.None));
        Assert.Null(await advisor.LatestFinalizedDateAsync(Array.Empty<string>(), Daily, Meta, CancellationToken.None));
        Assert.Null(await Advisor(200, maximumSymbols: 1).LatestFinalizedDateAsync(new[] { "AAA", "BBB" }, Daily, Meta, CancellationToken.None));   // above the cap: nothing is read
        Assert.Null(await Advisor(exchangeDayOffset: 0).LatestFinalizedDateAsync(new[] { "AAA" }, Daily, Meta, CancellationToken.None));          // every bar is on or after the exchange date
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => advisor.LatestFinalizedDateAsync(new[] { "AAA" }, Daily, Meta, new CancellationToken(true)));
    }

    [Fact]
    public async Task TheSingleSymbolRlProposalIsFoundShortOrEmptyAndHonoursTheEndBound()
    {
        Write("AAA", 100);
        var advisor = Advisor(exchangeDayOffset: 99);
        var found = await advisor.ProposeForRlAsync("AAA", 10, null, Daily, Meta, CancellationToken.None);
        Assert.Equal((StoredPeriodStatus.Found, Day(89), Day(98)), (found.Status, found.Start!.Value, found.Latest!.Value));
        var bounded = await advisor.ProposeForRlAsync("AAA", 10, Day(49), Daily, Meta, CancellationToken.None);
        Assert.Equal((Day(40), Day(49)), (bounded.Start!.Value, bounded.Latest!.Value));
        var shortHistory = await advisor.ProposeForRlAsync("AAA", 500, null, Daily, Meta, CancellationToken.None);
        Assert.Equal(StoredPeriodStatus.InsufficientHistory, shortHistory.Status);
        Assert.Equal((500, 99), (shortHistory.RequiredBars, shortHistory.AvailableBars));   // 99 final bars: the 100th is not final
        Assert.Equal(new[] { "AAA" }, shortHistory.ShortSymbols);
        Assert.Equal(StoredPeriodStatus.NoData, (await advisor.ProposeForRlAsync("MISSING", 10, null, Daily, Meta, CancellationToken.None)).Status);
        Assert.Equal(StoredPeriodStatus.NoProposal, (await advisor.ProposeForRlAsync("AAA", 0, null, Daily, Meta, CancellationToken.None)).Status);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => advisor.ProposeForRlAsync("AAA", 10, null, Daily, Meta, new CancellationToken(true)));
    }

    // ----- the equity classification equals the panel advisor's (the one place that copies its rule) -----

    private void WriteMetadata(string symbol, string select)
    {
        Directory.CreateDirectory(Meta);
        RlSourceFixture.CreateParquet(Path.Combine(Meta, symbol + ".meta.parquet"), select);
    }

    [Fact]
    public void TheEquityClassificationKeepsTheSameSymbolsAsThePanelPeriodAdvisor()
    {
        const string Control = "A_CONTROL";
        Write(Control, 60);
        Write("EQ1", 60);
        Write("EQ2", 40, offset: 5);
        Write("ETF1", 60, quoteType: "ETF");
        Write("NOMETA", 60, metadata: false);
        Write("NOTYPE", 60, metadata: false);
        WriteMetadata("NOTYPE", "SELECT 'USD' AS currency,'UTC' AS exchange_timezone_name");
        Write("BLANKTZ", 60, metadata: false);
        WriteMetadata("BLANKTZ", "SELECT 'USD' AS currency,'EQUITY' AS quote_type,'  ' AS exchange_timezone_name");
        Write("NOZONE", 60, timezone: "Not/AZone");
        Directory.CreateDirectory(Daily);
        RlSourceFixture.CreateParquet(Path.Combine(Daily, "BADDATES.parquet"),
            $"SELECT DATE '{First:yyyy-MM-dd}'+CAST(range AS INTEGER) AS date,100::FLOAT AS open,110::FLOAT AS high,90::FLOAT AS low,100::FLOAT AS close,1000::BIGINT AS volume FROM range(60) ORDER BY range DESC");
        WriteMetadata("BADDATES", "SELECT 'USD' AS currency,'EQUITY' AS quote_type,'UTC' AS exchange_timezone_name");
        Write("CORRUPT", 1);
        File.WriteAllBytes(Path.Combine(Daily, "CORRUPT.parquet"), new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        var symbols = new[] { "EQ1", "EQ2", "ETF1", "NOMETA", "NOTYPE", "BLANKTZ", "NOZONE", "BADDATES", "CORRUPT", "MISSING" };

        var advisor = Advisor(exchangeDayOffset: 5000);
        var ours = symbols.Where(s => advisor.ProposeFinalized(new[] { s }, 10, null, Daily, Meta, CancellationToken.None).Status != StoredPeriodStatus.NoData).ToArray();
        // The panel advisor needs two usable symbols; pairing a symbol with the control shows whether it keeps that symbol.
        var panel = symbols.Where(s => RlPanelPeriodAdvisor.Suggest(new[] { Control, s }, DateOnly.MaxValue, RlPanelEvaluationMode.Standard, RlSourceFixture.Limits,
            Daily, Meta, CancellationToken.None).Status != RlStartSuggestionStatus.NoUsableData).ToArray();
        Assert.Equal(new[] { "EQ1", "EQ2" }, panel);
        Assert.Equal(panel, ours);
    }

    [Fact]
    public void TheTwoIntendedDifferencesFromThePanelAdvisorArePinned()
    {
        const string Control = "A_CONTROL";
        Write(Control, 60);
        // (1) A symbol of another currency: the panel advisor drops it (one currency per universe); a date proposal still counts its dates.
        Write("EURO", 60, currency: "EUR");
        // (2) A symbol whose newest bar is not final: the panel advisor drops the whole symbol; the proposal drops that bar and keeps the symbol.
        var future = new DateOnly(2100, 1, 1);
        RlSourceFixture.WriteLocalSymbol(_root, "FRESH", future, 10);
        var advisor = Advisor(exchangeDayOffset: future.AddDays(4).DayNumber - First.DayNumber);   // the exchange date is 2100-01-05
        foreach (string symbol in new[] { "EURO", "FRESH" })
        {
            var panel = RlPanelPeriodAdvisor.Suggest(new[] { Control, symbol }, DateOnly.MaxValue, RlPanelEvaluationMode.Standard, RlSourceFixture.Limits, Daily, Meta, CancellationToken.None);
            Assert.Equal(RlStartSuggestionStatus.NoUsableData, panel.Status);
            Assert.NotEqual(StoredPeriodStatus.NoData, advisor.ProposeFinalized(new[] { symbol }, 3, null, Daily, Meta, CancellationToken.None).Status);
        }
        var fresh = advisor.ProposeFinalized(new[] { "FRESH" }, 3, null, Daily, Meta, CancellationToken.None);
        Assert.Equal((StoredPeriodStatus.Found, future.AddDays(1), future.AddDays(3)), (fresh.Status, fresh.Start!.Value, fresh.Latest!.Value));   // bars 2100-01-01..04 are final
    }

    // ----- one connection for many files -----

    [Fact]
    public void OneSessionReadsManyFilesAndStaysUsableAfterAFileThatFails()
    {
        Write("AAA", 30);
        Write("BBB", 20, offset: 5);
        Directory.CreateDirectory(Daily);
        string bad = Path.Combine(Daily, "BAD.parquet"), corrupt = Path.Combine(Daily, "CORRUPT.parquet");
        RlSourceFixture.CreateParquet(bad,
            $"SELECT DATE '{First:yyyy-MM-dd}'+CAST(range AS INTEGER) AS date,100::FLOAT AS open,110::FLOAT AS high,90::FLOAT AS low,100::FLOAT AS close,1000::BIGINT AS volume FROM range(10) ORDER BY range DESC");
        File.WriteAllBytes(corrupt, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        string aaa = Path.Combine(Daily, "AAA.parquet"), bbb = Path.Combine(Daily, "BBB.parquet");

        using var session = new RlLocalParquetSession(RlSourceFixture.Limits);
        Assert.Equal(30, session.ReadDates(aaa, DateOnly.MaxValue, CancellationToken.None).Length);
        Assert.Throws<InvalidDataException>(() => session.ReadDates(bad, DateOnly.MaxValue, CancellationToken.None));
        Assert.ThrowsAny<Exception>(() => session.ReadDates(corrupt, DateOnly.MaxValue, CancellationToken.None));
        Assert.Throws<OperationCanceledException>(() => session.ReadDates(aaa, DateOnly.MaxValue, new CancellationToken(true)));
        var again = session.ReadDates(bbb, Day(15), CancellationToken.None);
        Assert.Equal((10, Day(5), Day(14)), (again.Length, again[0], again[^1]));
        Assert.Equal(RlLocalParquetReader.ReadDates(aaa, DateOnly.MaxValue, RlSourceFixture.Limits, CancellationToken.None), session.ReadDates(aaa, DateOnly.MaxValue, CancellationToken.None));
        string meta = Path.Combine(Meta, "AAA.meta.parquet");
        Assert.Equal(RlLocalParquetReader.ReadMetadata(meta), session.ReadMetadata(meta));
    }

    [Fact]
    public void ReadDatesAcceptsTheLargestDateAsItsBound()
    {
        Write("AAA", 25);
        var dates = RlLocalParquetReader.ReadDates(Path.Combine(Daily, "AAA.parquet"), DateOnly.MaxValue, RlSourceFixture.Limits, CancellationToken.None);
        Assert.Equal(25, dates.Length);
        Assert.Equal(Day(24), dates[^1]);
    }

    // ----- the single-symbol RL minimum equals the panel minimum -----

    [Fact]
    public void TheSingleSymbolCatalogIsFeasibleExactlyFromTheStandardPanelMinimum_AndForEveryLargerLength()
    {
        int minimum = RlPanelCatalogBuilder.MinimumCalendarLength(RlPanelEvaluationMode.Standard);
        for (int n = minimum - 3; n <= minimum + 2 * RlContract.SupportLength; n++)
        {
            var bars = RlSourceFixture.Bars(First, n);
            bool feasible = Feasible(() => RlSourceSnapshot.BuildCatalog(bars, ImmutableArray<RlCorporateAction>.Empty, ImmutableArray<DateOnly>.Empty, smoke: false));
            Assert.True(feasible == (n >= minimum), $"standard: length {n} (minimum {minimum}) feasible={feasible}");
        }
    }

    [Fact]
    public void TheSingleSymbolShortHistoryCatalogIsFeasibleExactlyFromTheShortHistoryPanelMinimum_AndForEveryLargerLength()
    {
        int minimum = RlPanelCatalogBuilder.MinimumCalendarLength(RlPanelEvaluationMode.ShortHistory);
        for (int n = minimum - 3; n <= minimum + 2 * RlContract.SupportLength; n++)
        {
            var bars = RlSourceFixture.Bars(First, n);
            bool feasible = Feasible(() => RlSourceSnapshot.BuildShortHistoryCatalog(bars, ImmutableArray<RlCorporateAction>.Empty, ImmutableArray<DateOnly>.Empty, smoke: false));
            Assert.True(feasible == (n >= minimum), $"short history: length {n} (minimum {minimum}) feasible={feasible}");
        }
    }

    private static bool Feasible(Func<RlEpisodeCatalog> build)
    {
        try { _ = build(); return true; }
        catch (InvalidDataException) { return false; }
    }
}
