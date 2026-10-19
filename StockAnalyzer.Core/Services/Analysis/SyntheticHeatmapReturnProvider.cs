using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models.Analysis;

namespace StockAnalyzer.Core.Services.Analysis;

/// <summary>
/// Synthetic provider generating deterministic sample returns when live daily parquet data is unavailable.
/// Offset 0 reuses values from SyntheticScatterMetricProvider; offset > 0 yields NaN values as synthetic data lacks history.
/// </summary>
public sealed class SyntheticHeatmapReturnProvider : IHeatmapReturnProvider
{
    public static readonly SyntheticHeatmapReturnProvider Instance = new();

    private SyntheticHeatmapReturnProvider()
    {
    }

    public string ProviderId => "Synthetic";

    public async Task<HeatmapReturnResult> ComputeAsync(
        IReadOnlyList<string> tickers,
        int offset,
        CancellationToken cancellationToken = default)
    {
        if (tickers == null || tickers.Count == 0)
        {
            return HeatmapReturnResult.Empty;
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
            var metricResult = await SyntheticScatterMetricProvider.Instance
                .ComputeMetricsAsync(tickers, "Return", "Volatility", cancellationToken)
                .ConfigureAwait(false);

            var items = new List<UniformHeatmapItem>(metricResult.Items.Count);
            for (int i = 0; i < metricResult.Items.Count; i++)
            {
                var it = metricResult.Items[i];
                items.Add(new UniformHeatmapItem(it.Ticker, it.ReturnPercentage));
            }

            return new HeatmapReturnResult(
                items,
                targetDate: null,
                isLiveMarketData: false,
                providerId: ProviderId,
                statusMessage: "Synthetic Demo Data");
        }

        var nanItems = new List<UniformHeatmapItem>(tickers.Count);
        for (int i = 0; i < tickers.Count; i++)
        {
            nanItems.Add(new UniformHeatmapItem(tickers[i], double.NaN));
        }

        return new HeatmapReturnResult(
            nanItems,
            targetDate: null,
            isLiveMarketData: false,
            providerId: ProviderId,
            statusMessage: "Synthetic Demo Data (no history at offset)");
    }
}
