using System.IO;

namespace StockAnalyzer.Core.Models;

/// <summary>
/// Configuration for market data storage and access.
/// </summary>
public class MarketDataSettings
{
    public const string DefaultDailyDataPath = "Data/Daily";
    public const string DefaultMetadataDataPath = "Data/Metadata";
    public const string DefaultWeeklyDataPath = "Data/Weekly";
    public const string DefaultMonthlyDataPath = "Data/Monthly";

    /// <summary>
    /// Base directory path for daily Parquet files.
    /// </summary>
    public string? DailyDataPath { get; set; } = DefaultDailyDataPath;

    /// <summary>
    /// Base directory path for weekly Parquet files.
    /// </summary>
    public string? WeeklyDataPath { get; set; } = DefaultWeeklyDataPath;

    /// <summary>
    /// Base directory path for monthly Parquet files.
    /// </summary>
    public string? MonthlyDataPath { get; set; } = DefaultMonthlyDataPath;

    /// <summary>
    /// Base directory path for metadata Parquet files.
    /// </summary>
    public string? MetadataPath { get; set; } = DefaultMetadataDataPath;

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

        // Validate that timeframe directories are mutually disjoint to prevent accidental overwrites
        var timeframePaths = new (string Name, string? Value)[]
        {
            (nameof(DailyDataPath), DailyDataPath),
            (nameof(WeeklyDataPath), WeeklyDataPath),
            (nameof(MonthlyDataPath), MonthlyDataPath)
        };

        for (int i = 0; i < timeframePaths.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(timeframePaths[i].Value)) continue;
            string fullPathI;
            try
            {
                fullPathI = Path.GetFullPath(timeframePaths[i].Value!).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            for (int j = i + 1; j < timeframePaths.Length; j++)
            {
                if (string.IsNullOrWhiteSpace(timeframePaths[j].Value)) continue;
                string fullPathJ;
                try
                {
                    fullPathJ = Path.GetFullPath(timeframePaths[j].Value!).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    continue;
                }

                if (string.Equals(fullPathI, fullPathJ, StringComparison.OrdinalIgnoreCase))
                {
                    throw new System.ArgumentException(
                        $"{timeframePaths[i].Name} and {timeframePaths[j].Name} must not resolve to the same directory ('{timeframePaths[i].Value}').",
                        timeframePaths[j].Name);
                }
            }
        }
    }
}
