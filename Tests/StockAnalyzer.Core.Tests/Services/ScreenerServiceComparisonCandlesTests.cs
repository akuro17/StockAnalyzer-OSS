using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Tests.TestHelpers;
using Xunit;
using static StockAnalyzer.Core.Tests.TestHelpers.ScreenerSideFrameFixtures;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// <see cref="IScreenerService.LoadComparisonCandlesAsync"/> and <see cref="ScreenerSecondaryCandles.LoadForSpellingsAsync"/>: the comparison symbols' bars are read once per
/// instrument from the screener's own data service with the history the condition needs, every spelling of an instrument finds the same bars, an instrument without bars is absent.
/// </summary>
public sealed class ScreenerServiceComparisonCandlesTests
{
    private const int Days = 300;

    private static ScreenerIndicatorSideConfig Correlation(string symbol) => new()
    {
        IndicatorType = IndicatorType.Correlation,
        OutputName = IndicatorResult.MainSeriesName,
        Parameters = new Dictionary<string, object> { ["Period"] = 20, ["ComparisonSymbol"] = symbol },
    };

    private static ScreenerConditionTreeCondition Tree(params ScreenerIndicatorSideConfig[] sides)
        => new(Group(LogicalOperator.And, sides.Select(side => (IScreenerConditionNode)Leaf(Versus(side, ComparisonOperator.GreaterThan, 0m))).ToArray()));

    private static (ScreenerService Service, RecordingDataService Data) Service(params string[] symbols)
        => CreateService(symbols.Select(symbol => (symbol, Days)).ToArray());

    [Fact]
    public async Task TwoStoredSymbolsAreBothFound()
    {
        (ScreenerService service, _) = Service("SPY", "QQQ");

        IScreenerSecondaryCandles secondary = await service.LoadComparisonCandlesAsync(new[] { "SPY", "QQQ" }, TimeFrame.D1, Tree(Correlation("SPY")), CancellationToken.None);

        Assert.Equal(Days, secondary.Get("SPY")!.Count);
        Assert.Equal(Days, secondary.Get("QQQ")!.Count);
    }

    [Fact]
    public async Task ASymbolWithoutBarsIsAbsent()
    {
        (ScreenerService service, _) = Service("SPY");

        IScreenerSecondaryCandles secondary = await service.LoadComparisonCandlesAsync(new[] { "SPY", "ZZZZ" }, TimeFrame.D1, Tree(Correlation("SPY")), CancellationToken.None);

        Assert.NotNull(secondary.Get("SPY"));
        Assert.Null(secondary.Get("ZZZZ"));
    }

    [Fact]
    public async Task TheBarsAreReadWithTheHistoryTheConditionNeedsInTheGivenTimeFrame()
    {
        (ScreenerService service, RecordingDataService data) = Service("SPY");
        ScreenerConditionTreeCondition condition = Tree(Correlation("SPY"));

        await service.LoadComparisonCandlesAsync(new[] { "SPY" }, TimeFrame.D1, condition, CancellationToken.None);

        (string symbol, TimeFrame frame, int count) = Assert.Single(data.Requests);
        Assert.Equal("SPY", symbol);
        Assert.Equal(TimeFrame.D1, frame);
        Assert.Equal(ScreenerHistoryRequirements.GetRequiredCandleCount(condition), count);
        Assert.True(count > 0);
    }

    [Fact]
    public async Task ZeroMeansTheWholeHistoryWhenTheConditionNeedsIt()
    {
        (ScreenerService service, RecordingDataService data) = Service("SPY");
        var obv = new ScreenerIndicatorSideConfig { IndicatorType = IndicatorType.OBV, OutputName = IndicatorResult.MainSeriesName };
        ScreenerConditionTreeCondition condition = Tree(obv, Correlation("SPY"));
        Assert.Equal(0, ScreenerHistoryRequirements.GetRequiredCandleCount(condition));

        await service.LoadComparisonCandlesAsync(new[] { "SPY" }, TimeFrame.D1, condition, CancellationToken.None);

        Assert.Equal(0, Assert.Single(data.Requests).Count);
    }

    [Fact]
    public async Task ALookupIgnoresCase()
    {
        (ScreenerService service, _) = Service("SPY");

        IScreenerSecondaryCandles secondary = await service.LoadComparisonCandlesAsync(new[] { "SPY" }, TimeFrame.D1, Tree(Correlation("SPY")), CancellationToken.None);

        Assert.Same(secondary.Get("SPY"), secondary.Get("spy"));
    }

    [Fact]
    public void AServiceThatDoesNotImplementItRefusesInsteadOfAnsweringWithNothing()
    {
        IScreenerService refusing = new RefusingScreener();

        Task Call() => refusing.LoadComparisonCandlesAsync(new[] { "SPY" }, TimeFrame.D1, Tree(Correlation("SPY")), CancellationToken.None);

        // The default member throws when it is called (it does not hand back a faulted task), so a caller cannot mistake it for an empty answer.
        Assert.Throws<NotSupportedException>((Action)(() => _ = Call()));
    }

    [Fact]
    public async Task NullSpellingsAreRefused()
    {
        (ScreenerService service, _) = Service("SPY");

        await Assert.ThrowsAsync<ArgumentNullException>(() => service.LoadComparisonCandlesAsync(null!, TimeFrame.D1, Tree(Correlation("SPY")), CancellationToken.None));
    }

    [Fact]
    public async Task NoSpellingsGiveAnEmptySetAndReadNothing()
    {
        (ScreenerService service, RecordingDataService data) = Service("SPY");

        IScreenerSecondaryCandles secondary = await service.LoadComparisonCandlesAsync(Array.Empty<string>(), TimeFrame.D1, Tree(Correlation("SPY")), CancellationToken.None);

        Assert.Null(secondary.Get("SPY"));
        Assert.Empty(data.Requests);
    }

    // ---- LoadForSpellingsAsync: every spelling of one instrument is one read and one list --------------------------------------------

    [Theory]
    [InlineData("7203.T", "7203-T")]
    [InlineData("^N225", "N225")]
    public async Task TwoSpellingsOfOneInstrumentAreReadOnceAndFindTheSameBars(string first, string second)
    {
        (ScreenerService service, RecordingDataService data) = Service(first);

        IScreenerSecondaryCandles secondary = await service.LoadComparisonCandlesAsync(new[] { first, second }, TimeFrame.D1, Tree(Correlation(first)), CancellationToken.None);

        Assert.Equal(first, Assert.Single(data.Requests).Symbol);
        Assert.NotNull(secondary.Get(first));
        Assert.Same(secondary.Get(first), secondary.Get(second));
    }

    [Fact]
    public async Task AnInstrumentWithoutBarsRegistersNoSpelling()
    {
        (ScreenerService service, _) = Service("SPY");

        IScreenerSecondaryCandles secondary = await service.LoadComparisonCandlesAsync(new[] { "7203.T", "7203-T", "SPY" }, TimeFrame.D1, Tree(Correlation("SPY")), CancellationToken.None);

        Assert.Null(secondary.Get("7203.T"));
        Assert.Null(secondary.Get("7203-T"));
        Assert.NotNull(secondary.Get("SPY"));
    }

    [Fact]
    public async Task DifferentInstrumentsAreReadSeparatelyAndBlankSpellingsAreIgnored()
    {
        (ScreenerService service, RecordingDataService data) = Service("SPY", "QQQ");

        IScreenerSecondaryCandles secondary = await service.LoadComparisonCandlesAsync(new[] { "SPY", " ", "", "QQQ", "spy" }, TimeFrame.D1, Tree(Correlation("SPY")), CancellationToken.None);

        Assert.Equal(new[] { "QQQ", "SPY" }, data.Requests.Select(request => request.Symbol).OrderBy(symbol => symbol, StringComparer.Ordinal).ToArray());
        Assert.NotNull(secondary.Get("SPY"));
        Assert.NotNull(secondary.Get("QQQ"));
        Assert.NotSame(secondary.Get("SPY"), secondary.Get("QQQ"));
    }

    [Fact]
    public async Task TheSpellingLoaderCanBeCalledWithoutAServiceAndChecksItsArguments()
    {
        (_, RecordingDataService data) = Service("SPY");

        ScreenerSecondaryCandles secondary = await ScreenerSecondaryCandles.LoadForSpellingsAsync(data, new[] { "SPY" }, TimeFrame.D1, 50, CancellationToken.None);

        Assert.Equal(50, Assert.Single(data.Requests).Count);
        Assert.NotNull(secondary.Get("SPY"));
        await Assert.ThrowsAsync<ArgumentNullException>(() => ScreenerSecondaryCandles.LoadForSpellingsAsync(null!, new[] { "SPY" }, TimeFrame.D1, 50, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => ScreenerSecondaryCandles.LoadForSpellingsAsync(data, null!, TimeFrame.D1, 50, CancellationToken.None));
    }

    [Fact]
    public async Task ACancelledTokenStopsTheRead()
    {
        (ScreenerService service, _) = Service("SPY");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.LoadComparisonCandlesAsync(new[] { "SPY" }, TimeFrame.D1, Tree(Correlation("SPY")), cts.Token));
    }
}
