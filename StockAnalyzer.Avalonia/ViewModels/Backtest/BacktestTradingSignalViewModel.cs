using System;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace StockAnalyzer.Avalonia.ViewModels.Backtest;

/// <summary>
/// ViewModel for the BacktestWindow Tab 3 "Trading Signal" view: hosts the 2-column condition tree editor
/// (Long/Short parent containers containing Entry, Exit, and Reversal condition trees).
/// </summary>
public sealed partial class BacktestTradingSignalViewModel : ViewModelBase, IDisposable
{
    private readonly BacktestIndicatorSelectionViewModel? _selection;
    private bool _disposed;

    public BacktestConditionTreeViewModel ConditionTree { get; }

    [ObservableProperty]
    private string _conditionTreeSummaryText = string.Empty;

    [ObservableProperty]
    private string? _conditionTreeLimitWarning;

    public BacktestTradingSignalViewModel(BacktestConditionTreeViewModel conditionTree, BacktestIndicatorSelectionViewModel? selection = null)
    {
        ConditionTree = conditionTree ?? throw new ArgumentNullException(nameof(conditionTree));
        _selection = selection;

        if (_selection is not null)
        {
            _conditionTreeSummaryText = _selection.ConditionTreeSummaryText;
            _conditionTreeLimitWarning = _selection.ConditionTreeLimitWarning;
            _selection.PropertyChanged += OnSelectionPropertyChanged;
        }
    }

    private void OnSelectionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not BacktestIndicatorSelectionViewModel selection) return;
        if (e.PropertyName == nameof(BacktestIndicatorSelectionViewModel.ConditionTreeSummaryText))
        {
            ConditionTreeSummaryText = selection.ConditionTreeSummaryText;
        }
        else if (e.PropertyName == nameof(BacktestIndicatorSelectionViewModel.ConditionTreeLimitWarning))
        {
            ConditionTreeLimitWarning = selection.ConditionTreeLimitWarning;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_selection is not null)
        {
            _selection.PropertyChanged -= OnSelectionPropertyChanged;
        }
    }
}
