using System;
using StockAnalyzer.Avalonia.Converters;
using StockAnalyzer.Core.Models.Backtest.Engine;

namespace StockAnalyzer.Avalonia.ViewModels.Backtest;

/// <summary>One comparison of the condition tree editor. The entry is not edited in place: a different comparison is a different leaf.</summary>
public sealed class BacktestConditionLeafViewModel : BacktestConditionNodeViewModel
{
    public BacktestConditionLeafViewModel(BacktestConditionEntry entry)
    {
        Entry = entry ?? throw new ArgumentNullException(nameof(entry));
        DisplayText = BacktestConditionFormatter.Format(entry);
    }

    public BacktestConditionEntry Entry { get; }

    public string DisplayText { get; }
}
