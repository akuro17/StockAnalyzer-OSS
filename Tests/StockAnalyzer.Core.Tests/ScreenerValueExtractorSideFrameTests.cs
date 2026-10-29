using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Screener;
using StockAnalyzer.Core.Tests.TestHelpers;
using Xunit;
using static StockAnalyzer.Core.Tests.TestHelpers.ScreenerSideFrameFixtures;

namespace StockAnalyzer.Core.Tests;

/// <summary>
/// <see cref="ScreenerValueExtractor.ExtractValueNullableOnSideFrame"/>: the value of a side that is given the bars of its own time frame. The daily-only rule of the
/// existing extractor methods (pinned by ScreenerSprint1Tests) stays; this method is the new function beside it.
/// </summary>
public sealed class ScreenerValueExtractorSideFrameTests
{
    private static IReadOnlyList<CoreCandleData> Weekly() => BarSet().GetCore(TimeFrame.W1);

    [Fact]
    public void AWeeklyIndicatorSideHasAValueOnTheWeeklyBars()
    {
        var extractor = new ScreenerValueExtractor();

        decimal? value = extractor.ExtractValueNullableOnSideFrame(Sma(2, TimeFrame.W1), Weekly(), default, null);

        Assert.Equal(123.5m, value); // (120 + 127) / 2
        Assert.DoesNotContain(extractor.Faults.TakeSnapshotAndReset(), fault => fault.Count > 0);
    }

    [Fact]
    public void TheExistingMethodStillGivesNoValueForTheSameSideBecauseItIsDailyOnly()
    {
        var extractor = new ScreenerValueExtractor();

        Assert.Null(extractor.ExtractValueNullable(Sma(2, TimeFrame.W1), Weekly(), default));
        Assert.Contains(extractor.Faults.TakeSnapshotAndReset(), fault => fault.Count > 0);
    }

    [Fact]
    public void ADailySideGivesTheSameValueThroughBothMethods()
    {
        var extractor = new ScreenerValueExtractor();
        IReadOnlyList<CoreCandleData> daily = BarSet().GetCore(TimeFrame.D1);

        foreach (ScreenerIndicatorSideConfig side in new[] { Sma(5), Sma(5, offset: 3), Close(), Close(offset: 2) })
        {
            Assert.Equal(extractor.ExtractValueNullable(side, daily, default), extractor.ExtractValueNullableOnSideFrame(side, daily, default, null));
        }
    }

    [Fact]
    public void ThePriceAndBarValueColumnSidesReadTheBarsTheyAreGiven()
    {
        var extractor = new ScreenerValueExtractor();
        IReadOnlyList<CoreCandleData> weekly = Weekly();

        Assert.Equal(127m, extractor.ExtractValueNullableOnSideFrame(Close(TimeFrame.W1), weekly, default, null));
        Assert.Equal(120m, extractor.ExtractValueNullableOnSideFrame(Close(TimeFrame.W1, offset: 1), weekly, default, null));
        Assert.Equal(127m, extractor.ExtractValueNullableOnSideFrame(CloseColumn(TimeFrame.W1), weekly, default, null));
    }

    [Fact]
    public void TheOffsetCountsBarsOfTheSidesOwnFrame()
    {
        var extractor = new ScreenerValueExtractor();
        IReadOnlyList<CoreCandleData> weekly = Weekly(); // four bars

        Assert.Equal(106m, extractor.ExtractValueNullableOnSideFrame(Close(TimeFrame.W1, offset: 3), weekly, default, null));
        Assert.Null(extractor.ExtractValueNullableOnSideFrame(Close(TimeFrame.W1, offset: 4), weekly, default, null)); // before the first bar: no substitute
        Assert.Null(extractor.ExtractValueNullableOnSideFrame(Close(TimeFrame.W1, offset: -1), weekly, default, null)); // a future bar
    }

    [Fact]
    public void AnIndicatorThatComparesTwoTickersHasNoValueOnAnotherFrameButHasOneOnD1()
    {
        var extractor = new ScreenerValueExtractor();
        IReadOnlyList<CoreCandleData> daily = BarSet().GetCore(TimeFrame.D1);
        // A second series with its own shape (a repeating wobble on a different slope) so the correlation is defined.
        List<CoreCandleData?> other = daily.Select((bar, i) => (CoreCandleData?)bar with { Close = 50m + (i % 4) * 3m + i * 0.5m }).ToList();
        var secondary = new ScreenerSecondaryCandles(new Dictionary<string, IReadOnlyList<CoreCandleData?>>(StringComparer.OrdinalIgnoreCase) { ["OTHER"] = other });

        ScreenerIndicatorSideConfig Correlation(TimeFrame frame) => new()
        {
            IndicatorType = IndicatorType.Correlation,
            OutputName = "Main",
            Parameters = new Dictionary<string, object> { ["ComparisonSymbol"] = "OTHER", ["Period"] = 10 },
            TimeFrame = frame,
        };

        Assert.NotNull(extractor.ExtractValueNullableOnSideFrame(Correlation(TimeFrame.D1), daily, default, secondary));
        Assert.Null(extractor.ExtractValueNullableOnSideFrame(Correlation(TimeFrame.W1), daily, default, secondary));
        Assert.Contains(extractor.Faults.TakeSnapshotAndReset(), fault => fault.Count > 0);
    }

    [Fact]
    public void AnchoredVwapHasNoValueOnAnotherFrame()
    {
        var extractor = new ScreenerValueExtractor();
        var side = new ScreenerIndicatorSideConfig { IndicatorType = IndicatorType.AnchoredVWAP, OutputName = "Main", TimeFrame = TimeFrame.W1 };

        Assert.Null(extractor.ExtractValueNullableOnSideFrame(side, Weekly(), default, null));
        Assert.Contains(extractor.Faults.TakeSnapshotAndReset(), fault => fault.Count > 0);
    }

    [Fact]
    public void ANullSideOrAnEmptyListGivesNoValue()
    {
        var extractor = new ScreenerValueExtractor();

        Assert.Null(extractor.ExtractValueNullableOnSideFrame(null, Weekly(), default, null));
        Assert.Null(extractor.ExtractValueNullableOnSideFrame(Close(TimeFrame.W1), Array.Empty<CoreCandleData>(), default, null));
    }
}
