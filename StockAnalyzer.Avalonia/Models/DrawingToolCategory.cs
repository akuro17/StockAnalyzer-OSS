namespace StockAnalyzer.Avalonia.Models;

using StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Defines a category of drawing tools for the sidebar UI.
/// </summary>
public sealed record DrawingToolCategoryDefinition(
    string NameKey,
    string Icon,
    DrawingToolInfo[] Tools
);

/// <summary>
/// Metadata for a single drawing tool within a category.
/// </summary>
public sealed record DrawingToolInfo(
    DrawingTool Tool,
    string Icon,
    string NameKey
);
