using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Services;

/// <summary>What happened when the stored bars of one symbol and time frame were asked for.</summary>
public enum DataReadStatus
{
    /// <summary>There is no stored file for the symbol and time frame.</summary>
    FileMissing,

    /// <summary>The file was opened and read. A file with the columns and no row (or no row with a usable close) is read, with no bar.</summary>
    Read,

    /// <summary>The file exists but could not be read (zero bytes, damaged, locked, a database error). Cancellation is never reported as this.</summary>
    Unreadable,
}

/// <summary>The bars of a read (empty unless <see cref="Status"/> is <see cref="DataReadStatus.Read"/>) and how the read ended.</summary>
public readonly record struct DataReadResult(DataReadStatus Status, IReadOnlyList<CandleData> Candles);

/// <summary>
/// A data service that can tell a missing file from an unreadable one, which <see cref="IDataService"/> cannot: its
/// <c>LoadCandlesAsync</c> answers both with an empty list. Kept apart from that interface on purpose, so that a data service which wraps another
/// one (and so cannot vouch for the file) never inherits an answer; a consumer asks <c>service is IDataReadStatusSource</c> and falls back to the
/// plain load when it is not.
/// </summary>
public interface IDataReadStatusSource
{
    /// <summary>
    /// Loads the stored bars (<paramref name="count"/> latest bars, 0 = all) and reports how the read ended. Does not repair or hide a failure.
    /// <see cref="OperationCanceledException"/> is rethrown, never turned into a status.
    /// </summary>
    Task<DataReadResult> LoadCandlesWithStatusAsync(string symbol, TimeFrame timeFrame, int count, CancellationToken cancellationToken = default);
}
