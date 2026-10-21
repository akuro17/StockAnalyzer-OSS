using System.Collections.Generic;

namespace StockAnalyzer.Core.Models.Analysis;

/// <summary>On-disk shape of the registered scatter metrics (<c>Data\Config\scatter_metrics.json</c>).</summary>
public sealed class ScatterMetricFile
{
    /// <summary>The only format this build reads and writes. A file with a larger number is ignored (and left untouched).</summary>
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; set; } = CurrentFormatVersion;

    public List<ScatterMetricDefinition> Metrics { get; set; } = new();
}
