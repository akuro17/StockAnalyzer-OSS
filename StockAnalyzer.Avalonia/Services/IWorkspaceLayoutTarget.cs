using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.UI;
using StockAnalyzer.Core.Models.Settings;

namespace StockAnalyzer.Avalonia.Services;

/// <summary>
/// Defines the layout targets and actions required for workspace restoration and state capturing.
/// Fully decoupled from concrete view models (Dependency Inversion Principle).
/// </summary>
public interface IWorkspaceLayoutTarget
{
    double LeftPanelWidth { get; set; }
    double RightPanelWidth { get; set; }
    bool IsLeftPanelVisible { get; set; }
    bool IsRightPanelVisible { get; set; }
    double TopPanelHeight { get; set; }
    double BottomPanelHeight { get; set; }
    bool IsTopPanelVisible { get; set; }
    bool IsBottomPanelVisible { get; set; }
    bool IsLeftPanelPinned { get; set; }
    bool IsRightPanelPinned { get; set; }
    bool IsTopPanelPinned { get; set; }
    bool IsBottomPanelPinned { get; set; }

    int LeftSelectedTabIndex { get; set; }
    int RightSelectedTabIndex { get; set; }
    int TopSelectedTabIndex { get; set; }
    int BottomSelectedTabIndex { get; set; }

    ObservableCollection<WorkspaceViewItem> LeftPanelTabs { get; }
    ObservableCollection<WorkspaceViewItem> RightPanelTabs { get; }
    ObservableCollection<WorkspaceViewItem> TopPanelTabs { get; }
    ObservableCollection<WorkspaceViewItem> BottomPanelTabs { get; }

    string? SelectedTicker { get; set; }

    void ApplyPanelTabs(WorkspaceSettings settings);
    void RestoreDetachedTabs(WorkspaceSettings settings);
    void SetTimeframe(TimeframeType timeframe);
    void CaptureWorkspaceSettings(WorkspaceSettings settings);

    // Decoupled VM abstractions
    IReadOnlyList<CoreIndicatorSettings> GetIndicators();
    void ApplyIndicators(IEnumerable<CoreIndicatorSettings> indicators);
    void RefreshTickerListNodes();
    void SelectTickerListNodeById(Guid id);
    void RestoreChartSettings(WorkspaceSettings settings);
    void SetActiveColumns(IEnumerable<string> columnNames);
    void ApplyColumnWidths(Dictionary<string, string>? widths);
    void RestoreTickerColumnTemplateSelection(IReadOnlyDictionary<Guid, Guid>? selectionsByList);

    /// <summary>Restores the shared Column Customization selection (store only, not applied). The coordinator calls
    /// it before <see cref="RestoreTickerColumnTemplateSelection"/>, which applies the active store. Default no-op so
    /// existing implementers are unaffected.</summary>
    void RestoreTickerSharedColumnTemplateSelection(Guid templateId) { }
    void ApplySortState(string? columnName, int direction);
    void ImportFilterSettings(IEnumerable<FilterSettings> filters);
    void ImportCustomTickerCategories(IEnumerable<TickerParentCategorySettings> categories);
}
