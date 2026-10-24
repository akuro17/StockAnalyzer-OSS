using System.IO;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Services.ReinforcementLearning;

/// <summary>
/// The two folders an RL run copies its one symbol from. A caller that passes none gets the application's fixed <c>Data/Daily</c> and
/// <c>Data/Metadata</c> (resolved by the creating resolver, as the RL window always did); a caller that passes this record chooses the folders
/// and nothing is created for them: a missing folder is simply "no such symbol".
/// </summary>
/// <param name="Daily">Absolute folder of the daily Parquet files.</param>
/// <param name="Metadata">Absolute folder of the metadata Parquet files.</param>
public sealed record RlLocalSourceFolders(string Daily, string Metadata)
{
    /// <summary>The folders the configured market-data settings name, resolved without creating anything (the rule the read-only host uses for its own data tree).</summary>
    public static RlLocalSourceFolders FromSettings(MarketDataSettings settings)
        => new(
            Path.GetFullPath(PathDiscovery.ResolveDataPathWithoutCreating(settings.DailyDataPath, MarketDataSettings.DefaultDailyDataPath, "*.parquet")),
            Path.GetFullPath(PathDiscovery.ResolveDataPathWithoutCreating(settings.MetadataPath, MarketDataSettings.DefaultMetadataDataPath)));
}
