using System;
using System.Collections.Generic;

namespace StockAnalyzer.Core.Models;

/// <summary>
/// Container for multi-symbol market data aligned to a primary symbol's timeline.
/// All candle arrays in <see cref="Series"/> are guaranteed to have the same length
/// as <see cref="Timestamps"/>.
/// </summary>
/// <param name="PrimarySymbol">The symbol used as the timeline reference.</param>
/// <param name="Timestamps">The master list of periods (usually UTC).</param>
/// <param name="Series">Aligned CandleData arrays. Missing data is represented as null.</param>
/// <param name="Warnings">Optional warning messages (e.g., symbols that failed to load or had too much missing data).</param>
public record ComparisonAlignedData(
    string PrimarySymbol,
    DateTime[] Timestamps,
    IReadOnlyDictionary<string, CandleData?[]> Series,
    IReadOnlyList<string> Warnings
);
