using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Chart;
using Xunit;

namespace StockAnalyzer.Core.Tests.BlockChart;

/// <summary>The state an existing chart-type calculator ends in after the bars it was given (the oracle of the prefix parity tests). Update is null for the charts without it.</summary>
internal readonly record struct OracleState(decimal Close, decimal Reversal, decimal? Update, bool IsUp);

internal static class BlockChartParity
{
    /// <summary>
    /// Proves, for EVERY bar j, that the overlay equals the chart type: from the anchor on, <c>actual[j]</c> must be the oracle run on bars 0..j (null while the oracle has no block); before the anchor it must be
    /// null. This is the permanent guard against drift between an incremental engine and its batch calculator.
    /// </summary>
    public static void AssertMatches(
        IReadOnlyList<CoreCandleData> candles,
        BlockChartOverlaySeries actual,
        int anchorOrdinal,
        Func<IReadOnlyList<CoreCandleData>, OracleState?> oracle,
        string label,
        bool requireRichComparison = false)
    {
        Assert.Equal(candles.Count, actual.Close.Count);
        Assert.Equal(candles.Count, actual.Reversal.Count);
        Assert.Equal(candles.Count, actual.IsUpTrend.Count);
        if (actual.Update is not null) Assert.Equal(candles.Count, actual.Update.Count);

        int compared = 0, ups = 0, downs = 0;
        for (int j = 0; j < candles.Count; j++)
        {
            OracleState? expected = j < anchorOrdinal ? null : oracle(candles.Take(j + 1).ToList());
            if (expected is null)
            {
                Assert.True(
                    actual.Close[j] is null && actual.Reversal[j] is null && actual.IsUpTrend[j] is null && (actual.Update is null || actual.Update[j] is null),
                    $"{label}: bar {j} must be null (before the anchor or no block yet)");
                continue;
            }

            compared++;
            if (expected.Value.IsUp) ups++; else downs++;
            Assert.True(actual.Close[j] == expected.Value.Close, $"{label}: Close at {j}: {actual.Close[j]} vs {expected.Value.Close}");
            Assert.True(actual.Reversal[j] == expected.Value.Reversal, $"{label}: Reversal at {j}: {actual.Reversal[j]} vs {expected.Value.Reversal}");
            Assert.True(actual.IsUpTrend[j] == (expected.Value.IsUp ? 1m : 0m), $"{label}: IsUpTrend at {j}");
            if (actual.Update is null) Assert.Null(expected.Value.Update);
            else Assert.True(actual.Update[j] == expected.Value.Update, $"{label}: Update at {j}: {actual.Update[j]} vs {expected.Value.Update}");
        }

        // A parity test that only compares "null with null" proves nothing: the long random series must exercise many defined states in BOTH directions.
        if (requireRichComparison)
        {
            Assert.True(compared >= MinimumComparedBars, $"{label}: only {compared} bars had a state to compare");
            Assert.True(ups > 0 && downs > 0, $"{label}: up {ups} / down {downs} states compared; both directions must occur");
        }
    }

    private const int MinimumComparedBars = 20;
}
