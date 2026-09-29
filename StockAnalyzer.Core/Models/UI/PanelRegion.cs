namespace StockAnalyzer.Core.Models.UI;

/// <summary>
/// Defines the layout regions for docking panels.
/// Underlying type is byte to minimize memory footprint.
/// </summary>
public enum PanelRegion : byte
{
    Unknown = 0,
    Left = 1,
    Right = 2,
    Top = 3,
    Bottom = 4
}
