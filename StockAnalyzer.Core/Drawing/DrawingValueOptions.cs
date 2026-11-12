namespace StockAnalyzer.Core.Drawing;

/// <summary>Tool options that change which values a drawing-value calculator returns.</summary>
/// <param name="ShowExtensions">FibonacciRetracement only: use the extension level table instead of the default one.</param>
public readonly record struct DrawingValueOptions(bool ShowExtensions = false);
