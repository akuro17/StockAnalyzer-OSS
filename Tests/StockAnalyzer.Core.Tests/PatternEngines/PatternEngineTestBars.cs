using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services.Analysis.PatternEngines;
using StockAnalyzer.Core.Tests.Backtest.Verification;

namespace StockAnalyzer.Core.Tests.PatternEngines;

/// <summary>Real <see cref="CandleData"/> builders shared by the pattern engine tests (no mocks, every bar valid unless <see cref="Corrupt"/> is called).</summary>
internal static class PatternEngineTestBars
{
    /// <summary>Starting price of the seeded walks.</summary>
    public const decimal WalkStart = 100m;

    public static CandleData[] Walk(int seed, int count) => SyntheticBars.SeededRandomWalk(seed, count, WalkStart).ToArray();

    /// <summary>One bar with an explicit range; the volume is the shared default unless given.</summary>
    public static CandleData Bar(int index, decimal open, decimal high, decimal low, decimal close, long? volume = null)
    {
        CandleData bar = SyntheticBars.Bar(index, open, high, low, close);
        return volume.HasValue ? bar with { Volume = volume.Value } : bar;
    }

    /// <summary>A steady move: every bar opens where the previous one closed, moves <paramref name="step"/> and has wicks of <paramref name="wick"/> on both sides (true range = |step| + 2 * wick).</summary>
    public static CandleData[] Trend(int count, decimal start, decimal step, decimal wick)
    {
        var bars = new CandleData[count];
        decimal open = start;
        for (int i = 0; i < count; i++)
        {
            decimal close = open + step;
            bars[i] = Bar(i, open, Math.Max(open, close) + wick, Math.Min(open, close) - wick, close);
            open = close;
        }

        return bars;
    }

    /// <summary>Makes bar <paramref name="index"/> OHLC-inconsistent (open = 0) while leaving an extreme high behind, so a function that read it would notice.</summary>
    public static CandleData[] Corrupt(IReadOnlyList<CandleData> bars, int index)
    {
        CandleData[] copy = bars.ToArray();
        copy[index] = copy[index] with { Open = 0m, High = copy[index].High * 1000m };
        return copy;
    }

    public static PatternSeriesContext Context(IReadOnlyList<CandleData> bars) => new(bars, bars.Count);
}
