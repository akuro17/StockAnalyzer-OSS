using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace StockAnalyzer.Avalonia.Services;

/// <summary>
/// Mock data provider for testing and development.
/// Generates random candle data similar to WPF MockDataProvider.
/// </summary>
public class MockDataService : IDataService
{
    private readonly Random _random = new();

    public Task<IReadOnlyList<CandleData>> LoadCandlesAsync(string symbol, TimeFrame timeFrame, int count = 100)
    {
        var candles = new List<CandleData>();
        var now = DateTime.Now;
        decimal price = 10000m;

        var trendDirection = _random.NextDouble() > 0.5 ? 1 : -1;
        var trendStrength = (decimal)(_random.NextDouble() * 0.001);
        var volatility = (decimal)(_random.NextDouble() * 0.02 + 0.01);

        for (int i = 0; i < count; i++)
        {
            var trendChange = price * trendStrength * trendDirection;
            var randomChange = price * volatility * (decimal)(_random.NextDouble() * 2 - 1);

            decimal open = price;
            decimal close = price + trendChange + randomChange;
            decimal maxChange = Math.Abs(close - open) + open * volatility * (decimal)_random.NextDouble();
            decimal high = Math.Max(open, close) + maxChange * 0.5m;
            decimal low = Math.Min(open, close) - maxChange * 0.5m;

            var volumeMultiplier = 1 + Math.Abs((close - open) / Math.Max(open, 0.01m));
            var volume = (decimal)(_random.Next(100000, 1000000) * (double)volumeMultiplier);

            var candleTime = timeFrame switch
            {
                TimeFrame.D1 => now.AddDays(-count + i),
                TimeFrame.W1 => now.AddDays((-count + i) * 7),
                TimeFrame.H1 => now.AddHours(-count + i),
                TimeFrame.H4 => now.AddHours((-count + i) * 4),
                _ => now.AddDays(-count + i)
            };

            candles.Add(new CandleData(
                candleTime,
                Math.Round(Math.Max(0.01m, open), 2),
                Math.Round(Math.Max(0.01m, high), 2),
                Math.Round(Math.Max(0.01m, low), 2),
                Math.Round(Math.Max(0.01m, close), 2),
                Math.Max(0L, (long)volume)
            ));

            price = close;

            // Occasionally reverse trend
            if (_random.NextDouble() < 0.1)
            {
                trendDirection *= -1;
                trendStrength = (decimal)(_random.NextDouble() * 0.001);
            }
        }

        return Task.FromResult<IReadOnlyList<CandleData>>(candles);
    }
}
