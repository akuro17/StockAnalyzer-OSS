namespace StockAnalyzer.Avalonia.Drawing;

using System.Collections.Generic;

/// <summary>
/// Opt-in interface for drawing tools capable of auto-extrema extraction and projection.
/// Adheres strictly to the Interface Segregation Principle (ISP).
/// </summary>
public interface IExtremaLevelSupport
{
    /// <summary>Gets or sets whether resistance (high peak) extrema levels are rendered.</summary>
    bool ShowResistanceLevels { get; set; }

    /// <summary>Gets or sets whether support (low trough) extrema levels are rendered.</summary>
    bool ShowSupportLevels { get; set; }

    /// <summary>Gets or sets whether horizontal extrema levels are rendered.</summary>
    bool ShowHorizontalLevels { get; set; }

    /// <summary>Gets or sets the minimum swing depth percentage required for an extremum to be retained.</summary>
    double MinSwingPercent { get; set; }

    /// <summary>Gets or sets the maximum number of extrema levels displayed per type (High/Low).</summary>
    int MaxLevels { get; set; }

    /// <summary>Gets or sets the pixel tolerance within which nearby extrema are clustered into a single level.</summary>
    double ClusterTolerancePx { get; set; }

    /// <summary>Gets the cached list of calculated extrema levels.</summary>
    IReadOnlyList<ExtremaLevel> ExtremaLevels { get; }

    /// <summary>Invalidates extrema caches upon geometry or transform mutations.</summary>
    void InvalidateExtrema();
}
