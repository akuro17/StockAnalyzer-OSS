namespace StockAnalyzer.Core.Models.Watchlist;

/// <summary>
/// Platform-agnostic metadata for a table column.
/// Used to sync between ViewModel and UI components (like TreeDataGrid).
/// </summary>
public sealed record WatchlistColumnMetadata(
    string HeaderKey,
    string MemberName,
    string Width = "Auto",
    int Priority = 0);
