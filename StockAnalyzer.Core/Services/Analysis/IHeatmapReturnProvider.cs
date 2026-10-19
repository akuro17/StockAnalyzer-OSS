using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models.Analysis;

namespace StockAnalyzer.Core.Services.Analysis;

/// <summary>
/// Provides daily return ratios for uniform heatmap rendering based on a lookback offset.
/// </summary>
public interface IHeatmapReturnProvider
{
    /// <summary>
    /// Identifier of the provider implementation (e.g. "DuckDB", "Synthetic").
    /// </summary>
    string ProviderId { get; }

    /// <summary>
    /// Computes daily returns for the specified tickers evaluated on the (offset + 1)-th distinct trading day.
    /// </summary>
    /// <param name="tickers">List of ticker symbols to evaluate.</param>
    /// <param name="offset">Day offset back from latest trading day (0 = latest distinct date).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Heatmap return result with evaluated items and resolved reference date.</returns>
    Task<HeatmapReturnResult> ComputeAsync(
        IReadOnlyList<string> tickers,
        int offset,
        CancellationToken cancellationToken = default);
}
