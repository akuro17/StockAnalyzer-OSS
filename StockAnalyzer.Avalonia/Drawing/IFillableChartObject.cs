using Avalonia.Media;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Opt-in interface for 2D closed chart drawing objects supporting advanced fills,
/// gradients, and blending modes.
/// </summary>
public interface IFillableChartObject : IChartObject
{
    /// <summary>Gets or sets whether the interior is filled.</summary>
    bool IsFilled { get; set; }

    /// <summary>Gets or sets the blending mode applied to the fill layer.</summary>
    DrawingBlendMode BlendMode { get; set; }

    /// <summary>Gets or sets the gradient fill type.</summary>
    DrawingGradientType GradientType { get; set; }

    /// <summary>
    /// Gets or sets the secondary/end color for gradient fills.
    /// If null, the base Color is used with GradientEndAlpha.
    /// </summary>
    Color? GradientEndColor { get; set; }

    /// <summary>Gets or sets the opacity (0-255) for the primary fill / gradient start. Default: 30.</summary>
    byte FillAlpha { get; set; }

    /// <summary>Gets or sets the opacity (0-255) for the gradient end. Default: 30.</summary>
    byte GradientEndAlpha { get; set; }
}
