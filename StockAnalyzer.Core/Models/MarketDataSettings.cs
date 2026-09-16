using System.IO;

namespace StockAnalyzer.Core.Models;

/// <summary>
/// Configuration for market data storage and access.
/// </summary>
public class MarketDataSettings
{
    /// <summary>
    /// Base directory path for daily Parquet files.
    /// </summary>
    public string? DailyDataPath { get; set; } = "Data/Daily";

    /// <summary>
    /// Base directory path for weekly Parquet files.
    /// </summary>
    public string? WeeklyDataPath { get; set; } = "Data/Weekly";

    /// <summary>
    /// Base directory path for monthly Parquet files.
    /// </summary>
    public string? MonthlyDataPath { get; set; } = "Data/Monthly";

    /// <summary>
    /// Base directory path for metadata Parquet files.
    /// </summary>
    public string? MetadataPath { get; set; } = "Data/Metadata";

    /// <summary>
    /// Path to the master ticker list JSON file.
    /// </summary>
    public string? TickerListPath { get; set; } = "StockAnalyzer.Python/tickers.json";

    /// <summary>
    /// Validates the settings.
    /// </summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(DailyDataPath))
        {
            throw new System.ArgumentException("DailyDataPath must not be empty.", nameof(DailyDataPath));
        }

        // Validate paths for invalid characters
        var invalidChars = Path.GetInvalidPathChars();
        var extraInvalidChars = new[] { '*', '?', '"', '<', '>', '|' };

        var pathEntries = new (string Name, string? Value)[]
        {
            (nameof(DailyDataPath), DailyDataPath),
            (nameof(WeeklyDataPath), WeeklyDataPath),
            (nameof(MonthlyDataPath), MonthlyDataPath),
            (nameof(MetadataPath), MetadataPath),
            (nameof(TickerListPath), TickerListPath)
        };

        foreach (var entry in pathEntries)
        {
            if (!string.IsNullOrEmpty(entry.Value) && 
                (entry.Value.IndexOfAny(invalidChars) >= 0 || entry.Value.IndexOfAny(extraInvalidChars) >= 0))
            {
                throw new System.ArgumentException($"Path '{entry.Value}' contains invalid characters.", entry.Name);
            }
        }
    }
}
