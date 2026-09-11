namespace StockAnalyzer.Core.Models.UI;

/// <summary>
/// Immutable metadata for a workspace tab.
/// </summary>
public readonly record struct TabMetadata(
    string Id,
    string DisplayName,
    string? IconKey = null,
    bool CanClose = true,
    bool AllowMultiple = false
);
