namespace StockAnalyzer.Core.Models
{
    /// <summary>
    /// Represents the status of a synchronization task.
    /// </summary>
    public enum SyncStatus
    {
        /// <summary>
        /// Task is waiting in queue.
        /// </summary>
        Waiting,

        /// <summary>
        /// Task is currently being processed.
        /// </summary>
        Syncing,

        /// <summary>
        /// Task completed successfully.
        /// </summary>
        Completed,

        /// <summary>
        /// Task failed due to an error.
        /// </summary>
        Error,

        /// <summary>
        /// Task has been paused.
        /// </summary>
        Paused,

        /// <summary>
        /// Task was canceled.
        /// </summary>
        Canceled
    }
}
