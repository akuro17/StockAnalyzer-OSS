using System;

namespace StockAnalyzer.Core.Models
{
    /// <summary>
    /// Model representing an individual ticker's synchronization state.
    /// </summary>
    public class SyncItemModel
    {
        public SyncItemModel() { }

        public SyncItemModel(string symbol, string period)
        {
            Symbol = symbol;
            Period = period;
            Status = SyncStatus.Waiting;
        }

        /// <summary>
        /// The stock ticker symbol.
        /// </summary>
        public string Symbol { get; set; } = string.Empty;

        /// <summary>
        /// The time series period (e.g., Daily, Weekly).
        /// </summary>
        public string Period { get; set; } = string.Empty;

        /// <summary>
        /// Progress percentage (0 to 100).
        /// </summary>
        public double Progress { get; set; }

        /// <summary>
        /// Numerical progress representation (e.g., "10,500 / 12,345").
        /// </summary>
        public string ProgressText { get; set; } = string.Empty;

        /// <summary>
        /// Current synchronization status.
        /// </summary>
        public SyncStatus Status { get; set; }

        /// <summary>
        /// Error message if status is Error.
        /// </summary>
        public string? ErrorMessage { get; set; }

        /// <summary>
        /// The last time high-level progress was reported.
        /// </summary>
        public DateTime? LastUpdated { get; set; }

        public void UpdateProgress(double progress, string text)
        {
            Progress = progress;
            ProgressText = text;
            LastUpdated = DateTime.Now;
        }

        public void MarkSyncing()
        {
            Status = SyncStatus.Syncing;
            LastUpdated = DateTime.Now;
        }

        public void MarkComplete()
        {
            Status = SyncStatus.Completed;
            Progress = 100;
            ProgressText = "Complete";
            LastUpdated = DateTime.Now;
        }

        public void MarkError(string error)
        {
            Status = SyncStatus.Error;
            ErrorMessage = error;
            LastUpdated = DateTime.Now;
        }

        public void MarkCanceled()
        {
            Status = SyncStatus.Canceled;
            ErrorMessage = "Canceled";
            LastUpdated = DateTime.Now;
        }
    }
}
