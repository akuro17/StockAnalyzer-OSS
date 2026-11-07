using System;

namespace StockAnalyzer.Core.ReadOnly;

/// <summary>
/// The single definition of how <see cref="IReadOnlyStockAnalyzerGateway.RunBacktestBatchAsync(System.Collections.Generic.IReadOnlyList{StockAnalyzer.Core.Services.BacktestRunSnapshot}, int, System.Threading.CancellationToken)"/>
/// tells the caller WHICH snapshot failed: the exception it rethrows (the one of the lowest failing index, the one a sequential loop would hit first) carries that index in
/// <see cref="Exception.Data"/>. The exception type and message are unchanged, so a caller that does not look at the index behaves as before.
/// </summary>
public static class BacktestBatchFailure
{
    /// <summary>The <see cref="Exception.Data"/> key holding the zero-based index of the failing snapshot (an <see cref="int"/>).</summary>
    public const string ItemIndexKey = "BacktestBatchItemIndex";

    internal static void Tag(Exception exception, int index) => exception.Data[ItemIndexKey] = index;

    /// <summary>The index of the snapshot whose run raised <paramref name="exception"/>, or null when the exception was not raised by a batch item.</summary>
    public static int? ItemIndexOf(Exception exception) => exception.Data[ItemIndexKey] is int index ? index : null;
}
