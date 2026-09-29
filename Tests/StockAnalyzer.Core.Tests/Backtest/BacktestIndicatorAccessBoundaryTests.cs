#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Services.Backtest.Engine;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest;

/// <summary>
/// Fix T3 (the P1 correctness plan, finding R4): the indicator series are precomputed
/// over the whole bar array, so the <see cref="IndicatorSeriesSet"/> a strategy receives through <see cref="StrategyContext"/> must
/// refuse indexes past the bar the context belongs to - including from a context the strategy kept after the run moved on.
/// </summary>
public class BacktestIndicatorAccessBoundaryTests
{
    private const string Key = "series";
    private static readonly DateTime Bar0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static IndicatorSeriesSet UnboundedSet(params decimal?[] values)
        => new(new Dictionary<string, ImmutableArray<decimal?>> { [Key] = values.ToImmutableArray() });

    [Fact]
    public void UnboundedSet_KeepsLegacyBehavior_AnyInRangeIndexIsReadable()
    {
        IndicatorSeriesSet set = UnboundedSet(10m, 11m, 12m, 13m);

        Assert.Equal(13m, set.ValueAt(Key, 3));
        Assert.Null(set.ValueAt(Key, 4));
        Assert.Null(set.ValueAt(Key, -1));
    }

    [Fact]
    public void BoundedView_ReturnsOriginalValuesUpToItsBar_AndNullBeyond()
    {
        IndicatorSeriesSet view = UnboundedSet(10m, null, 12m, 13m, 14m).BoundedTo(2);

        Assert.Equal(12m, view.ValueAt(Key, 2));   // current bar
        Assert.Null(view.ValueAt(Key, 1));         // previous bar: original null (warm-up) is preserved
        Assert.Equal(10m, view.ValueAt(Key, 0));   // older bar
        Assert.Null(view.ValueAt(Key, 3));         // future + 1
        Assert.Null(view.ValueAt(Key, 4));         // series tail that exists but lies in the future
        Assert.Null(view.ValueAt(Key, -1));
        Assert.Null(view.ValueAt(Key, int.MaxValue));
    }

    [Fact]
    public void BoundedView_UnknownKey_StillThrowsKeyNotFound_EvenForAnOutOfBoundIndex()
    {
        IndicatorSeriesSet view = UnboundedSet(10m, 11m).BoundedTo(0);

        Assert.Throws<KeyNotFoundException>(() => view.ValueAt("undeclared", 0));
        Assert.Throws<KeyNotFoundException>(() => view.ValueAt("undeclared", 99));
    }

    [Fact]
    public void CreateBarBoundedViews_GivesEachBarItsOwnImmutableBound()
    {
        IndicatorSeriesSet set = UnboundedSet(10m, 11m, 12m);

        IndicatorSeriesSet[] views = set.CreateBarBoundedViews(3);

        Assert.Equal(3, views.Length);
        Assert.Null(views[0].ValueAt(Key, 1));
        Assert.Equal(11m, views[1].ValueAt(Key, 1));
        Assert.Null(views[1].ValueAt(Key, 2));
        Assert.Equal(12m, views[2].ValueAt(Key, 2));
    }

    /// <summary>Probes every offset around the current bar on every bar and keeps the FIRST context it was given.</summary>
    private sealed class FutureProbingStrategy : IBacktestStrategy
    {
        public string Name => "FutureProbing";
        public List<(int Bar, decimal? Current, decimal? Previous, decimal? NextBar, decimal? FarFuture, decimal? Negative)> Reads { get; } = new();
        public IndicatorSeriesSet? FirstContextIndicators { get; private set; }

        public IReadOnlyList<StrategyIndicatorRequest> GetRequiredIndicators()
            => new[] { new StrategyIndicatorRequest(Key, IndicatorType.SMA, new CoreSmaParameter { Period = 2 }) };

        public StrategyOrderRequest? Evaluate(StrategyContext context)
        {
            FirstContextIndicators ??= context.Indicators;
            int i = context.BarIndex;
            Reads.Add((i,
                context.Indicators.ValueAt(Key, i),
                context.Indicators.ValueAt(Key, i - 1),
                context.Indicators.ValueAt(Key, i + 1),
                context.Indicators.ValueAt(Key, i + 100),
                context.Indicators.ValueAt(Key, -1)));
            return null;
        }
    }

    [Fact]
    public void Run_StrategyCannotReadIndicatorValuesOfLaterBars_EvenFromARetainedContext()
    {
        const int barCount = 6;
        var bars = ImmutableArray.CreateBuilder<CandleData>(barCount);
        for (int i = 0; i < barCount; i++)
        {
            decimal price = 100m + i;
            bars.Add(new CandleData(Bar0.AddDays(i), price, price, price, price, 1000));
        }
        var input = new BacktestInput(bars.MoveToImmutable(), "TEST", TimeFrame.D1, BacktestInput.CurrentDataVersion, Bar0, Bar0.AddDays(barCount + 1), 0, 0);
        var config = new BacktestConfiguration { InitialCapital = 1000m, SizingModel = PositionSizingModel.FixedQuantity, SizingParameter = 1m };
        var strategy = new FutureProbingStrategy();

        new BacktestEngine(new IndicatorFactory()).Run(input, config, strategy);

        Assert.Equal(barCount, strategy.Reads.Count);
        foreach (var read in strategy.Reads)
        {
            Assert.Null(read.NextBar);
            Assert.Null(read.FarFuture);
            Assert.Null(read.Negative);
        }

        // SMA(2) of 100,101,...: value at bar i (i >= 1) is the mean of bars i-1 and i.
        Assert.Null(strategy.Reads[0].Current);
        Assert.Equal(100.5m, strategy.Reads[1].Current);
        Assert.Equal(104.5m, strategy.Reads[5].Current);
        Assert.Equal(103.5m, strategy.Reads[5].Previous);

        // The context handed out at bar 0 stays bound to bar 0 after the run has moved on.
        Assert.Null(strategy.FirstContextIndicators!.ValueAt(Key, barCount - 1));
        Assert.Null(strategy.FirstContextIndicators.ValueAt(Key, 1));
    }
}
