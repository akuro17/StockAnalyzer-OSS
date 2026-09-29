using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace StockAnalyzer.Core.Interfaces;

/// <summary>
/// Provides high-performance ticker extraction from text streams.
/// </summary>
public interface ITickerImportService
{
    /// <summary>
    /// Extracts unique, normalized ticker symbols from a text stream.
    /// Supports newline, comma, tab, and space delimiters.
    /// </summary>
    /// <param name="stream">The source stream (UTF-8 text).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A unique list of normalized tickers.</returns>
    Task<IReadOnlyList<string>> ImportTickersAsync(Stream stream, CancellationToken ct = default);
}
