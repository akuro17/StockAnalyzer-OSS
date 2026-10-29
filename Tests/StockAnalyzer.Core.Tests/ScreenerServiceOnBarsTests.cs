using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services.Screener;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Tests.TestHelpers;
using Xunit;

namespace StockAnalyzer.Core.Tests;

/// <summary>
/// <see cref="IScreenerService.ScreenOnBarsAsync"/>: the scan over the bars of a <see cref="ScreenerBarSelection"/> that the Screener tab uses for weekly and monthly frames.
/// The daily bars are one per calendar day from Monday 2024-01-01 and close at 100 + day index (Monday 2024-01-29 is day 28, Tuesday 2024-01-30 day 29), so every
/// number is worked out from that: complete weeks end on the Sundays 01-07, 01-14, 01-21 and 01-28; the week of 01-29 is partial.
/// </summary>
public sealed class ScreenerServiceOnBarsTests
{
    private static readonly DateTime FirstDay = new(2024, 1, 1);

    private static List<CandleData> Daily(int days)
        => Enumerable.Range(0, days)
            .Select(day => new CandleData(FirstDay.AddDays(day), 100m + day, 101m + day, 99m + day, 100m + day, 1000))
            .ToList();

    /// <summary>Serves the daily bars it was given and records every request (a weekly/monthly request would show that stored weekly files are read).</summary>
    private sealed class RecordingDataService : IDataService
    {
        private readonly IReadOnlyDictionary<string, List<CandleData>> _bySymbol;

        public RecordingDataService(IReadOnlyDictionary<string, List<CandleData>> bySymbol) => _bySymbol = bySymbol;

        public ConcurrentBag<(string Symbol, TimeFrame TimeFrame, int Count)> Requests { get; } = new();

        public Task<IReadOnlyList<CandleData>> LoadCandlesAsync(string symbol, TimeFrame timeFrame, int count = 100)
        {
            Requests.Add((symbol, timeFrame, count));
            return Task.FromResult<IReadOnlyList<CandleData>>(_bySymbol.TryGetValue(symbol, out List<CandleData>? bars) ? bars : new List<CandleData>());
        }
    }

    /// <summary>Records the bars it was handed per symbol; matches a symbol that has at least one bar.</summary>
    private sealed class RecordingCondition : IScreeningCondition
    {
        public ConcurrentDictionary<string, IReadOnlyList<CandleData>> Seen { get; } = new();

        public bool IsMet(IReadOnlyList<CandleData> candles) => candles.Count > 0;

        public ValueTask<bool> IsMetAsync(IReadOnlyList<CandleData> candles)
        {
            Seen[candles.Count > 0 ? $"{candles[0].Timestamp:yyyyMMdd}-{candles.Count}" : "empty"] = candles;
            return new ValueTask<bool>(IsMet(candles));
        }
    }

    private static (ScreenerService Service, RecordingDataService Data) Create(params (string Symbol, int Days)[] symbols)
    {
        var data = new RecordingDataService(symbols.ToDictionary(item => item.Symbol, item => Daily(item.Days), StringComparer.Ordinal));
        return (new ScreenerService(data, new PythonIpcTestSettingsBase(), Array.Empty<IMarketDataProvider>()), data);
    }

    private static Task<ScreenerBarsResult> ScanAsync(ScreenerService service, ScreenerBarSelection selection, TimeFrame timeFrame, IScreeningCondition condition, params string[] symbols)
        => service.ScreenOnBarsAsync(symbols.ToList(), condition, timeFrame, selection, new Progress<int>(), CancellationToken.None);

    [Fact]
    public async Task AWeeklyScanReadsTheDailyHistoryBuildsTheWeeksAndLeavesOutThePartialLastWeek()
    {
        (ScreenerService service, RecordingDataService data) = Create(("A", 30));
        var condition = new RecordingCondition();

        ScreenerBarsResult result = await ScanAsync(service, new ScreenerBarSelection(null, true), TimeFrame.W1, condition, "A");

        Assert.Equal(new[] { "A" }, result.Matched);
        Assert.Equal(1, result.SkippedPartialBarCount); // the week of 2024-01-29 (Monday and Tuesday)
        Assert.Empty(result.PartialLastBarPeriodStarts);
        Assert.Empty(result.SymbolsWithoutBars);
        IReadOnlyList<CandleData> seen = Assert.Single(condition.Seen).Value;
        Assert.Equal(4, seen.Count);
        Assert.Equal(127m, seen[^1].Close); // Sunday 2024-01-28 is day 27
        Assert.Equal(new DateTime(2024, 1, 22), seen[^1].Timestamp); // a weekly bar is stamped with the first day of its week
        // the whole daily history is read (count 0) and nothing else: no stored weekly file is asked for
        Assert.All(data.Requests, request => Assert.Equal((TimeFrame.D1, 0), (request.TimeFrame, request.Count)));
    }

    [Fact]
    public async Task WithCompleteBarsOnlyFalseThePartialWeekIsEvaluatedAndNamed()
    {
        (ScreenerService service, _) = Create(("A", 30));
        var condition = new RecordingCondition();

        ScreenerBarsResult result = await ScanAsync(service, new ScreenerBarSelection(null, false), TimeFrame.W1, condition, "A");

        IReadOnlyList<CandleData> seen = Assert.Single(condition.Seen).Value;
        Assert.Equal(5, seen.Count);
        Assert.Equal(129m, seen[^1].Close); // Tuesday 2024-01-30 is day 29
        Assert.Equal(new[] { new DateOnly(2024, 1, 29) }, result.PartialLastBarPeriodStarts);
        Assert.Equal(0, result.SkippedPartialBarCount);
    }

    [Fact]
    public async Task ADateCutsTheDailyBarsBeforeTheWeeksAreBuilt()
    {
        (ScreenerService service, _) = Create(("A", 30));
        var condition = new RecordingCondition();

        // Wednesday 2024-01-17: the week of 01-15 holds three days, so it is partial and left out; the weeks of 01-01 and 01-08 remain (the last close is Sunday 01-14, day 13).
        ScreenerBarsResult result = await ScanAsync(service, new ScreenerBarSelection(new DateOnly(2024, 1, 17), true), TimeFrame.W1, condition, "A");

        IReadOnlyList<CandleData> seen = Assert.Single(condition.Seen).Value;
        Assert.Equal(2, seen.Count);
        Assert.Equal(113m, seen[^1].Close);
        Assert.Equal(1, result.SkippedPartialBarCount);
    }

    [Fact]
    public async Task ASymbolWithoutABarIsNamedAndTheAnswerIsRestrictedToTheScannedSymbols()
    {
        (ScreenerService service, _) = Create(("A", 30));

        ScreenerBarsResult result = await ScanAsync(service, new ScreenerBarSelection(null, true), TimeFrame.W1, new RecordingCondition(), "A", "MISSING");

        Assert.Equal(new[] { "A" }, result.Matched);
        Assert.Equal(new[] { "MISSING" }, result.SymbolsWithoutBars);
    }

    [Fact]
    public async Task TheResultNamesOnlyTheSymbolsTheScanEvaluated()
    {
        // ToResult is the one place the answer is put together: bars read for another symbol (a comparison symbol) do not appear in the scan's answer.
        (_, RecordingDataService data) = Create(("A", 30), ("B", 30));
        var bars = new ScreenerBarsDataService(data, new ScreenerBarSelection(null, true));
        await bars.LoadCandlesAsync("A", TimeFrame.W1, 10);
        await bars.LoadCandlesAsync("B", TimeFrame.W1, 10);
        await bars.LoadCandlesAsync("GONE", TimeFrame.W1, 10);

        ScreenerBarsResult onlyA = bars.ToResult(new[] { "A" }, new[] { "A" });
        ScreenerBarsResult both = bars.ToResult(new[] { "A" }, new[] { "A", "B", "GONE" });

        Assert.Equal(1, onlyA.SkippedPartialBarCount);
        Assert.Empty(onlyA.SymbolsWithoutBars);
        Assert.Equal(2, both.SkippedPartialBarCount);
        Assert.Equal(new[] { "GONE" }, both.SymbolsWithoutBars);
    }

    [Fact]
    public async Task AServiceThatDoesNotScanSelectionsRefusesInsteadOfScanningOtherBars()
    {
        IScreenerService minimal = new RefusingScreener();

        await Assert.ThrowsAsync<NotSupportedException>(() => minimal.ScreenOnBarsAsync(
            new List<string> { "A" }, new RecordingCondition(), TimeFrame.W1, new ScreenerBarSelection(null, true), new Progress<int>(), CancellationToken.None));
    }

    // ---- the bars of the result columns are the bars the scan evaluated ---------------------------------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheBarsOfTheResultColumnsAreExactlyTheBarsTheScanEvaluated(bool completeBarsOnly)
    {
        (ScreenerService service, _) = Create(("A", 30));
        var condition = new RecordingCondition();
        var selection = new ScreenerBarSelection(null, completeBarsOnly);

        await ScanAsync(service, selection, TimeFrame.W1, condition, "A");
        IReadOnlyList<CandleData> evaluated = Assert.Single(condition.Seen).Value;
        IReadOnlyList<CandleData> loaded = await service.LoadScreenerBarsAsync("A", TimeFrame.W1, selection, 0, CancellationToken.None);

        Assert.Equal(completeBarsOnly ? 4 : 5, loaded.Count);
        Assert.Equal(evaluated, loaded);
    }

    [Fact]
    public async Task TheLatestCountBarsAreReturnedAndASymbolWithoutABarGivesAnEmptyList()
    {
        (ScreenerService service, _) = Create(("A", 30));
        var selection = new ScreenerBarSelection(new DateOnly(2024, 1, 17), true);

        IReadOnlyList<CandleData> last = await service.LoadScreenerBarsAsync("A", TimeFrame.W1, selection, 1, CancellationToken.None);
        IReadOnlyList<CandleData> none = await service.LoadScreenerBarsAsync("MISSING", TimeFrame.W1, selection, 5, CancellationToken.None);

        Assert.Equal(113m, Assert.Single(last).Close); // the week of 2024-01-08 ends Sunday 01-14 (day 13); the week of 01-15 is partial at 01-17
        Assert.Empty(none);
    }

    [Theory]
    [InlineData(TimeFrame.H1)]
    [InlineData(TimeFrame.M5)]
    public async Task ATimeFrameTheDataLayerDoesNotDefineIsRefusedByBothSelectionMembers(TimeFrame frame)
    {
        (ScreenerService service, RecordingDataService data) = Create(("A", 30));
        var selection = new ScreenerBarSelection(null, true);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ScanAsync(service, selection, frame, new RecordingCondition(), "A"));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.LoadScreenerBarsAsync("A", frame, selection, 1, CancellationToken.None));
        Assert.Empty(data.Requests); // refused before anything was read
    }

    [Fact]
    public async Task EveryFrameOfTheSingleDefinitionIsAccepted()
    {
        (ScreenerService service, _) = Create(("A", 30));
        var selection = new ScreenerBarSelection(null, false); // January 2024 of the 30 days is a partial month: it is kept so every frame has a bar

        foreach (TimeFrame frame in DataServiceTimeFrames.Supported)
        {
            IReadOnlyList<CandleData> bars = await service.LoadScreenerBarsAsync("A", frame, selection, 0, CancellationToken.None);
            Assert.NotEmpty(bars);
        }
    }

    [Fact]
    public async Task ABarReaderThatIsNotGivenAValidRequestRefuses()
    {
        (ScreenerService service, _) = Create(("A", 30));
        var selection = new ScreenerBarSelection(null, true);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.LoadScreenerBarsAsync("A", TimeFrame.W1, selection, -1, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => service.LoadScreenerBarsAsync(" ", TimeFrame.W1, selection, 1, CancellationToken.None));
        await Assert.ThrowsAsync<NotSupportedException>(() => ((IScreenerService)new RefusingScreener()).LoadScreenerBarsAsync("A", TimeFrame.W1, selection, 1, CancellationToken.None));
    }
}
