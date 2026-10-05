using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace StockAnalyzer.Avalonia.ViewModels.Dialogs;

/// <summary>
/// Represents a single column item in the Backtest Trade History column chooser.
/// </summary>
public class BacktestTradeColumnItemViewModel : ViewModelBase
{
    private bool _isActive;
    private readonly Func<BacktestTradeColumnItemViewModel, bool, bool>? _canChangeActivePredicate;

    public string ColumnId { get; }
    public string DisplayName { get; }
    public string Description { get; }

    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value) return;
            if (_canChangeActivePredicate != null && !_canChangeActivePredicate(this, value))
            {
                // Invariant: At least 1 active column must remain (ActiveCount >= 1).
                OnPropertyChanged(nameof(IsActive));
                return;
            }
            _isActive = value;
            OnPropertyChanged(nameof(IsActive));
        }
    }

    public BacktestTradeColumnItemViewModel(
        string columnId,
        string displayName,
        string description,
        bool isActive,
        Func<BacktestTradeColumnItemViewModel, bool, bool>? canChangeActivePredicate = null)
    {
        ColumnId = columnId ?? throw new ArgumentNullException(nameof(columnId));
        DisplayName = displayName ?? columnId;
        Description = description ?? string.Empty;
        _isActive = isActive;
        _canChangeActivePredicate = canChangeActivePredicate;
    }
}
