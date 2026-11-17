using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

/// <summary>Single definition of the deterministic rising candle series used by the drawing value-row tests.</summary>
internal static class DrawingTestCandles
{
    /// <summary>First timestamp of the series; each candle is one hour after the previous one.</summary>
    public static readonly DateTime FirstTimestamp = new(2024, 1, 1);

    public static List<CoreCandleData> CreateCandles(int count, decimal start = 100m, decimal step = 1.5m)
    {
        var list = new List<CoreCandleData>(count);
        for (int i = 0; i < count; i++)
        {
            decimal p = start + i * step + (i % 3 == 0 ? 1m : -0.5m);
            list.Add(new CoreCandleData(FirstTimestamp.AddHours(i), p - 1m, p + 2m, p - 2m, p, 1000));
        }

        return list;
    }
}
