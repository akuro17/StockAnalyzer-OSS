using Avalonia;

namespace StockAnalyzer.Avalonia.Common;

/// <summary>Layout token of the shared group headers of the Training Wizard, RL and Portfolio RL windows (<c>Assets/TrainingGroupHeader.axaml</c>), in device independent pixels. Owned here so no window layout class is referenced by the shared asset.</summary>
public static class TrainingGroupHeaderLayout
{
    public const double AdvancedTopMargin = 12;
    public static Thickness AdvancedMargin { get; } = new(0, AdvancedTopMargin, 0, 0);
}
