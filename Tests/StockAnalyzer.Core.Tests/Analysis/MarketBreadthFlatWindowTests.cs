using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Core.Tests.Analysis;

/// <summary>Plan task T8 (owner decision D-10 = A): a window in which every close is equal has no new high and no new low.</summary>
public class MarketBreadthFlatWindowTests
{
    private const int Bars = 25;
    private const int HighLowBars = 5;
    private static readonly DateTime FirstDay = new(2024, 1, 1);

    private static List<CandleData> Series(Func<int, decimal> close)
        => Enumerable.Range(0, Bars).Select(i => new CandleData(FirstDay.AddDays(i), close(i), close(i), close(i), close(i), 1000)).ToList();

    private static MarketBreadthResult Calculate(params List<CandleData>[] symbols)
    {
        var universe = new Dictionary<string, IReadOnlyList<CandleData>>();
        for (int i = 0; i < symbols.Length; i++)
        {
            universe[$"S{i}"] = symbols[i];
        }

        return MarketBreadthCalculator.Calculate(universe, new MarketBreadthParameters { HighLowBars = HighLowBars, AdRatioBars = 2 });
    }

    [Fact]
    public void AFlatUniverse_HasNoNewHighsAndNoNewLows()
    {
        MarketBreadthResult result = Calculate(Series(_ => 100m), Series(_ => 50m), Series(_ => 20m));

        for (int t = HighLowBars - 1; t < Bars; t++)
        {
            Assert.Equal(0, result.Series.NewHighs[t]);
            Assert.Equal(0, result.Series.NewLows[t]);
            Assert.Equal(0, result.Series.NetNewHighs[t]);
        }
    }

    [Fact]
    public void AFlatSymbolIsIgnored_WhileARisingOneStillMakesNewHighs()
    {
        MarketBreadthResult result = Calculate(Series(_ => 100m), Series(i => 50m + i));

        for (int t = HighLowBars - 1; t < Bars; t++)
        {
            Assert.Equal(1, result.Series.NewHighs[t]);
            Assert.Equal(0, result.Series.NewLows[t]);
        }
    }

    [Fact]
    public void ATieWithTheWindowMaximumInAWindowThatIsNotFlat_StillCountsAsNewHigh()
    {
        // Closes 10, 12, 12, 12, 12: the last window [10,12,12,12,12] is not flat and the close equals its maximum (documented tie rule, unchanged).
        List<CandleData> tie = Series(i => i == 0 ? 10m : 12m);

        MarketBreadthResult result = Calculate(tie);

        Assert.Equal(1, result.Series.NewHighs[HighLowBars - 1]);
        Assert.Equal(0, result.Series.NewLows[HighLowBars - 1]);
        // Once the window holds only 12s it is flat again.
        Assert.Equal(0, result.Series.NewHighs[HighLowBars]);
        Assert.Equal(0, result.Series.NewLows[HighLowBars]);
    }
}
