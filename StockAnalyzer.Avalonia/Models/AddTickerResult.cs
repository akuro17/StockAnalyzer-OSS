using System;
using System.Collections.Generic;

namespace StockAnalyzer.Avalonia.Models;

/// <summary>
/// Result payload returned by the Add Ticker dialog.
/// </summary>
public record AddTickerResult(
    string? Symbol,
    bool RequestBulkImport,
    IReadOnlyList<Guid> TargetProfileIds,
    string? ImportTags);
