using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Core.ReadOnly;

/// <summary>Used when the data tree is incomplete: returns no candles instead of letting <c>ParquetDataService</c> create the missing folders.</summary>
public sealed class EmptyDataService : IDataService
{
    public Task<IReadOnlyList<CandleData>> LoadCandlesAsync(string symbol, TimeFrame timeFrame, int count = 100)
        => Task.FromResult<IReadOnlyList<CandleData>>(Array.Empty<CandleData>());
}
