using StockAnalyzer.Core.Models.Portfolio;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// Outcome of reading a ticker's persisted metadata file.
/// </summary>
/// <param name="Meta">The stored metadata; <see cref="TickerMetadata.Unknown"/> when there is nothing to read.</param>
/// <param name="ReadFailed">True only when reading the existing file threw. False for "no file" and "no row": those mean
/// the ticker has no stored data, whereas a failed read says nothing about what is stored.</param>
public readonly record struct MetadataDiskLoad(TickerMetadata Meta, bool ReadFailed);
