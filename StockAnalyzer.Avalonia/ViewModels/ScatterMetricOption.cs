using System;

namespace StockAnalyzer.Avalonia.ViewModels;

/// <summary>One registered scatter metric as the X / Y combo boxes list it: the identity that survives an edit of the metric, and its display text.</summary>
public sealed record ScatterMetricOption(Guid Id, string Label)
{
    public override string ToString() => Label;
}
