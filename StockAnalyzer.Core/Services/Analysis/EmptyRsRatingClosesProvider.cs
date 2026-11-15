using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Analysis;

namespace StockAnalyzer.Core.Services.Analysis;

/// <summary>
/// No-op <see cref="IRsRatingClosesProvider"/> used when the stored data folder is incomplete or absent.
/// </summary>
public sealed class EmptyRsRatingClosesProvider : IRsRatingClosesProvider
{
    private static readonly IReadOnlyDictionary<string, RsRatingCloses> EmptyResult =
        new Dictionary<string, RsRatingCloses>(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, RsRatingCloses>> GetRsRatingClosesAsync(
        IReadOnlyList<string> tickers,
        CancellationToken cancellationToken = default)
        => Task.FromResult(EmptyResult);
}
