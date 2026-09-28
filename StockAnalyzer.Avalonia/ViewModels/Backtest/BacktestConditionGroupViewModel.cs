using System;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Screener;

namespace StockAnalyzer.Avalonia.ViewModels.Backtest;

/// <summary>
/// An AND/OR group of the condition tree editor. Every group knows the (section, side) root it lives under, so the add target rule
/// (a selected group is the target only when both match the requested button) needs no tree walk. A root is fixed: it is never removed, only cleared.
/// </summary>
public sealed partial class BacktestConditionGroupViewModel : BacktestConditionNodeViewModel
{
    internal BacktestConditionGroupViewModel(BacktestConditionSection section, TradeSide side, bool isRoot, LogicalOperator @operator = LogicalOperator.And)
    {
        Section = section;
        Side = side;
        IsRoot = isRoot;
        _operator = @operator;
        Children = new ReadOnlyObservableCollection<BacktestConditionNodeViewModel>(MutableChildren);
        MutableChildren.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmptyRoot));
    }

    /// <summary>The tree view-model that owns this root; set only on the six fixed roots.</summary>
    internal BacktestConditionTreeViewModel? Owner { get; set; }

    public BacktestConditionSection Section { get; }

    public TradeSide Side { get; }

    public bool IsRoot { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAndOperator))]
    private LogicalOperator _operator;

    /// <summary>The user-entered name of a nested group, or null (unnamed). A fixed root never has one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasName))]
    private string? _name;

    public bool HasName => Name is not null;

    /// <summary>True for AND, false for OR (a row shows one of the two localized operator texts).</summary>
    public bool IsAndOperator => Operator == LogicalOperator.And;

    public bool IsLongSide => Side == TradeSide.Long;

    /// <summary>A Reverse root carries an extra explanation (it acts only while the opposite side is held).</summary>
    public bool IsReverseRoot => IsRoot && Section == BacktestConditionSection.Reverse;

    /// <summary>A root without a condition does nothing; its row says so.</summary>
    public bool IsEmptyRoot => IsRoot && Children.Count == 0;

    /// <summary>Why "add group" is disabled here (the limit text), or null while it is possible; kept up to date by <see cref="BacktestConditionTreeViewModel"/>.</summary>
    [ObservableProperty]
    private string? _addGroupToolTip;

    /// <summary>Children in evaluation/storage order (read-only; the tree view-model is the only writer, through <see cref="MutableChildren"/>).</summary>
    public ReadOnlyObservableCollection<BacktestConditionNodeViewModel> Children { get; }

    /// <summary>The writable list behind <see cref="Children"/>; used by <see cref="BacktestConditionTreeViewModel"/> only.</summary>
    internal ObservableCollection<BacktestConditionNodeViewModel> MutableChildren { get; } = new();

    /// <summary>Group depth with the root group as 1 (the same definition the configured depth limit uses).</summary>
    public int Depth => Parent is null ? 1 : Parent.Depth + 1;

    internal BacktestConditionGroup ToDomain()
    {
        System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();
        var children = ImmutableArray.CreateBuilder<IBacktestConditionNode>(Children.Count);
        foreach (BacktestConditionNodeViewModel child in Children)
        {
            children.Add(child switch
            {
                BacktestConditionGroupViewModel group => group.ToDomain(),
                BacktestConditionLeafViewModel leaf => new BacktestConditionLeaf(leaf.Entry),
                _ => throw new InvalidOperationException($"Unsupported condition node view-model {child.GetType().Name}."),
            });
        }
        return new BacktestConditionGroup(Operator, children.MoveToImmutable(), Name);
    }
}
