using StockAnalyzer.Core.Models.Indicators;

namespace StockAnalyzer.Core.Models.Screener;

public enum ScreenerItemCategoryType
{
    Indicator,
    Column,
    Criteria
}

/// <summary>
/// Unified catalog display item representing an Indicator, Column, or Criteria condition.
/// </summary>
public class ScreenerCatalogItem
{
    public ScreenerItemCategoryType CategoryType { get; set; }
    public string GroupName { get; set; } = string.Empty;
    public string ShortName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    public IndicatorType? IndicatorType { get; set; }
    public string? ColumnMemberName { get; set; }
}
