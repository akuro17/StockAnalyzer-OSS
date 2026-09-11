using System.Collections.Generic;

namespace StockAnalyzer.Core.Models.Settings;

public sealed record RelativePerformancePreset
{
    public string Name { get; init; } = string.Empty;
    public List<string> Targets { get; init; } = new();
}
