using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.ReadOnly;

/// <summary>
/// Result of checking, without creating anything, that every folder and file the Parquet data layer
/// would otherwise create on construction already exists. When anything is missing the read-only
/// composition uses empty data sources instead (the providers create missing folders and a default
/// ticker list in their constructors).
/// </summary>
public sealed record ReadOnlyDataTree(IReadOnlyList<string> MissingItems)
{
    public bool IsComplete => MissingItems.Count == 0;

    /// <summary>Names the missing <see cref="MarketDataSettings"/> entries; never contains a path.</summary>
    public static ReadOnlyDataTree Probe(MarketDataSettings settings)
    {
        settings.Validate();

        var missing = new List<string>();
        CheckDirectory(missing, nameof(MarketDataSettings.DailyDataPath),
            PathDiscovery.ResolveDataPathWithoutCreating(settings.DailyDataPath, MarketDataSettings.DefaultDailyDataPath, "*.parquet"));
        CheckDirectory(missing, nameof(MarketDataSettings.WeeklyDataPath),
            PathDiscovery.ResolveDataPathWithoutCreating(settings.WeeklyDataPath, MarketDataSettings.DefaultWeeklyDataPath));
        CheckDirectory(missing, nameof(MarketDataSettings.MonthlyDataPath),
            PathDiscovery.ResolveDataPathWithoutCreating(settings.MonthlyDataPath, MarketDataSettings.DefaultMonthlyDataPath));
        CheckDirectory(missing, nameof(MarketDataSettings.MetadataPath),
            PathDiscovery.ResolveDataPathWithoutCreating(settings.MetadataPath, MarketDataSettings.DefaultMetadataDataPath));

        if (!string.IsNullOrEmpty(settings.TickerListPath)
            && !File.Exists(PathDiscovery.ResolveFilePath(settings.TickerListPath, settings.TickerListPath)))
        {
            missing.Add(nameof(MarketDataSettings.TickerListPath));
        }

        return new ReadOnlyDataTree(missing);
    }

    private static void CheckDirectory(List<string> missing, string settingName, string resolvedPath)
    {
        if (!Directory.Exists(resolvedPath))
        {
            missing.Add(settingName);
        }
    }
}
