namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Common contract for NURBS curve drawing objects with a single variable curvature (Weight).
/// Implemented by both <see cref="NurbsHyperbolaObject"/> and <see cref="NurbsConicArcObject"/> so
/// that <c>DrawingSettingsDialog</c> can share the same settings panel/binding logic for both
/// (same intent as <see cref="INurbsConicShapeObject"/>).
/// </summary>
public interface INurbsWeightedCurveObject
{
    double Weight { get; set; }

    /// <summary>The UI-facing minimum value to set on the NumericUpDown.</summary>
    double WeightRangeMin { get; }

    /// <summary>The UI-facing maximum value to set on the NumericUpDown.</summary>
    double WeightRangeMax { get; }

    /// <summary>Localization key for the settings panel's label.</summary>
    string WeightLabelKey { get; }

    /// <summary>The NumericUpDown's step increment.</summary>
    double WeightIncrement { get; }

    /// <summary>The NumericUpDown's display format (compatible with Avalonia NumericUpDown.FormatString).</summary>
    string WeightFormatString { get; }
}
