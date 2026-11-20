using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Tests.BlockChart;

/// <summary>Real <see cref="CoreCandleData"/> records for the block chart overlay tests (no mocks). Prices are decimal literals / decimal arithmetic only.</summary>
internal static class BlockChartTestData
{
    public static readonly DateTime Start = new(2024, 1, 1);

    public static CoreCandleData Bar(int day, decimal open, decimal high, decimal low, decimal close)
        => new(Start.AddDays(day), open, high, low, close, 1000);

    /// <summary>A bar whose open, high and low all equal the close (a close-only price; High == Low == Close is valid).</summary>
    public static CoreCandleData Flat(int day, decimal close) => Bar(day, close, close, close, close);

    public static List<CoreCandleData> FromCloses(params decimal[] closes)
    {
        var list = new List<CoreCandleData>(closes.Length);
        for (int i = 0; i < closes.Length; i++) list.Add(Flat(i, closes[i]));
        return list;
    }

    /// <summary>
    /// A reproducible price path with trends, reversals, quiet stretches and occasional gaps, and a real high-low range around every close (so ATR and the P&amp;F high/low rules are exercised).
    /// Same seed, same series. Prices stay positive.
    /// </summary>
    public static List<CoreCandleData> RandomWalk(int seed, int count, decimal start, decimal volatility)
    {
        var rng = new Random(seed);
        var list = new List<CoreCandleData>(count);
        decimal previousClose = start;
        decimal drift = 0m;

        for (int i = 0; i < count; i++)
        {
            if (i % 17 == 0) drift = (rng.Next(-3, 4)) * volatility * 0.15m;

            decimal step = (rng.Next(-100, 101) / 100m) * volatility + drift;
            if (rng.Next(0, 40) == 0) step *= 6m; // an occasional gap

            decimal open = previousClose;
            decimal close = Math.Max(0.5m, Math.Round(open + step, 2));
            decimal high = Math.Max(open, close) + Math.Round(rng.Next(0, 100) / 100m * volatility * 0.5m, 2);
            decimal low = Math.Max(0.01m, Math.Min(open, close) - Math.Round(rng.Next(0, 100) / 100m * volatility * 0.5m, 2));

            list.Add(Bar(i, open, high, low, close));
            previousClose = close;
        }

        return list;
    }
}
