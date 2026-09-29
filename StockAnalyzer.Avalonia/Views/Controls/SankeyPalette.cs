namespace StockAnalyzer.Avalonia.Views.Controls;

/// <summary>
/// Color palette definition for rendering Sankey nodes, ribbon bands, labels, and focus borders.
/// Colors are specified in 32-bit ARGB format to remain framework-neutral.
/// </summary>
public readonly record struct SankeyPalette(
    uint NodeArgb,
    uint LinkArgb,
    uint TextArgb,
    uint FocusArgb,
    uint BackgroundArgb)
{
    /// <summary>
    /// Default high-contrast dark theme palette.
    /// </summary>
    public static SankeyPalette DefaultDark => new(
        NodeArgb: 0xFF4A90E2,        // Soft/Dodger Blue
        LinkArgb: 0x994A90E2,        // Semi-transparent link flow (~60% alpha)
        TextArgb: 0xFFE0E0E0,        // Off-white readable text
        FocusArgb: 0xFFFFD700,       // Golden focus highlight
        BackgroundArgb: 0x00000000);  // Transparent canvas

    /// <summary>
    /// Default light theme palette.
    /// </summary>
    public static SankeyPalette DefaultLight => new(
        NodeArgb: 0xFF1976D2,        // Deep Blue
        LinkArgb: 0x991976D2,        // Semi-transparent link flow (~60% alpha)
        TextArgb: 0xFF212121,        // Dark gray text
        FocusArgb: 0xFFE65100,       // Amber/Orange focus highlight
        BackgroundArgb: 0x00000000);  // Transparent canvas
}
