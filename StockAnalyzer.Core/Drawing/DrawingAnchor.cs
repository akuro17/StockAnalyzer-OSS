namespace StockAnalyzer.Core.Drawing;

/// <summary>
/// A control point of a drawing tool in chart coordinates (time, price), free of any UI type.
/// Price is a <see cref="decimal"/>; it is never a screen coordinate.
/// </summary>
public readonly record struct DrawingAnchor(DateTime Time, decimal Price);
