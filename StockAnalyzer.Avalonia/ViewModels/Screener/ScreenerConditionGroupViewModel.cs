using System.Collections.Immutable;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using StockAnalyzer.Core.Models.Screener;

namespace StockAnalyzer.Avalonia.ViewModels.Screener;

/// <summary>
/// An AND/OR group of the Filters condition tree editor. Unlike <c>BacktestConditionGroupViewModel</c>, there is no
/// (Section, Side) pair here: <see cref="ScreenerConditionTreeViewModel"/> has exactly one fixed root (decision B of
/// Y:\Temp\sa_implementation_plan_ScreenerFilterConditionTree.md), so <see cref="IsRoot"/> alone distinguishes it from
/// a nested group. A root is fixed: it is never removed, only cleared.
/// </summary>
public sealed partial class ScreenerConditionGroupViewModel : ScreenerConditionNodeViewModel
{
    internal ScreenerConditionGroupViewModel(bool isRoot, LogicalOperator @operator = LogicalOperator.And)
    {
        IsRoot = isRoot;
        _operator = @operator;
        Children = new ReadOnlyObservableCollection<ScreenerConditionNodeViewModel>(MutableChildren);
        MutableChildren.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmptyRoot));
    }

    /// <summary>The tree view-model that owns this group; set only on the single fixed root.</summary>
    internal ScreenerConditionTreeViewModel? Owner { get; set; }

    public bool IsRoot { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAndOperator))]
    private LogicalOperator _operator;

    /// <summary>The user-entered name of a nested group, or null (unnamed). The fixed root never has one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasName))]
    private string? _name;

    public bool HasName => Name is not null;

    /// <summary>True for AND, false for OR (a row shows one of the two localized operator texts).</summary>
    public bool IsAndOperator => Operator == LogicalOperator.And;

    /// <summary>The root without a condition does nothing (no Filters registered); its row says so.</summary>
    public bool IsEmptyRoot => IsRoot && Children.Count == 0;

    /// <summary>Why "add group" is disabled here (the limit text), or null while it is possible; kept up to date by <see cref="ScreenerConditionTreeViewModel"/>.</summary>
    [ObservableProperty]
    private string? _addGroupToolTip;

    /// <summary>Children in evaluation order (read-only; the tree view-model is the only writer, through <see cref="MutableChildren"/>).</summary>
    public ReadOnlyObservableCollection<ScreenerConditionNodeViewModel> Children { get; }

    /// <summary>The writable list behind <see cref="Children"/>; used by <see cref="ScreenerConditionTreeViewModel"/> only.</summary>
    internal ObservableCollection<ScreenerConditionNodeViewModel> MutableChildren { get; } = new();

    /// <summary>Group depth with the root group as 1 (the same definition the configured depth limit uses).</summary>
    public int Depth => Parent is null ? 1 : Parent.Depth + 1;

    internal ScreenerConditionGroup ToDomain()
    {
        System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();
        var children = ImmutableArray.CreateBuilder<IScreenerConditionNode>(Children.Count);
        foreach (ScreenerConditionNodeViewModel child in Children)
        {
            children.Add(child switch
            {
                ScreenerConditionGroupViewModel group => group.ToDomain(),
                ScreenerConditionLeafViewModel leaf => new ScreenerConditionLeaf(leaf.Entry),
                _ => throw new System.InvalidOperationException($"Unsupported condition node view-model {child.GetType().Name}."),
            });
        }
        return new ScreenerConditionGroup(Operator, children.MoveToImmutable(), Name);
    }
}
