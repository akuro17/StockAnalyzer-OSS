using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// Authoritative single producer of Weekly (W1) and Monthly (MN1) Parquet files from Daily (D1) Parquet.
/// Applies canonical date stamping (PeriodStart) and guarantees atomic replacement and read-back verification.
/// </summary>
public interface ITimeFrameParquetGenerator
{
    /// <summary>
    /// Generates Weekly and Monthly Parquet files for the specified symbol or all symbols in the Daily folder.
    /// Returns the number of tickers that failed (0 on full success).
    /// </summary>
    Task<int> GenerateAsync(string? symbol = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates Weekly and Monthly Parquet files from a given daily candle collection for a specific symbol.
    /// Returns true on success; returns false and preserves prior target files untouched on any failure.
    /// </summary>
    Task<bool> GenerateForSymbolAsync(string symbol, IReadOnlyList<CandleData> dailyBars, CancellationToken cancellationToken = default);
}
