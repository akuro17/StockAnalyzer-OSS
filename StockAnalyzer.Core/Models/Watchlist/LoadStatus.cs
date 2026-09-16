namespace StockAnalyzer.Core.Models.Watchlist
{
    /// <summary>
    /// Represents the loading or synchronization status of a single watchlist item.
    /// Byte-based enum to minimize memory footprint and avoid boxing in ZeroAllocation scenarios.
    /// </summary>
    public enum LoadStatus : byte
    {
        /// <summary>
        /// Initial state. Not yet processed or waiting in queue.
        /// </summary>
        Pending = 0,

        /// <summary>
        /// Currently being processed (Hydrating from local storage or Syncing from remote).
        /// </summary>
        Loading = 1,

        /// <summary>
        /// Processed successfully.
        /// </summary>
        Success = 2,

        /// <summary>
        /// Processing failed due to error or timeout.
        /// </summary>
        Failed = 3,

        /// <summary>
        /// Processing was canceled by user intervention.
        /// </summary>
        Canceled = 4
    }
}
