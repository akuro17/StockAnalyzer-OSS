using System;

namespace StockAnalyzer.Core.Models.Screener;

public enum TargetSourceType
{
    AllTickers,
    Watchlist,
    Portfolio,
    TagFilter,
    SavedFilter
}

public sealed record ScreenerTargetSource
{
    public TargetSourceType Type { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public Guid? ProfileId { get; init; }
    public string? TagName { get; init; }
    public StockAnalyzer.Core.Models.Settings.FilterSettings? FilterSettings { get; init; }
    public bool IsHeader { get; init; }

    public ScreenerTargetSource(
        TargetSourceType type,
        string displayName,
        Guid? profileId = null,
        string? tagName = null,
        StockAnalyzer.Core.Models.Settings.FilterSettings? filterSettings = null,
        bool isHeader = false)
    {
        Type = type;
        DisplayName = displayName;
        ProfileId = profileId;
        TagName = tagName;
        FilterSettings = filterSettings;
        IsHeader = isHeader;
    }
}
