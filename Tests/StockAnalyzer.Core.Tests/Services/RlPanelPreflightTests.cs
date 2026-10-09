using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// Panel preflight on real local Daily/Metadata Parquet files (isolated temp folders, read in place) and the pure Start gate. The bars are
/// bounded integrity fixtures: flat prices on consecutive calendar days from 2000-01-01, so the expected counts follow from the day arithmetic alone.
/// </summary>
public sealed class RlPanelPreflightTests : IDisposable
{
    private const int Days = 1_400;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sa_rl_preflight_" + Guid.NewGuid().ToString("N"));
    private string Daily => Path.Combine(_root, "Daily");
    private string Meta => Path.Combine(_root, "Metadata");
    private static readonly DateOnly First = RlSourceFixture.FirstDay;

    public RlPanelPreflightTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private PanelPreflight Run(string[] symbols, DateOnly? start = null, DateOnly? end = null, bool holdout = true,
        RlPanelEvaluationMode mode = RlPanelEvaluationMode.ShortHistory, CancellationToken ct = default)
        => RlPanelPreflightEvaluator.Evaluate(symbols, start ?? First, end ?? First.AddDays(Days), holdout, mode, new RlPortfolioSettings(),
            RlSourceFixture.Limits, Daily, Meta, ct);

    private void Write(string symbol, int count = Days, int offset = 0, string currency = "USD", string timezone = "UTC", string quoteType = "EQUITY", bool metadata = true)
        => RlSourceFixture.WriteLocalSymbol(_root, symbol, First.AddDays(offset), count, currency, timezone, quoteType, metadata);

    private void WriteFour() { foreach (string s in new[] { "AAA", "BBB", "CCC", "DDD" }) Write(s); }

    [Fact]
    public void AReadyPanelShowsRequiredAndActualBarsAndBlocks()
    {
        WriteFour();
        var result = Run(new[] { "DDD", "AAA", "CCC", "BBB" });
        Assert.Equal(new[] { "AAA", "BBB", "CCC", "DDD" }, result.Symbols.Select(s => s.Symbol).ToArray());
        Assert.All(result.Symbols, s => Assert.Equal((RlPanelSymbolStatus.Ready, Days, RlContract.SupportLength), (s.Status, s.BarsInPeriod, s.RequiredBarsPerBlock)));
        Assert.Equal(4, result.ReadyCount);
        Assert.Equal(Days, result.CalendarLength);
        Assert.Equal(RlPanelCatalogBuilder.MinimumCalendarLength(RlPanelEvaluationMode.ShortHistory), result.RequiredCalendarLength);
        Assert.Equal(1278, result.RequiredCalendarLength);
        Assert.True(result.Feasible);
        Assert.False(result.HoldoutSplitRisk);
        // T = 1400: cut1 840, cut2 1120. Train blocks 0, 192, 384, 576; validation block 904..1096; OOS block 1184..1376.
        Assert.Equal(new[] { ("train", 4, 1), ("validation", 1, 1), ("oos", 1, 1) },
            result.Cells.Select(c => (c.Partition, c.BlockCount, c.RequiredBlocks)).ToArray());
    }

    [Fact]
    public void TheStandardModeNeedsALongerCalendarAndIsInfeasibleForFourteenHundredBars()
    {
        WriteFour();
        var result = Run(new[] { "AAA", "BBB", "CCC", "DDD" }, mode: RlPanelEvaluationMode.Standard);
        Assert.False(result.Feasible);
        Assert.Equal(5118, result.RequiredCalendarLength);
        Assert.Equal(Days, result.CalendarLength);
        Assert.Contains(result.Cells, c => c.Partition == "validation" && c.BlockCount == 1 && c.RequiredBlocks == RlContract.EvaluationEpisodeCount);
        Assert.Equal(4, result.ReadyCount);          // the symbols are fine; the panel is too short
    }

    [Fact]
    public void ThePeriodRestrictsTheBarsAndNothingIsAdjusted()
    {
        WriteFour();
        var result = Run(new[] { "AAA", "BBB" }, First.AddDays(100), First.AddDays(400));
        Assert.All(result.Symbols, s => Assert.Equal(300, s.BarsInPeriod));
        Assert.Equal(300, result.CalendarLength);
        Assert.False(result.Feasible);
        Assert.Equal(First.AddDays(100), result.Start);
        Assert.Equal(First.AddDays(400), result.EndExclusive);
        // A one-day period is a valid input: it simply has one bar and is infeasible.
        var oneDay = Run(new[] { "AAA", "BBB" }, First.AddDays(5), First.AddDays(6));
        Assert.All(oneDay.Symbols, s => Assert.Equal((RlPanelSymbolStatus.Ready, 1), (s.Status, s.BarsInPeriod)));
        Assert.False(oneDay.Feasible);
        Assert.Throws<ArgumentException>(() => Run(new[] { "AAA" }, First.AddDays(5), First.AddDays(5)));
    }

    [Fact]
    public void EverySymbolGetsExactlyOneStatusWithItsReason()
    {
        Write("AAA");
        Write("CCC", metadata: false);
        Write("DDD", quoteType: "ETF");
        Write("EEE", currency: "EUR");
        Write("FFF", timezone: "America/New_York");
        Write("GGG", offset: 4_000);                                         // no bar inside the period
        Write("HHH");                                                         // then corrupt its Daily file
        File.WriteAllBytes(Path.Combine(Daily, "HHH.parquet"), new byte[] { 1, 2, 3, 4 });
        var result = Run(new[] { "AAA", "BBB", "CCC", "DDD", "EEE", "FFF", "GGG", "HHH", "A/B" });
        var byName = result.Symbols.ToDictionary(s => s.Symbol);
        Assert.Equal(RlPanelSymbolStatus.Ready, byName["AAA"].Status);
        Assert.Equal(RlPanelSymbolStatus.MissingFile, byName["BBB"].Status);       // no Daily file
        Assert.Equal(RlPanelSymbolStatus.MissingFile, byName["CCC"].Status);       // no Metadata file
        Assert.Equal((RlPanelSymbolStatus.NotEquity, "ETF"), (byName["DDD"].Status, byName["DDD"].Detail));
        Assert.Equal((RlPanelSymbolStatus.CurrencyMismatch, "EUR"), (byName["EEE"].Status, byName["EEE"].Detail));
        Assert.Equal((RlPanelSymbolStatus.TimezoneMismatch, "America/New_York"), (byName["FFF"].Status, byName["FFF"].Detail));
        Assert.Equal((RlPanelSymbolStatus.NoValidBlock, 0), (byName["GGG"].Status, byName["GGG"].BarsInPeriod));
        Assert.Equal(RlPanelSymbolStatus.ReadError, byName["HHH"].Status);
        Assert.Equal(RlPanelSymbolStatus.ReadError, byName["A/B"].Status);
        Assert.Equal(9, result.Symbols.Length);
        Assert.Equal(1, result.ReadyCount);
        Assert.False(result.Feasible);
        Assert.Equal(Days, result.CalendarLength);
    }

    [Fact]
    public void ABarOnTheCurrentOrFutureExchangeDateIsAReadError()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        RlSourceFixture.WriteLocalSymbol(_root, "AAA", today.AddDays(-5), 20);
        var result = Run(new[] { "AAA" }, today.AddDays(-10), today.AddDays(30));
        var symbol = Assert.Single(result.Symbols);
        Assert.Equal(RlPanelSymbolStatus.ReadError, symbol.Status);
        Assert.Contains("future", symbol.Detail);
    }

    [Fact]
    public void ASymbolWithoutACompleteBlockIsNoValidBlockAndLeavesThePanelNumbersOfTheReadySet()
    {
        foreach (string s in new[] { "AAA", "BBB", "CCC" }) Write(s);
        Write("SHORT", count: 100, offset: 500);                     // 100 bars: shorter than one block of 192
        var result = Run(new[] { "AAA", "BBB", "CCC", "SHORT" });
        var shortOne = result.Symbols.Single(s => s.Symbol == "SHORT");
        Assert.Equal((RlPanelSymbolStatus.NoValidBlock, 100), (shortOne.Status, shortOne.BarsInPeriod));
        Assert.Equal(3, result.ReadyCount);
        Assert.Equal(Days, result.CalendarLength);
        Assert.True(result.Feasible);
    }

    /// <summary>A symbol whose bars are exactly the days at the given offsets from the first day (the rest of the fixture writes consecutive days).</summary>
    private void WriteOffsets(string symbol, string offsetsSql)
    {
        Directory.CreateDirectory(Daily); Directory.CreateDirectory(Meta);
        RlSourceFixture.CreateParquet(Path.Combine(Daily, symbol + ".parquet"),
            $"SELECT DATE '{First:yyyy-MM-dd}'+CAST(i AS INTEGER) AS date,100::FLOAT AS open,110::FLOAT AS high,90::FLOAT AS low,100::FLOAT AS close,1000::BIGINT AS volume FROM range(1400) t(i) WHERE {offsetsSql} ORDER BY i");
        RlSourceFixture.CreateParquet(Path.Combine(Meta, symbol + ".meta.parquet"), "SELECT 'USD' AS currency,'EQUITY' AS quote_type,'UTC' AS exchange_timezone_name");
    }

    /// <summary>
    /// W9: removing a symbol that is in no block can remove calendar dates only that symbol had, which moves every later index; a symbol that sat in a block
    /// of the first calendar may then sit in none. The exclusion must repeat until no Ready symbol is left without a block, so no Ready symbol has the role None.
    /// Here AAA and BBB miss day 100, XXX has day 100 and nine early days (in no block), YYY has exactly the days 844..1035 (one validation block of the
    /// 1,301-day union calendar, which no longer lines up once day 100 is gone).
    /// </summary>
    [Fact]
    public void AnExcludedSymbolThatShiftsTheCalendarCannotLeaveAReadySymbolWithoutABlock()
    {
        WriteOffsets("AAA", "i < 1301 AND i <> 100");
        WriteOffsets("BBB", "i < 1301 AND i <> 100");
        WriteOffsets("XXX", "i = 100 OR (i >= 1 AND i <= 9)");
        WriteOffsets("YYY", "i >= 844 AND i < 1036");
        var result = Run(new[] { "AAA", "BBB", "XXX", "YYY" }, end: First.AddDays(1_301), holdout: false);
        var byName = result.Symbols.ToDictionary(s => s.Symbol);
        Assert.True(byName["XXX"].Status == RlPanelSymbolStatus.NoValidBlock, byName["XXX"].Status + ": " + byName["XXX"].Detail);
        Assert.Equal(RlPanelSymbolStatus.NoValidBlock, byName["YYY"].Status);                 // it was in a block of the first calendar and is in none of the final one
        Assert.Equal(new[] { RlPanelSymbolStatus.Ready, RlPanelSymbolStatus.Ready }, new[] { byName["AAA"].Status, byName["BBB"].Status });
        Assert.All(result.Symbols.Where(s => s.Status == RlPanelSymbolStatus.Ready), s => Assert.NotEqual(RlPanelSymbolRole.None, s.Role));
        Assert.Equal(2, result.ReadyCount);
        Assert.Equal(1_300, result.CalendarLength);                                          // the calendar of the symbols that remain
        Assert.True(result.Feasible);
    }

    [Fact]
    public void TheHoldoutSplitRiskIsRaisedWhenTrainingBlocksDependOnSymbolsThatMayBeHeldOut()
    {
        Write("AAA"); Write("BBB"); Write("CCC", count: 800, offset: 600); Write("DDD", count: 800, offset: 600);
        var names = new[] { "AAA", "BBB", "CCC", "DDD" };
        // Train blocks (0..768) are complete for AAA and BBB only; a holdout of two symbols may take both and leave no training pair.
        var risky = Run(names);
        Assert.True(risky.Feasible);
        Assert.True(risky.HoldoutSplitRisk);
        Assert.False(Run(names, holdout: false).HoldoutSplitRisk);
    }

    [Fact]
    public void ThePreflightReadsInPlaceAndWritesNothing()
    {
        WriteFour();
        string before = RlSourceFixture.TreeDigest(_root);
        _ = Run(new[] { "AAA", "BBB", "CCC", "DDD" });
        Assert.Equal(before, RlSourceFixture.TreeDigest(_root));
    }

    [Fact]
    public void ItIsCancelableAndTrimsAndDeduplicatesTheSelection()
    {
        WriteFour();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => Run(new[] { "AAA" }, ct: cancelled.Token));
        var result = Run(new[] { " AAA ", "AAA", "", "BBB" });
        Assert.Equal(new[] { "AAA", "BBB" }, result.Symbols.Select(s => s.Symbol).ToArray());
        var empty = Run(Array.Empty<string>());
        Assert.True(empty.Symbols.IsEmpty);
        Assert.Equal((0, 0, false), (empty.ReadyCount, empty.CalendarLength, empty.Feasible));
    }

    // ----- Start gate (pure) -----

    private static PanelPreflight Pre(bool feasible, params RlPanelSymbolStatus[] statuses)
        => new(First, First.AddDays(Days), RlPanelEvaluationMode.ShortHistory,
            statuses.Select((s, i) => new RlPanelSymbolPreflight("S" + i, s, Days, RlContract.SupportLength, "")).ToImmutableArray(),
            Days, 1278, ImmutableArray<RlPanelCellCount>.Empty, feasible, false);

    private const RlPanelSymbolStatus Ok = RlPanelSymbolStatus.Ready, Bad = RlPanelSymbolStatus.MissingFile;
    private static RlPanelStartDecision Gate(PanelPreflight preflight, bool exclude = false, bool holdout = true, RlPortfolioSettings? settings = null)
        => RlPanelStartGate.Decide(preflight, exclude, holdout, settings ?? new RlPortfolioSettings());

    [Fact]
    public void TheGateBlocksEmptyAndSingleSymbolSelectionsWithoutSwitchingMode()
    {
        Assert.Equal(RlPanelStartBlocker.NoSymbols, Gate(Pre(true)).Blocker);
        Assert.Equal(RlPanelStartBlocker.SingleSymbol, Gate(Pre(true, Ok)).Blocker);
        Assert.Equal(RlPanelStartBlocker.SingleSymbol, Gate(Pre(true, Ok), exclude: true).Blocker);
        Assert.Equal(RlPanelStartBlocker.SingleSymbol, Gate(Pre(true, Ok, Bad), exclude: true).Blocker);     // one Ready symbol after exclusion
        Assert.Equal(RlPanelStartBlocker.TooFewSymbols, Gate(Pre(true, Bad, Bad), exclude: true).Blocker);
    }

    [Fact]
    public void StartNeedsAllReadyOrAConfirmedExclusionAndAFeasiblePanel()
    {
        Assert.True(Gate(Pre(true, Ok, Ok)).CanStart);
        var partial = Pre(true, Ok, Ok, Bad);
        var blocked = Gate(partial);
        Assert.Equal((false, RlPanelStartBlocker.NotAllReady), (blocked.CanStart, blocked.Blocker));
        var confirmed = Gate(partial, exclude: true);
        Assert.True(confirmed.CanStart);
        Assert.Equal(new[] { "S0", "S1" }, confirmed.TrainingSymbols.ToArray());
        Assert.Equal(new[] { "S2" }, confirmed.ExcludedSymbols.ToArray());
        Assert.Equal(RlPanelStartBlocker.Infeasible, Gate(Pre(false, Ok, Ok)).Blocker);
        Assert.Equal(RlPanelStartBlocker.Infeasible, Gate(Pre(false, Ok, Ok, Bad), exclude: true).Blocker);
    }

    [Fact]
    public void TheGateNeverTruncatesAboveTheConfiguredMaximum()
    {
        var settings = new RlPortfolioSettings { MaxSymbols = 3 };
        Assert.Equal(RlPanelStartBlocker.TooManySymbols, Gate(Pre(true, Ok, Ok, Ok, Ok), settings: settings).Blocker);
        Assert.Equal(RlPanelStartBlocker.TooManySymbols, Gate(Pre(true, Ok, Ok, Ok, Ok, Bad), exclude: true, settings: settings).Blocker);
        Assert.True(Gate(Pre(true, Ok, Ok, Ok), settings: settings).CanStart);
    }

    [Fact]
    public void TheSymbolHoldoutIsOnlyOfferedFromFourSymbolsAndSaysSoOtherwise()
    {
        var three = Gate(Pre(true, Ok, Ok, Ok));
        Assert.Equal((true, false, true), (three.CanStart, three.SymbolHoldout, three.HoldoutUnavailable));
        var four = Gate(Pre(true, Ok, Ok, Ok, Ok));
        Assert.Equal((true, false), (four.SymbolHoldout, four.HoldoutUnavailable));
        var declined = Gate(Pre(true, Ok, Ok, Ok, Ok), holdout: false);
        Assert.Equal((false, false), (declined.SymbolHoldout, declined.HoldoutUnavailable));
        // Half of five symbols held out rounds up to three, leaving two to train on: still allowed; a larger share is rejected by the settings band.
        var half = Gate(Pre(true, Ok, Ok, Ok, Ok, Ok), settings: new RlPortfolioSettings { HoldoutPercent = 50 });
        Assert.True(half.SymbolHoldout);
        // After exclusion the count that matters is the Ready count.
        var shrunk = Gate(Pre(true, Ok, Ok, Ok, Bad, Bad), exclude: true);
        Assert.Equal((true, false, true), (shrunk.CanStart, shrunk.SymbolHoldout, shrunk.HoldoutUnavailable));
    }
}
