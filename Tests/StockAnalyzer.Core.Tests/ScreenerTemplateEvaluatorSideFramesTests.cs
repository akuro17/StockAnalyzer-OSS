using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Models.Templates;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Screener;
using Xunit;
using static StockAnalyzer.Core.Tests.TestHelpers.ScreenerSideFrameFixtures;

namespace StockAnalyzer.Core.Tests;

/// <summary>
/// <see cref="IScreenerTemplateEvaluator.EvaluateWithSideFramesAsync"/>: the entries of a template combined exactly as <c>EvaluateAsync</c> combines them, each entry scanned with
/// every side on its own frame. Symbol A has 30 daily bars (last complete week closes 127), B has 25 (last complete week closes 120); see <see cref="Daily"/>.
/// </summary>
public sealed class ScreenerTemplateEvaluatorSideFramesTests
{
    private static ScreenerIndicatorTemplate Template(params ScreenerIndicatorEntry[] entries)
    {
        var template = new ScreenerIndicatorTemplate { Id = Guid.NewGuid(), Name = "t" };
        template.SetEntries(entries);
        return template;
    }

    private static (ScreenerTemplateEvaluator Evaluator, RecordingDataService Data) Create()
    {
        (ScreenerService service, RecordingDataService data) = CreateService(("A", 30), ("B", 25));
        return (new ScreenerTemplateEvaluator(service), data);
    }

    private static readonly string[] Symbols = { "A", "B" };
    private static readonly ScreenerBarSelection Complete = new(null, true);

    private static async Task<string[]> Run(ScreenerTemplateEvaluator evaluator, ScreenerIndicatorTemplate template)
        => (await evaluator.EvaluateWithSideFramesAsync(Symbols, template, Complete, new Progress<int>(), CancellationToken.None)).OrderBy(symbol => symbol, StringComparer.Ordinal).ToArray();

    [Fact]
    public async Task AnAndOfADailyAndAWeeklyEntryKeepsTheSymbolsThatMeetBoth()
    {
        (ScreenerTemplateEvaluator evaluator, _) = Create();
        ScreenerIndicatorTemplate template = Template(
            Versus(Close(), ComparisonOperator.GreaterThan, 100m),
            Versus(Close(TimeFrame.W1), ComparisonOperator.Equal, 127m));

        Assert.Equal(new[] { "A" }, await Run(evaluator, template));
    }

    [Fact]
    public async Task AnOrIsDecidedByTheLogicalOperatorOfThePrecedingEntry()
    {
        (ScreenerTemplateEvaluator evaluator, _) = Create();
        ScreenerIndicatorEntry first = Versus(Close(TimeFrame.W1), ComparisonOperator.Equal, 120m);
        first.LogicalOperator = LogicalOperator.Or;
        ScreenerIndicatorEntry second = Versus(Close(TimeFrame.W1), ComparisonOperator.Equal, 127m);

        Assert.Equal(new[] { "A", "B" }, await Run(evaluator, Template(first, second)));

        first.LogicalOperator = LogicalOperator.And;
        Assert.Empty(await Run(evaluator, Template(first, second)));
    }

    [Fact]
    public async Task ADisabledEntryIsIgnoredAndNoEnabledEntryIsAnEmptyAnswerWithoutAScan()
    {
        (ScreenerTemplateEvaluator evaluator, RecordingDataService data) = Create();

        Assert.Equal(new[] { "A" }, await Run(evaluator, Template(
            Versus(Close(TimeFrame.W1), ComparisonOperator.Equal, 127m),
            Versus(Close(TimeFrame.W1), ComparisonOperator.Equal, 0m, enabled: false))));

        data.Requests.Clear();
        Assert.Empty(await Run(evaluator, Template(Versus(Close(TimeFrame.W1), ComparisonOperator.Equal, 127m, enabled: false))));
        Assert.Empty(await Run(evaluator, Template()));
        Assert.Empty(data.Requests);
    }

    [Fact]
    public async Task ADailyOnlyTemplateGivesTheAnswerOfThePlainEvaluation()
    {
        (ScreenerTemplateEvaluator evaluator, _) = Create();
        ScreenerIndicatorTemplate template = Template(
            Versus(Close(), ComparisonOperator.GreaterThan, 110m),
            Versus(Close(offset: 2), ComparisonOperator.LessThan, Sma(3))); // c-2 < the mean of c-2, c-1, c

        HashSet<string> plain = await evaluator.EvaluateAsync(Symbols, template, TimeFrame.D1, new Progress<int>(), CancellationToken.None);

        Assert.NotEmpty(plain);
        Assert.Equal(plain.OrderBy(symbol => symbol, StringComparer.Ordinal), await Run(evaluator, template));
    }

    [Fact]
    public async Task AnEvaluatorThatCannotReadSidesOnTheirFramesRefuses()
    {
        IScreenerTemplateEvaluator plainOnly = new PlainOnlyEvaluator();

        await Assert.ThrowsAsync<NotSupportedException>(() => plainOnly.EvaluateWithSideFramesAsync(Symbols, Template(), Complete, null, CancellationToken.None));
        await Assert.ThrowsAsync<NotSupportedException>(() => plainOnly.EvaluateWithSideFramesReportAsync(Symbols, Template(), Complete, null, CancellationToken.None));
    }

    // ---- the report (matched symbols + what each frame did) ----------------------------------------------------------------------------------

    [Fact]
    public async Task TheReportHasTheMatchedSymbolsOfTheSetEvaluationAndOneMergedReportPerFrameTheEntriesRead()
    {
        (ScreenerTemplateEvaluator evaluator, _) = Create();
        ScreenerIndicatorTemplate template = Template(
            Versus(Close(), ComparisonOperator.GreaterThan, 100m),
            Versus(Close(TimeFrame.W1), ComparisonOperator.GreaterThan, 100m),
            Versus(Close(TimeFrame.W1), ComparisonOperator.Equal, 127m));

        ScreenerSideFramesResult report = await evaluator.EvaluateWithSideFramesReportAsync(Symbols, template, Complete, new Progress<int>(), CancellationToken.None);

        Assert.Equal(await Run(evaluator, template), report.Matched.OrderBy(symbol => symbol, StringComparer.Ordinal));
        ScreenerFrameReport weekly = Assert.Single(report.Frames);
        Assert.Equal(TimeFrame.W1, weekly.Frame);
        Assert.Equal(2, weekly.SkippedPartialBarCount); // both symbols end in a partial week; two entries read the frame, the symbols are counted once
        Assert.Empty(weekly.PartialLastBarPeriodStarts);
        Assert.Empty(weekly.SymbolsWithoutBars);
        Assert.Empty(report.SymbolsWithoutDailyBars);
    }

    [Fact]
    public async Task WithCompleteBarsOnlyFalseThePartialPeriodsOfAllSymbolsAreUnitedOnceAndInOrder()
    {
        (ScreenerTemplateEvaluator evaluator, _) = Create();
        ScreenerIndicatorTemplate template = Template(
            Versus(Close(TimeFrame.W1), ComparisonOperator.GreaterThan, 100m),
            Versus(Close(TimeFrame.W1), ComparisonOperator.LessThan, 200m));

        ScreenerSideFramesResult report = await evaluator.EvaluateWithSideFramesReportAsync(Symbols, template, new ScreenerBarSelection(null, false), new Progress<int>(), CancellationToken.None);

        ScreenerFrameReport weekly = Assert.Single(report.Frames);
        Assert.Equal(0, weekly.SkippedPartialBarCount);
        // A's last week starts Monday 2024-01-29 (30 bars), B's Monday 2024-01-22 (25 bars): each once, ascending.
        Assert.Equal(new[] { new DateOnly(2024, 1, 22), new DateOnly(2024, 1, 29) }, weekly.PartialLastBarPeriodStarts);
    }

    [Fact]
    public async Task TheReportOfADailyOnlyTemplateNamesNoFrame()
    {
        (ScreenerTemplateEvaluator evaluator, _) = Create();

        ScreenerSideFramesResult report = await evaluator.EvaluateWithSideFramesReportAsync(
            Symbols, Template(Versus(Close(), ComparisonOperator.GreaterThan, 110m)), Complete, new Progress<int>(), CancellationToken.None);

        Assert.NotEmpty(report.Matched);
        Assert.Empty(report.Frames);
    }

    [Fact]
    public async Task TheReportOfATemplateWithNoEnabledEntryIsEmpty()
    {
        (ScreenerTemplateEvaluator evaluator, RecordingDataService data) = Create();

        ScreenerSideFramesResult report = await evaluator.EvaluateWithSideFramesReportAsync(
            Symbols, Template(Versus(Close(TimeFrame.W1), ComparisonOperator.Equal, 127m, enabled: false)), Complete, new Progress<int>(), CancellationToken.None);

        Assert.Empty(report.Matched);
        Assert.Empty(report.Frames);
        Assert.Empty(data.Requests);
    }

    [Fact]
    public async Task ArgumentsAreChecked()
    {
        (ScreenerTemplateEvaluator evaluator, _) = Create();

        await Assert.ThrowsAsync<ArgumentNullException>(() => evaluator.EvaluateWithSideFramesAsync(null!, Template(), Complete, null, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => evaluator.EvaluateWithSideFramesAsync(Symbols, null!, Complete, null, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => evaluator.EvaluateWithSideFramesAsync(Symbols, Template(), null!, null, CancellationToken.None));
    }

    private sealed class PlainOnlyEvaluator : IScreenerTemplateEvaluator
    {
        public Task<HashSet<string>> EvaluateAsync(IReadOnlyList<string> symbols, ScreenerIndicatorTemplate template, TimeFrame timeFrame, IProgress<int>? progress, CancellationToken ct)
            => throw new InvalidOperationException();
    }
}
