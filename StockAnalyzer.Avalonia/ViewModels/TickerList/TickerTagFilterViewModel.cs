using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using StockAnalyzer.Avalonia.Common;

namespace StockAnalyzer.Avalonia.ViewModels.TickerList;

/// <summary>
/// Backs the Tickers tab's right-column "Ticker / Tag" filter (Y:\Temp\sa_implementation_plan_TickerTagFilter_Redo.md
/// Phase 2). <see cref="FilterText"/> is applied directly as the filter condition on every change -
/// there is no separate "commit a suggestion" step - while <see cref="AvailableSuggestions"/> (ticker
/// symbols and registered tags combined) drives the AutoCompleteBox's suggestion popup.
/// </summary>
/// <remarks>
/// sa_improve (constraint-check follow-up, Y:\Temp\sa_constraint_check_TickerTagFilter_20260923.md):
/// takes its data source and filter-apply callback as constructor delegates rather than holding a
/// direct reference to the owning <c>TickerListViewModel</c>, so this type can be constructed and
/// unit-tested without a full owner instance.
/// </remarks>
public sealed partial class TickerTagFilterViewModel : ObservableObject
{
    private readonly Action<string?> _applyFilter;
    private readonly Func<IReadOnlyList<string>> _getTickerSymbols;
    private readonly Func<IEnumerable<string>> _getExistingTags;

    public BulkObservableCollection<TickerTagFilterSuggestion> AvailableSuggestions { get; } = new();

    [ObservableProperty]
    private string? _filterText;

    public TickerTagFilterViewModel(
        Action<string?> applyFilter,
        Func<IReadOnlyList<string>> getTickerSymbols,
        Func<IEnumerable<string>> getExistingTags)
    {
        _applyFilter = applyFilter ?? throw new ArgumentNullException(nameof(applyFilter));
        _getTickerSymbols = getTickerSymbols ?? throw new ArgumentNullException(nameof(getTickerSymbols));
        _getExistingTags = getExistingTags ?? throw new ArgumentNullException(nameof(getExistingTags));
    }

    partial void OnFilterTextChanged(string? value) => _applyFilter(value);

    /// <summary>Rebuilds <see cref="AvailableSuggestions"/> from the current ticker symbols and
    /// registered tags in one <see cref="BulkObservableCollection{T}.ReplaceRange"/> call (single Reset
    /// notification) instead of a Clear+per-item Add loop. Called whenever either source changes
    /// (ticker load, tag add/remove).</summary>
    public void RebuildAvailableSuggestions()
    {
        AvailableSuggestions.ReplaceRange(
            _getTickerSymbols().Select(TickerTagFilterSuggestion.ForTicker)
                .Concat(_getExistingTags().Select(TickerTagFilterSuggestion.ForTag)));
    }
}
