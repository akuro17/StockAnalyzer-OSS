using System;
using System.Collections.Generic;
using System.IO;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// Immutable timestamp-to-row index of one validated, ordered monitoring observation. It answers exactly what a linear
/// search for <c>Utc(candle.Timestamp) == anchor</c> answered (the row position, or -1 when absent), without scanning.
/// </summary>
internal sealed class MonitoringAnchorIndex
{
    private readonly Dictionary<DateTimeOffset, int> _rowByAnchor;
    private MonitoringAnchorIndex(Dictionary<DateTimeOffset, int> rowByAnchor) => _rowByAnchor = rowByAnchor;

    /// <summary>Indexes every row by its UTC-stamped timestamp. A duplicate timestamp is a validation error, never a silent overwrite.</summary>
    public static MonitoringAnchorIndex Create(IReadOnlyList<CandleData> candles)
    {
        ArgumentNullException.ThrowIfNull(candles);
        var rows = new Dictionary<DateTimeOffset, int>(candles.Count);
        for (int i = 0; i < candles.Count; i++)
            if (!rows.TryAdd(PredictionLogService.Utc(candles[i].Timestamp), i))
                throw new InvalidDataException("Monitoring candles contain a duplicate timestamp.");
        return new MonitoringAnchorIndex(rows);
    }

    /// <summary>The row of <paramref name="anchorUtc"/>, or -1 when the observation has no such bar.</summary>
    public int Find(DateTimeOffset anchorUtc) => _rowByAnchor.TryGetValue(anchorUtc, out var row) ? row : -1;
}
