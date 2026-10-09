using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// The start-date advisor on real local Daily/Metadata Parquet files (isolated temp folders, read in place). The bars are bounded integrity fixtures:
/// flat prices on consecutive calendar days from 2000-01-01, so every expected start follows from the day arithmetic and the pinned catalog minimum
/// (1,278 short-history, 5,118 standard) alone. The preflight is used as an independent oracle: it must accept the suggested start and reject the next one.
/// </summary>
public sealed class RlPanelPeriodAdvisorTests : IDisposable
{
    private const int Total = 2_000;
    private static readonly DateOnly First = RlSourceFixture.FirstDay;
    private static readonly string[] Four = { "AAA", "BBB", "CCC", "DDD" };
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sa_rl_advisor_" + Guid.NewGuid().ToString("N"));
    private string Daily => Path.Combine(_root, "Daily");
    private string Meta => Path.Combine(_root, "Metadata");

    public RlPanelPeriodAdvisorTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private void Write(string symbol, int count = Total, int offset = 0, string currency = "USD", string timezone = "UTC", string quoteType = "EQUITY", bool metadata = true)
        => RlSourceFixture.WriteLocalSymbol(_root, symbol, First.AddDays(offset), count, currency, timezone, quoteType, metadata);

    private void WriteFour(int count = Total) { foreach (string s in Four) Write(s, count); }

    /// <summary>A symbol whose Daily file lacks the given day indexes (the calendar of the other symbols still holds them).</summary>
    private void WriteSparse(string symbol, int count, params int[] missing)
    {
        Directory.CreateDirectory(Daily); Directory.CreateDirectory(Meta);
        string filter = missing.Length == 0 ? "" : " WHERE range NOT IN (" + string.Join(",", missing) + ")";
        RlSourceFixture.CreateParquet(Path.Combine(Daily, symbol + ".parquet"),
            $"SELECT DATE '{First:yyyy-MM-dd}'+CAST(range AS INTEGER) AS date,100::FLOAT AS open,110::FLOAT AS high,90::FLOAT AS low,100::FLOAT AS close,1000::BIGINT AS volume FROM range({count}){filter}");
        RlSourceFixture.CreateParquet(Path.Combine(Meta, symbol + ".meta.parquet"), "SELECT 'USD' AS currency,'EQUITY' AS quote_type,'UTC' AS exchange_timezone_name");
    }

    private RlStartSuggestion Suggest(string[] symbols, int days = Total, RlPanelEvaluationMode mode = RlPanelEvaluationMode.ShortHistory,
        Core.Models.Training.TrainingResourceLimits? limits = null, CancellationToken ct = default)
        => RlPanelPeriodAdvisor.Suggest(symbols, First.AddDays(days), mode, limits ?? RlSourceFixture.Limits, Daily, Meta, ct);

    private PanelPreflight Preflight(string[] symbols, DateOnly start, int days = Total, RlPanelEvaluationMode mode = RlPanelEvaluationMode.ShortHistory,
        RlPanelPartitionPlan? plan = null)
        => RlPanelPreflightEvaluator.Evaluate(symbols, start, First.AddDays(days), false, mode, new RlPortfolioSettings(), RlSourceFixture.Limits, Daily, Meta, CancellationToken.None, plan);

    private static RlPanelPartitionPlan BlockCount(int train = 1, int validation = 1, int test = 1)
        => new(RlPortfolioPartitionMode.BlockCount, new RlPanelBlockRequirement(train, validation, test));

    // ----- partition plan -----

    [Fact]
    public void ABlockCountPlanSuggestsTheStartItsOwnMinimumBeforeTheEndAndThePreflightAgrees()
    {
        WriteFour();
        var plan = BlockCount(3, 1, 1);
        int minimum = RlPanelCatalogBuilder.MinimumCalendarLength(RlPanelEvaluationMode.ShortHistory, plan);
        Assert.Equal(1088, minimum);
        var result = RlPanelPeriodAdvisor.Suggest(Four, First.AddDays(Total), RlPanelEvaluationMode.ShortHistory, RlSourceFixture.Limits, Daily, Meta,
            CancellationToken.None, plan);
        Assert.Equal(new RlStartSuggestion(RlStartSuggestionStatus.Found, First.AddDays(Total - minimum), minimum, minimum), result);
        Assert.True(Preflight(Four, result.Start!.Value, plan: plan).Feasible);
        Assert.False(Preflight(Four, result.Start.Value.AddDays(1), plan: plan).Feasible);
    }

    [Fact]
    public void AStoredHistoryBelowTheRatioMinimumIsEnoughForABlockCountPlan()
    {
        // 1,218 stored bars: the ratio rule needs 1,278 (insufficient), the block-count plan with one block per partition needs 704.
        WriteFour(1_218);
        var ratio = Suggest(Four, days: 1_218);
        Assert.Equal(RlStartSuggestionStatus.InsufficientHistory, ratio.Status);
        var block = RlPanelPeriodAdvisor.Suggest(Four, First.AddDays(1_218), RlPanelEvaluationMode.ShortHistory, RlSourceFixture.Limits, Daily, Meta,
            CancellationToken.None, BlockCount());
        Assert.Equal(new RlStartSuggestion(RlStartSuggestionStatus.Found, First.AddDays(1_218 - 704), 704, 704), block);
    }

    [Fact]
    public void ThePlanAppliesToTheShortHistoryEvaluationOnly()
    {
        WriteFour();
        Assert.Throws<ArgumentException>(() => RlPanelPeriodAdvisor.Suggest(Four, First.AddDays(Total), RlPanelEvaluationMode.Standard, RlSourceFixture.Limits,
            Daily, Meta, CancellationToken.None, BlockCount()));
    }

    // ----- the rule -----

    [Fact]
    public void AlignedSymbolsGetTheStartExactlyTheMinimumCalendarLengthBeforeTheEnd()
    {
        WriteFour();
        var result = Suggest(Four);
        int minimum = RlPanelCatalogBuilder.MinimumCalendarLength(RlPanelEvaluationMode.ShortHistory);
        Assert.Equal(1278, minimum);
        Assert.Equal(new RlStartSuggestion(RlStartSuggestionStatus.Found, First.AddDays(Total - minimum), minimum, minimum), result);
        // The preflight, an independent code path, accepts exactly this start and rejects the next later day.
        var accepted = Preflight(Four, result.Start!.Value);
        Assert.True(accepted.Feasible);
        Assert.Equal(minimum, accepted.CalendarLength);
        var rejected = Preflight(Four, result.Start.Value.AddDays(1));
        Assert.False(rejected.Feasible);
        Assert.Equal(minimum - 1, rejected.CalendarLength);
    }

    [Fact]
    public void TheStandardModeNeedsTheLongerMinimum()
    {
        WriteFour(6_000);
        var result = Suggest(Four, days: 6_000, mode: RlPanelEvaluationMode.Standard);
        Assert.Equal(new RlStartSuggestion(RlStartSuggestionStatus.Found, First.AddDays(6_000 - 5_118), 5_118, 5_118), result);
        Assert.True(Preflight(Four, result.Start!.Value, 6_000, RlPanelEvaluationMode.Standard).Feasible);
    }

    [Fact]
    public void AGapInTheLastSegmentPushesTheStartEarlierUntilTheCatalogRuleHolds()
    {
        // One symbol is complete; the others each miss one of the last three days. At 1,278..1,280 bars the last (oos) block ends on the final day and
        // has fewer than two complete symbols; at 1,281 bars the block ends one day earlier, symbol AAA is complete in it and the rule holds.
        WriteSparse("AAA", Total, Total - 1);
        WriteSparse("BBB", Total, Total - 2);
        WriteSparse("CCC", Total, Total - 3);
        Write("DDD");
        var result = Suggest(Four);
        Assert.Equal(new RlStartSuggestion(RlStartSuggestionStatus.Found, First.AddDays(Total - 1_281), 1_278, 1_281), result);
        var accepted = Preflight(Four, result.Start!.Value);
        Assert.True(accepted.Feasible);
        Assert.Equal(1_281, accepted.CalendarLength);
        Assert.False(Preflight(Four, result.Start.Value.AddDays(1)).Feasible);
    }

    [Fact]
    public void TheUnionCalendarCanBeLongerThanAnySingleSymbol()
    {
        // Two symbols cover the first 1,500 days, two others the days from 500 to the end: the union spans the whole period.
        Write("AAA", 1_500); Write("BBB", 1_500); Write("CCC", Total - 500, 500); Write("DDD", Total - 500, 500);
        var result = Suggest(Four);
        Assert.Equal(RlStartSuggestionStatus.Found, result.Status);
        Assert.Equal(First.AddDays(Total - 1_278), result.Start);
        Assert.True(Preflight(Four, result.Start!.Value).Feasible);
    }

    [Fact]
    public void AShortHistoryIsReportedWithTheEarliestUsableDate()
    {
        WriteFour(1_000);
        var result = Suggest(Four, days: 1_000);
        Assert.Equal(new RlStartSuggestion(RlStartSuggestionStatus.InsufficientHistory, First, 1_278, 1_000), result);
        Assert.False(Preflight(Four, result.Start!.Value, 1_000).Feasible);
    }

    [Fact]
    public void NothingAfterTheEndIsRead()
    {
        WriteFour();
        var result = Suggest(Four, days: 1_500);
        Assert.Equal(new RlStartSuggestion(RlStartSuggestionStatus.Found, First.AddDays(1_500 - 1_278), 1_278, 1_278), result);
    }

    [Fact]
    public void TheSampleCeilingCapsTheSearchRange()
    {
        WriteFour();
        var capped = RlSourceFixture.Limits with { MaxSamples = 1_000 };
        var result = Suggest(Four, limits: capped);
        Assert.Equal(new RlStartSuggestion(RlStartSuggestionStatus.InsufficientHistory, First.AddDays(Total - 1_000), 1_278, 1_000), result);
        var roomy = RlSourceFixture.Limits with { MaxSamples = 1_500 };
        Assert.Equal(RlStartSuggestionStatus.Found, Suggest(Four, limits: roomy).Status);
    }

    // ----- symbols -----

    [Fact]
    public void SymbolsThatThePreflightCannotUseAreSkippedWithoutAnError()
    {
        WriteFour();
        Write("EEE", quoteType: "ETF"); Write("FFF", currency: "EUR"); Write("GGG", timezone: "America/New_York");
        Write("HHH", metadata: false); Write("III");
        File.WriteAllBytes(Path.Combine(Daily, "III.parquet"), new byte[] { 1, 2, 3, 4 });
        string[] mixed = Four.Concat(new[] { "EEE", "FFF", "GGG", "HHH", "III", "A/B", "NOFILE", " AAA ", "" }).ToArray();
        var result = Suggest(mixed);
        Assert.Equal(Suggest(Four), result);
        var statuses = Preflight(mixed, First).Symbols.Where(s => s.Status == RlPanelSymbolStatus.Ready).Select(s => s.Symbol).OrderBy(s => s, StringComparer.Ordinal);
        Assert.Equal(Four, statuses);                 // the preflight's Ready set is the advisor's kept set
    }

    [Fact]
    public void FewerThanTwoUsableSymbolsGiveNoSuggestion()
    {
        Assert.Equal(RlStartSuggestionStatus.NoUsableData, Suggest(Array.Empty<string>()).Status);
        Write("AAA");
        var one = Suggest(new[] { "AAA", "MISSING" });
        Assert.Equal((RlStartSuggestionStatus.NoUsableData, null, 0), (one.Status, one.Start, one.AvailableBars));
        Assert.Equal(1_278, one.RequiredBars);
    }

    [Fact]
    public void ASymbolWhoseLastBarReachesTheCurrentExchangeDateIsSkippedLikeTheReadError()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        RlSourceFixture.WriteLocalSymbol(_root, "AAA", today.AddDays(-5), 20);
        RlSourceFixture.WriteLocalSymbol(_root, "BBB", today.AddDays(-5), 20);
        var result = RlPanelPeriodAdvisor.Suggest(new[] { "AAA", "BBB" }, today.AddDays(30), RlPanelEvaluationMode.ShortHistory, RlSourceFixture.Limits, Daily, Meta, CancellationToken.None);
        Assert.Equal(RlStartSuggestionStatus.NoUsableData, result.Status);
    }

    [Fact]
    public void ThePricesAreNotValidatedByTheAdvisorButAreByThePreflight()
    {
        Write("AAA");
        Directory.CreateDirectory(Daily);
        RlSourceFixture.CreateParquet(Path.Combine(Daily, "BAD.parquet"),
            $"SELECT DATE '{First:yyyy-MM-dd}'+CAST(range AS INTEGER) AS date,(-1)::FLOAT AS open,110::FLOAT AS high,90::FLOAT AS low,100::FLOAT AS close,1000::BIGINT AS volume FROM range({Total})");
        RlSourceFixture.CreateParquet(Path.Combine(Meta, "BAD.meta.parquet"), "SELECT 'USD' AS currency,'EQUITY' AS quote_type,'UTC' AS exchange_timezone_name");
        Assert.Equal(RlStartSuggestionStatus.Found, Suggest(new[] { "AAA", "BAD" }).Status);       // dates only: the advisor keeps the symbol
        Assert.Equal(RlPanelSymbolStatus.ReadError, Preflight(new[] { "AAA", "BAD" }, First).Symbols.Single(s => s.Symbol == "BAD").Status);
    }

    // ----- contract -----

    [Fact]
    public void ItReadsInPlaceWritesNothingAndIsCancelable()
    {
        WriteFour();
        string before = RlSourceFixture.TreeDigest(_root);
        _ = Suggest(Four);
        Assert.Equal(before, RlSourceFixture.TreeDigest(_root));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => Suggest(Four, ct: cancelled.Token));
        Assert.Throws<ArgumentNullException>(() => RlPanelPeriodAdvisor.Suggest(null!, First, RlPanelEvaluationMode.ShortHistory, RlSourceFixture.Limits, Daily, Meta, CancellationToken.None));
    }

    [Fact]
    public async Task TheServiceSeamDefaultsToNoSuggestionAndTheServiceDelegatesToTheAdvisor()
    {
        IRlPortfolioService withoutOverride = new NoSuggestionService();
        Assert.Null(await withoutOverride.SuggestStartAsync(Four, First, RlPanelEvaluationMode.ShortHistory, CancellationToken.None));
        IRlPortfolioService real = new RlPortfolioService(new RlPortfolioSettings());
        var none = await real.SuggestStartAsync(new[] { "SA_NO_SUCH_SYMBOL_1", "SA_NO_SUCH_SYMBOL_2" }, First, RlPanelEvaluationMode.ShortHistory, CancellationToken.None);
        Assert.Equal(RlStartSuggestionStatus.NoUsableData, none!.Status);
    }

    private sealed class NoSuggestionService : IRlPortfolioService
    {
        public RlPortfolioSettings Settings { get; } = new();
        public Task<PanelPreflight> PreflightAsync(System.Collections.Generic.IReadOnlyList<string> symbols, DateOnly start, DateOnly endExclusive, bool holdoutRequested,
            RlPanelEvaluationMode mode, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<RlPortfolioTrainingResult> RunAsync(RlPortfolioRunRequest request, IProgress<RlTrainingProgress>? progress,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    // ----- ReadDates -----

    [Fact]
    public void ReadDatesReturnsTheStoredDatesBeforeTheEndAndRejectsABrokenCalendar()
    {
        string good = Path.Combine(_root, "good.parquet");
        RlSourceFixture.CreateParquet(good, $"SELECT DATE '{First:yyyy-MM-dd}'+CAST(range AS INTEGER) AS date FROM range(10)");
        var dates = RlLocalParquetReader.ReadDates(good, First.AddDays(4), RlSourceFixture.Limits, CancellationToken.None);
        Assert.Equal(Enumerable.Range(0, 4).Select(i => First.AddDays(i)), dates);
        Assert.True(RlLocalParquetReader.ReadDates(good, First, RlSourceFixture.Limits, CancellationToken.None).IsEmpty);

        string backwards = Path.Combine(_root, "backwards.parquet");
        RlSourceFixture.CreateParquet(backwards, $"SELECT DATE '{First:yyyy-MM-dd}'+CAST(range AS INTEGER) AS date FROM range(10) ORDER BY range DESC");
        Assert.Throws<InvalidDataException>(() => RlLocalParquetReader.ReadDates(backwards, First.AddDays(50), RlSourceFixture.Limits, CancellationToken.None));

        string text = Path.Combine(_root, "text.parquet");
        RlSourceFixture.CreateParquet(text, "SELECT '2000-01-01' AS date");
        Assert.Throws<InvalidDataException>(() => RlLocalParquetReader.ReadDates(text, First.AddDays(50), RlSourceFixture.Limits, CancellationToken.None));

        string withNull = Path.Combine(_root, "null.parquet");
        RlSourceFixture.CreateParquet(withNull, "SELECT CAST(NULL AS DATE) AS date");
        Assert.Throws<InvalidDataException>(() => RlLocalParquetReader.ReadDates(withNull, First.AddDays(50), RlSourceFixture.Limits, CancellationToken.None));
    }
}
