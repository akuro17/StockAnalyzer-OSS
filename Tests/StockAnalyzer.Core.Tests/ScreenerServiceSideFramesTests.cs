using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Screener;
using StockAnalyzer.Core.Tests.TestHelpers;
using Xunit;
using static StockAnalyzer.Core.Tests.TestHelpers.ScreenerSideFrameFixtures;

namespace StockAnalyzer.Core.Tests;

/// <summary>
/// <see cref="IScreenerService.ScreenWithSideFramesAsync"/> and <see cref="ScreenerBarsDataService.LoadBarSetAsync"/>: the scan in which every side reads its own time frame,
/// built from the daily history. Bars: see <see cref="ScreenerSideFrameFixtures"/> (30 days from Monday 2024-01-01; the week of 01-29 is partial).
/// </summary>
public sealed class ScreenerServiceSideFramesTests
{
    private static (ScreenerService Service, RecordingDataService Data) Create(params (string Symbol, int Days)[] symbols) => CreateService(symbols);

    private static Task<ScreenerSideFramesResult> ScanAsync(ScreenerService service, ScreenerBarSelection selection, IScreeningCondition condition, params string[] symbols)
        => service.ScreenWithSideFramesAsync(symbols.ToList(), condition, selection, new Progress<int>(), CancellationToken.None);

    private static readonly ScreenerBarSelection Complete = new(null, true);
    private static readonly ScreenerBarSelection WithPartial = new(null, false);

    // ---- routing -----------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ADailyConditionWithoutADateIsThePlainDailyScan()
    {
        (ScreenerService service, RecordingDataService data) = Create(("A", 30));
        ScreenerIndicatorEntry entry = Versus(Close(), ComparisonOperator.GreaterThan, 100m);

        ScreenerSideFramesResult result = await ScanAsync(service, Complete, entry, "A");

        Assert.Equal(new[] { "A" }, result.Matched);
        Assert.Empty(result.Frames);
        Assert.Empty(result.SymbolsWithoutDailyBars);
        // the plain scan asks for the sized history of the daily frame, not for the whole history
        (string _, TimeFrame frame, int count) = Assert.Single(data.Requests);
        Assert.Equal(TimeFrame.D1, frame);
        Assert.Equal(ScreenerHistoryRequirements.GetRequiredCandleCount(entry), count);
    }

    [Fact]
    public async Task ADailyConditionWithADateReadsTheWholeHistoryAndCutsItAtTheDate()
    {
        (ScreenerService service, RecordingDataService data) = Create(("A", 30));
        // Wednesday 2024-01-17 is day 16: the close is 116 on that date and 129 on the last day.
        ScreenerIndicatorEntry entry = Versus(Close(), ComparisonOperator.Equal, 116m);

        ScreenerSideFramesResult cut = await ScanAsync(service, new ScreenerBarSelection(new DateOnly(2024, 1, 17), true), entry, "A");
        ScreenerSideFramesResult last = await ScanAsync(service, Complete, entry, "A");

        Assert.Equal(new[] { "A" }, cut.Matched);
        Assert.Empty(last.Matched);
        Assert.Contains(data.Requests, request => request == ("A", TimeFrame.D1, 0));
    }

    [Fact]
    public async Task APlainDailyScanAndTheBarSetPathGiveTheSameAnswerForDailySides()
    {
        // 40 symbols with different shapes; the far date forces the bar-set path for a daily-only condition.
        var shapes = Enumerable.Range(0, 40).Select(i => ($"S{i:D2}", Days: 25 + i)).ToArray();
        (ScreenerService service, _) = Create(shapes);
        List<string> symbols = shapes.Select(shape => shape.Item1).ToList();
        ScreenerIndicatorEntry entry = Versus(Sma(5), ComparisonOperator.LessThan, Close(offset: 1));

        List<string> plain = await service.ScreenAsync(symbols, entry, TimeFrame.D1, new Progress<int>(), CancellationToken.None);
        ScreenerSideFramesResult viaBarSet = await service.ScreenWithSideFramesAsync(symbols, entry, new ScreenerBarSelection(new DateOnly(2999, 1, 1), true), new Progress<int>(), CancellationToken.None);

        Assert.NotEmpty(plain);
        Assert.Equal(plain, viaBarSet.Matched);
    }

    // ---- sides on their own frames -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ADailyPriceAgainstAWeeklyAverageIsBuiltFromTheDailyHistoryOnly()
    {
        (ScreenerService service, RecordingDataService data) = Create(("A", 30));
        ScreenerIndicatorEntry entry = Versus(Close(), ComparisonOperator.GreaterThan, Sma(2, TimeFrame.W1)); // 129 > (120 + 127) / 2

        ScreenerSideFramesResult result = await ScanAsync(service, Complete, entry, "A");

        Assert.Equal(new[] { "A" }, result.Matched);
        ScreenerFrameReport report = Assert.Single(result.Frames);
        Assert.Equal(TimeFrame.W1, report.Frame);
        Assert.Equal(1, report.SkippedPartialBarCount);
        Assert.Empty(report.SymbolsWithoutBars);
        Assert.Empty(report.PartialLastBarPeriodStarts);
        // one read of the whole daily history per symbol, never a stored weekly file
        Assert.All(data.Requests, request => Assert.Equal(("A", TimeFrame.D1, 0), request));
        Assert.Single(data.Requests);
    }

    [Fact]
    public async Task CompleteBarsOnlyDecidesWhichWeekIsTheLastOne()
    {
        (ScreenerService service, _) = Create(("A", 30));
        ScreenerIndicatorEntry lastCompleteWeek = Versus(Close(TimeFrame.W1), ComparisonOperator.Equal, 127m);
        ScreenerIndicatorEntry partialWeek = Versus(Close(TimeFrame.W1), ComparisonOperator.Equal, 129m);

        ScreenerSideFramesResult complete127 = await ScanAsync(service, Complete, lastCompleteWeek, "A");
        ScreenerSideFramesResult complete129 = await ScanAsync(service, Complete, partialWeek, "A");
        ScreenerSideFramesResult partial127 = await ScanAsync(service, WithPartial, lastCompleteWeek, "A");
        ScreenerSideFramesResult partial129 = await ScanAsync(service, WithPartial, partialWeek, "A");

        Assert.Equal(new[] { "A" }, complete127.Matched);
        Assert.Empty(complete129.Matched);
        Assert.Empty(partial127.Matched);
        Assert.Equal(new[] { "A" }, partial129.Matched);
        ScreenerFrameReport evaluated = Assert.Single(partial129.Frames);
        Assert.Equal(new[] { new DateOnly(2024, 1, 29) }, evaluated.PartialLastBarPeriodStarts);
        Assert.Equal(0, evaluated.SkippedPartialBarCount);
    }

    [Fact]
    public async Task EachFrameGetsItsOwnReportInTheOrderOfTheSingleDefinition()
    {
        (ScreenerService service, _) = Create(("A", 30));
        // Monthly: the 30 days of January are a partial month, so the monthly list is empty under CompleteBarsOnly and the monthly side has no value.
        ScreenerIndicatorEntry entry = Versus(Close(TimeFrame.MN1), ComparisonOperator.GreaterThan, Close(TimeFrame.W1));

        ScreenerSideFramesResult result = await ScanAsync(service, Complete, entry, "A");

        Assert.Empty(result.Matched);
        Assert.Equal(new[] { TimeFrame.W1, TimeFrame.MN1 }, result.Frames.Select(report => report.Frame));
        ScreenerFrameReport month = result.Frames[1];
        Assert.Equal(new[] { "A" }, month.SymbolsWithoutBars);
        Assert.Equal(1, month.SkippedPartialBarCount);
    }

    [Fact]
    public async Task TheLatestRequiredBarsOfEachFrameAreKept()
    {
        (ScreenerService service, RecordingDataService data) = Create(("A", 30));
        var bars = new ScreenerBarsDataService(data, WithPartial);

        IScreenerBarSet set = await bars.LoadBarSetAsync("A", new[] { TimeFrame.D1, TimeFrame.W1 }, 3);

        Assert.Equal(new[] { 127m, 128m, 129m }, set.Get(TimeFrame.D1).Select(bar => bar.Close));
        Assert.Equal(new[] { 120m, 127m, 129m }, set.Get(TimeFrame.W1).Select(bar => bar.Close));
        IScreenerBarSet all = await bars.LoadBarSetAsync("A", new[] { TimeFrame.D1, TimeFrame.W1 }, 0);
        Assert.Equal(30, all.Get(TimeFrame.D1).Count);
        Assert.Equal(5, all.Get(TimeFrame.W1).Count);
    }

    [Fact]
    public async Task ASymbolWithoutBarsIsNamedOnceAsWithoutDailyBarsAndInEveryFrame()
    {
        (ScreenerService service, _) = Create(("A", 30));
        ScreenerIndicatorEntry entry = Versus(Close(), ComparisonOperator.GreaterThan, Close(TimeFrame.W1, offset: 1));

        ScreenerSideFramesResult result = await ScanAsync(service, Complete, entry, "A", "MISSING");

        Assert.Equal(new[] { "A" }, result.Matched);
        Assert.Equal(new[] { "MISSING" }, result.SymbolsWithoutDailyBars);
        Assert.Equal(new[] { "MISSING" }, Assert.Single(result.Frames).SymbolsWithoutBars);
    }

    [Fact]
    public async Task TheMatchesFollowTheOrderOfTheInput()
    {
        (ScreenerService service, _) = Create(("A", 30), ("B", 30), ("C", 30), ("D", 30));
        ScreenerIndicatorEntry entry = Versus(Close(TimeFrame.W1), ComparisonOperator.Equal, 127m);

        ScreenerSideFramesResult result = await ScanAsync(service, Complete, entry, "D", "B", "NONE", "A", "C");

        Assert.Equal(new[] { "D", "B", "A", "C" }, result.Matched);
    }

    [Fact]
    public async Task ATreeWithADisabledWeeklyLeafReadsDailyBarsOnly()
    {
        (ScreenerService service, RecordingDataService data) = Create(("A", 30));
        var tree = new ScreenerConditionTreeCondition(Group(
            LogicalOperator.And,
            Leaf(Versus(Close(), ComparisonOperator.GreaterThan, 100m)),
            Leaf(Versus(Close(TimeFrame.W1), ComparisonOperator.Equal, 0m, enabled: false))));

        ScreenerSideFramesResult result = await ScanAsync(service, Complete, tree, "A");

        Assert.Equal(new[] { "A" }, result.Matched);
        Assert.Empty(result.Frames);
        Assert.Equal(ScreenerHistoryRequirements.GetRequiredCandleCount(tree), Assert.Single(data.Requests).Count); // the plain scan
    }

    [Fact]
    public async Task ATreeWithoutAnActiveLeafOrAnEmptyInputReadsNothing()
    {
        (ScreenerService service, RecordingDataService data) = Create(("A", 30));
        var inactive = new ScreenerConditionTreeCondition(Group(LogicalOperator.And, Leaf(Versus(Close(TimeFrame.W1), ComparisonOperator.GreaterThan, 0m, enabled: false))));

        ScreenerSideFramesResult none = await ScanAsync(service, Complete, inactive, "A");
        ScreenerSideFramesResult empty = await ScanAsync(service, Complete, Versus(Close(TimeFrame.W1), ComparisonOperator.GreaterThan, 0m));

        Assert.Empty(none.Matched);
        Assert.Empty(empty.Matched);
        Assert.Empty(data.Requests);
    }

    // ---- refusals ----------------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(TimeFrame.H1)]
    [InlineData(TimeFrame.M5)]
    public async Task ATimeFrameTheDataLayerDoesNotDefineIsRefusedBeforeAnythingIsRead(TimeFrame frame)
    {
        (ScreenerService service, RecordingDataService data) = Create(("A", 30));
        ScreenerIndicatorEntry entry = Versus(Close(), ComparisonOperator.GreaterThan, Close(frame));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ScanAsync(service, Complete, entry, "A"));
        Assert.Empty(data.Requests);
    }

    [Fact]
    public async Task AnOffsetAboveTheConfiguredMaximumIsRefusedByBothScanFormsTheSameWay()
    {
        (ScreenerService service, RecordingDataService data) = Create(("A", 30));
        ScreenerIndicatorEntry entry = Versus(Close(TimeFrame.W1, offset: int.MaxValue), ComparisonOperator.GreaterThan, 0m);

        ArgumentOutOfRangeException plain = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => service.ScreenAsync(new List<string> { "A" }, entry, TimeFrame.D1, new Progress<int>(), CancellationToken.None));
        ArgumentOutOfRangeException sides = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ScanAsync(service, Complete, entry, "A"));

        Assert.Equal(plain.ParamName, sides.ParamName);
        Assert.Equal(plain.ActualValue, sides.ActualValue);
        Assert.Empty(data.Requests);
    }

    [Fact]
    public async Task ACancelledScanThrowsAndInvalidArgumentsAreRefused()
    {
        (ScreenerService service, _) = Create(("A", 30));
        ScreenerIndicatorEntry entry = Versus(Close(TimeFrame.W1), ComparisonOperator.GreaterThan, 0m);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.ScreenWithSideFramesAsync(new List<string> { "A" }, entry, Complete, new Progress<int>(), cancelled.Token));
        await Assert.ThrowsAsync<ArgumentNullException>(() => service.ScreenWithSideFramesAsync(null!, entry, Complete, new Progress<int>(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => service.ScreenWithSideFramesAsync(new List<string>(), null!, Complete, new Progress<int>(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => service.ScreenWithSideFramesAsync(new List<string>(), entry, null!, new Progress<int>(), CancellationToken.None));
    }

    [Fact]
    public async Task AServiceThatDoesNotScanSidesOnTheirFramesRefusesInsteadOfScanningDailyBars()
    {
        IScreenerService minimal = new RefusingScreener();

        await Assert.ThrowsAsync<NotSupportedException>(() => minimal.ScreenWithSideFramesAsync(
            new List<string> { "A" }, Versus(Close(), ComparisonOperator.GreaterThan, 0m), Complete, new Progress<int>(), CancellationToken.None));
    }

    [Fact]
    public async Task TheBarSetLoaderRefusesAFrameTheDataLayerDoesNotDefine()
    {
        (_, RecordingDataService data) = Create(("A", 30));
        var bars = new ScreenerBarsDataService(data, Complete);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => bars.LoadBarSetAsync("A", new[] { TimeFrame.H1 }, 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => bars.LoadBarSetAsync("A", new[] { TimeFrame.D1 }, -1));
    }
}
