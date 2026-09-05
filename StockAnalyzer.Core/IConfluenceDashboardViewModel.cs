using StockAnalyzer.Core.Models.Confluence;
using System.Collections.ObjectModel;
using System;

namespace StockAnalyzer.Core;

/// <summary>
/// Domain interface for the Confluence Dashboard ViewModel.
/// Defines the contract for latest-candle data orchestration.
/// </summary>
public interface IConfluenceDashboardViewModel : IDisposable
{
    /// <summary>
    /// Gets the global confluence score for the latest candle.
    /// </summary>
    double TotalScore { get; }

    /// <summary>
    /// Gets the collection of indicators and their latest status.
    /// Pre-allocated and updated in-place.
    /// </summary>
    ObservableCollection<IndicatorDashboardItem> IndicatorItems { get; }

    /// <summary>
    /// Force an update of the dashboard data from the latest snapshot.
    /// </summary>
    void UpdateLatestData();
}
