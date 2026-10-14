using System;
using System.Linq;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Tests.TestHelpers;

/// <summary>
/// Daily candles that swing fast around a slow uptrend, so that every chart type that is not a time series (Renko, Point and Figure, Three Line Break, Kagi)
/// converts them into many columns.
/// </summary>
public static class SwingingCandles
{
    private const double SwingDivisor = 1.5;
    private const decimal SwingAmplitude = 6m;
    private const decimal TrendPerCandle = 0.05m;
    private const decimal BasePrice = 100m;

    public static CoreCandleData[] Create(int count = 400) => Enumerable.Range(0, count)
        .Select(i =>
        {
            decimal close = BasePrice + SwingAmplitude * (decimal)Math.Sin(i / SwingDivisor) + i * TrendPerCandle;
            return new CoreCandleData(new DateTime(2023, 1, 1).AddDays(i), close - 0.5m, close + 1.5m, close - 1.5m, close, 1000 + i);
        })
        .ToArray();
}
