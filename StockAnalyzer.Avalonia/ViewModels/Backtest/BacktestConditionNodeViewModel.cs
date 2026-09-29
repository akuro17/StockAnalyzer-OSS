using CommunityToolkit.Mvvm.ComponentModel;
using StockAnalyzer.Core.Models.Backtest.Engine;

namespace StockAnalyzer.Avalonia.ViewModels.Backtest;

/// <summary>
/// Editable view-model node of the condition tree editor. The editor never mutates Core objects: the tree of these nodes is converted to the
/// immutable <see cref="BacktestConditionTree"/> in one step (<see cref="BacktestConditionTreeViewModel.Build"/>). Nodes are added, removed and
/// re-operated only through <see cref="BacktestConditionTreeViewModel"/>, which owns the bound checks and keeps <see cref="Parent"/> consistent.
/// </summary>
public abstract partial class BacktestConditionNodeViewModel : ObservableObject
{
    /// <summary>The containing group; null for a root and for a node that has been removed from the tree.</summary>
    public BacktestConditionGroupViewModel? Parent { get; internal set; }

    /// <summary>
    /// Two-way bound to the tree view item's selection. <see cref="BacktestConditionTreeViewModel.SelectedNode"/> is the single selected node of the
    /// whole editor: selecting a node here makes it the selected node, and a newly selected node clears this flag on the previous one.
    /// </summary>
    [ObservableProperty]
    private bool _isSelected;

    partial void OnIsSelectedChanged(bool value)
    {
        BacktestConditionTreeViewModel? owner = FindOwner();
        if (owner is null) return;

        if (value) owner.SelectedNode = this;
        else if (ReferenceEquals(owner.SelectedNode, this)) owner.SelectedNode = null;
    }

    /// <summary>The tree view-model this node is attached to (the commands of its context menu live there); null for a node that has been removed.</summary>
    public BacktestConditionTreeViewModel? Tree => FindOwner();

    private BacktestConditionTreeViewModel? FindOwner()
    {
        BacktestConditionNodeViewModel top = this;
        while (top.Parent is not null) top = top.Parent;
        return (top as BacktestConditionGroupViewModel)?.Owner;
    }

    [ObservableProperty]
    private bool _isExpanded = true;

    /// <summary>Hover text: the expression one level above this node (its parent's children joined by the parent's operator, this node shown by name); null when there is no level above.</summary>
    [ObservableProperty]
    private string? _hoverUpperText;

    /// <summary>Hover text: the expression one level below (a group: its own children; a leaf: the list it belongs to); null when the node has nothing below. Sub-groups appear by name, never expanded.</summary>
    [ObservableProperty]
    private string? _hoverLowerText;
}
