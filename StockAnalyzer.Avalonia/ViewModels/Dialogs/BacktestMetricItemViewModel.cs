using System;
using CommunityToolkit.Mvvm.ComponentModel;
using StockAnalyzer.Avalonia.ViewModels.Backtest;

namespace StockAnalyzer.Avalonia.ViewModels.Dialogs;

/// <summary>
/// Represents a single metric item in the Backtest Metrics column chooser.
/// </summary>
public class BacktestMetricItemViewModel : ViewModelBase
{
    private bool _isActive;
    private readonly Func<BacktestMetricItemViewModel, bool, bool>? _canChangeActivePredicate;

    public string MetricName { get; }
    internal BacktestMetricGroup Group { get; }
    public string DisplayName { get; }
    public string GroupDisplayName { get; }
    public string Description { get; }

    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value) return;
            if (_canChangeActivePredicate != null && !_canChangeActivePredicate(this, value))
            {
                // Invariant: At least 1 active metric must remain (ActiveCount >= 1).
                OnPropertyChanged(nameof(IsActive));
                return;
            }
            _isActive = value;
            OnPropertyChanged(nameof(IsActive));
        }
    }

    internal BacktestMetricItemViewModel(
        string metricName,
        BacktestMetricGroup group,
        string displayName,
        string groupDisplayName,
        string description,
        bool isActive,
        Func<BacktestMetricItemViewModel, bool, bool>? canChangeActivePredicate = null)
    {
        MetricName = metricName ?? throw new ArgumentNullException(nameof(metricName));
        Group = group;
        DisplayName = displayName ?? metricName;
        GroupDisplayName = groupDisplayName ?? group.ToString();
        Description = description ?? string.Empty;
        _isActive = isActive;
        _canChangeActivePredicate = canChangeActivePredicate;
    }
}
