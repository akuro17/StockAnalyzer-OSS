using System;

namespace StockAnalyzer.Core.Models.Watchlist;

/// <summary>
/// Centralized constants for the Watchlist implementation.
/// </summary>
public static class WatchlistConstants
{
    // Persistence & Identity
    public static readonly Guid AllTickersProfileId = new Guid("A1111111-1111-1111-1111-111111111111");
    
    // Performance
    public const int HydrationBatchSize = 20;

    // Search & Symbol Constraints
    public const int MaxSearchSuggestions = 10;
    public const int MaxTickerLength = 10;
    public const int MaxRelativePerformanceTargets = 10;

    // Display Formats
    public const string FormatDecimal = "N2";
    public const string FormatInteger = "N0";
    public const string FormatPercent = "F2";
    public const string PercentSuffix = "%";

    // Default Column Widths
    public const string WidthSelect = "40";
    public const string WidthSymbol = "70";
    public const string WidthName = "150";
    public const string WidthSector = "100";
    public const string WidthIndustry = "100";
    public const string WidthPrice = "70";
    public const string WidthVolume = "80";
    public const string WidthChange = "70";
    public const string WidthRatio = "60";
}
