using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace StockAnalyzer.Core.Services.Analysis;

/// <summary>
/// Deterministic synthetic metrics provider for demo mode, offline operation, and unit testing.
/// Uses 32-bit FNV-1a hashing to generate stable, reproducible cross-platform synthetic values.
/// </summary>
public sealed class SyntheticScatterMetricProvider : IScatterMetricProvider
{
    public static readonly SyntheticScatterMetricProvider Instance = new();

    public string ProviderId => "Synthetic";
    public bool IsLiveMarketData => false;

    /// <summary>
    /// Computes deterministic 32-bit FNV-1a hash across processes and platforms.
    /// </summary>
    public static uint GetStableHash32(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        uint hash = 2166136261u;
        foreach (char c in text)
        {
            hash ^= (byte)c;
            hash *= 16777619u;
            hash ^= (byte)(c >> 8);
            hash *= 16777619u;
        }
        return hash;
    }

    public Task<ScatterMetricResult> ComputeMetricsAsync(
        IReadOnlyList<string> tickers,
        string xMetric,
        string yMetric,
        CancellationToken cancellationToken = default)
    {
        if (tickers == null || tickers.Count == 0)
        {
            return Task.FromResult(ScatterMetricResult.Empty);
        }

        cancellationToken.ThrowIfCancellationRequested();

        var items = new List<ScatterMetricItem>(tickers.Count);

        for (int i = 0; i < tickers.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string symbol = tickers[i];
            uint hash = GetStableHash32(symbol);

            double seed = (hash % 1000u) / 1000.0;
            double ret = ((seed * 0.16) - 0.08); // -8% to +8%
            double vol = 0.10 + ((((hash / 1000u) % 500u) / 500.0) * 0.40); // 10% to 50%

            double xVal = xMetric switch
            {
                "Volatility" => vol,
                "RSI" => 30.0 + (seed * 45.0),
                "P/E" => 10.0 + (seed * 40.0),
                "Volume" => 100000.0 + (seed * 5000000.0),
                "MarketCap" => 1000.0 + (seed * 50000.0),
                _ => ret * 100.0 // Return percentage
            };

            double yVal = yMetric switch
            {
                "Return" => ret * 100.0,
                "RSI" => 30.0 + (seed * 45.0),
                "P/E" => 10.0 + (seed * 40.0),
                "Volume" => 100000.0 + (seed * 5000000.0),
                "MarketCap" => 1000.0 + (seed * 50000.0),
                _ => vol * 100.0 // Volatility percentage
            };

            items.Add(new ScatterMetricItem(symbol, xVal, yVal, ret));
        }

        var result = new ScatterMetricResult(
            items: items,
            isLiveMarketData: false,
            providerId: ProviderId,
            statusMessage: "Synthetic Demo Data"
        );

        return Task.FromResult(result);
    }
}
