using System;
using System.Collections.Generic;

namespace StockAnalyzer.Core.Models.Analysis;

/// <summary>
/// Container holding scatter plot points along with metadata, axis labels, and optional item labels.
/// </summary>
public sealed class ScatterDataset
{
    public static ScatterDataset Empty { get; } = new();

    public string Title { get; init; } = string.Empty;
    public string XAxisLabel { get; init; } = string.Empty;
    public string YAxisLabel { get; init; } = string.Empty;
    public IReadOnlyList<ScatterPoint> Points { get; init; } = Array.Empty<ScatterPoint>();
    public IReadOnlyList<string>? Labels { get; init; } // Optional ticker symbols or item labels matching Points index

    /// <summary>Fixed X axis range (data units); null = the axis autoscales from the points.</summary>
    public AxisRange? XFixedRange { get; init; }

    /// <summary>Fixed Y axis range (data units); null = the axis autoscales from the points.</summary>
    public AxisRange? YFixedRange { get; init; }

    public ScatterDataset()
    {
    }

    public ScatterDataset(
        string title,
        string xAxisLabel,
        string yAxisLabel,
        IReadOnlyList<ScatterPoint> points,
        IReadOnlyList<string>? labels = null)
    {
        Title = title ?? string.Empty;
        XAxisLabel = xAxisLabel ?? string.Empty;
        YAxisLabel = yAxisLabel ?? string.Empty;
        Points = points ?? Array.Empty<ScatterPoint>();
        Labels = labels;
    }
}
