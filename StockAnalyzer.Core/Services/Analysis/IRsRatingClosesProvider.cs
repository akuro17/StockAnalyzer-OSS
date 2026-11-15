using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Analysis;

namespace StockAnalyzer.Core.Services.Analysis;

/// <summary>
/// Provides trailing daily closes needed to compute cross-sectional IBD-style RS Rating across a ticker universe.
/// Separate from <see cref="IHeatmapReturnProvider"/> so existing heatmap test doubles remain unaffected.
/// </summary>
public interface IRsRatingClosesProvider
{
    /// <summary>
    /// Fetches daily close checkpoints for the requested <paramref name="tickers"/>.
    /// Tickers without enough non-null daily closes are omitted.
    /// </summary>
    Task<IReadOnlyDictionary<string, RsRatingCloses>> GetRsRatingClosesAsync(
        IReadOnlyList<string> tickers,
        CancellationToken cancellationToken = default);
}
