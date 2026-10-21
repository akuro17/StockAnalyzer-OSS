using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models.Analysis;

namespace StockAnalyzer.Core.Services.Analysis;

/// <summary>
/// Synthetic provider generating deterministic sample returns when live daily parquet data is unavailable.
/// Offset 0 yields a stable pseudo-random 1-bar return per ticker (32-bit FNV-1a of the symbol); offset > 0 yields NaN values as synthetic data lacks history.
/// </summary>
public sealed class SyntheticHeatmapReturnProvider : IHeatmapReturnProvider
{
    public static readonly SyntheticHeatmapReturnProvider Instance = new();

    private const uint FnvOffsetBasis = 2166136261u;
    private const uint FnvPrime = 16777619u;
    private const uint SeedModulus = 1000u;
    private const double ReturnSpan = 0.16;
    private const double ReturnFloor = -0.08;

    /// <summary>
    /// Computes deterministic 32-bit FNV-1a hash across processes and platforms.
    /// </summary>
    public static uint GetStableHash32(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        uint hash = FnvOffsetBasis;
        foreach (char c in text)
        {
            hash ^= (byte)c;
            hash *= FnvPrime;
            hash ^= (byte)(c >> 8);
            hash *= FnvPrime;
        }
        return hash;
    }

    private SyntheticHeatmapReturnProvider()
    {
    }

    public string ProviderId => "Synthetic";

    public Task<HeatmapReturnResult> ComputeAsync(
        IReadOnlyList<string> tickers,
        int offset,
        CancellationToken cancellationToken = default)
    {
        if (tickers == null || tickers.Count == 0)
        {
            return Task.FromResult(HeatmapReturnResult.Empty);
        }

        if (offset < HeatmapOffsetLimits.Min || offset > HeatmapOffsetLimits.Max)
        {
            throw new ArgumentOutOfRangeException(
                nameof(offset),
                offset,
                $"Offset must be between {HeatmapOffsetLimits.Min} and {HeatmapOffsetLimits.Max}.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (offset == 0)
        {
            var items = new List<UniformHeatmapItem>(tickers.Count);
            for (int i = 0; i < tickers.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string symbol = tickers[i];
                double seed = (GetStableHash32(symbol) % SeedModulus) / (double)SeedModulus;
                items.Add(new UniformHeatmapItem(symbol, (seed * ReturnSpan) + ReturnFloor));
            }

            return Task.FromResult(new HeatmapReturnResult(
                items,
                targetDate: null,
                isLiveMarketData: false,
                providerId: ProviderId,
                statusMessage: "Synthetic Demo Data"));
        }

        var nanItems = new List<UniformHeatmapItem>(tickers.Count);
        for (int i = 0; i < tickers.Count; i++)
        {
            nanItems.Add(new UniformHeatmapItem(tickers[i], double.NaN));
        }

        return Task.FromResult(new HeatmapReturnResult(
            nanItems,
            targetDate: null,
            isLiveMarketData: false,
            providerId: ProviderId,
            statusMessage: "Synthetic Demo Data (no history at offset)"));
    }
}
