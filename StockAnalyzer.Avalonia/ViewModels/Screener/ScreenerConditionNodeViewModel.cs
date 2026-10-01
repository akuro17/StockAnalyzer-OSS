using CommunityToolkit.Mvvm.ComponentModel;

namespace StockAnalyzer.Avalonia.ViewModels.Screener;

/// <summary>
/// Editable view-model node of the Filters condition tree editor. Mirrors <c>BacktestConditionNodeViewModel</c>
/// (<c>StockAnalyzer.Avalonia.ViewModels.Backtest</c>) reduced to a single root (no Section/Side dimension - see
/// Y:\Temp\sa_implementation_plan_ScreenerFilterConditionTree.md section 1.2 for why this family is not shared with
/// the Backtest one). The editor never mutates Core objects: the tree of these nodes is converted to the immutable
/// <see cref="StockAnalyzer.Core.Models.Screener.ScreenerConditionGroup"/> in one step
/// (<see cref="ScreenerConditionTreeViewModel.Build"/>). Nodes are added, removed and re-operated only through
/// <see cref="ScreenerConditionTreeViewModel"/>, which owns the bound checks and keeps <see cref="Parent"/> consistent.
/// </summary>
public abstract partial class ScreenerConditionNodeViewModel : ObservableObject
{
    /// <summary>The containing group; null for the root and for a node that has been removed from the tree.</summary>
    public ScreenerConditionGroupViewModel? Parent { get; internal set; }

    /// <summary>
    /// Two-way bound to the tree view item's selection. <see cref="ScreenerConditionTreeViewModel.SelectedNode"/> is the single
    /// selected node of the editor: selecting a node here makes it the selected node, and a newly selected node clears this flag
    /// on the previous one.
    /// </summary>
    [ObservableProperty]
    private bool _isSelected;

    partial void OnIsSelectedChanged(bool value)
    {
        ScreenerConditionTreeViewModel? owner = FindOwner();
        if (owner is null) return;

        if (value) owner.SelectedNode = this;
        else if (ReferenceEquals(owner.SelectedNode, this)) owner.SelectedNode = null;
    }

    /// <summary>The tree view-model this node is attached to (the commands of its context menu live there); null for a removed node.</summary>
    public ScreenerConditionTreeViewModel? Tree => FindOwner();

    private ScreenerConditionTreeViewModel? FindOwner()
    {
        ScreenerConditionNodeViewModel top = this;
        while (top.Parent is not null) top = top.Parent;
        return (top as ScreenerConditionGroupViewModel)?.Owner;
    }

    [ObservableProperty]
    private bool _isExpanded = true;

    /// <summary>Hover text: the expression one level above this node (its parent's children joined by the parent's operator, this node shown by name); null when there is no level above.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHoverText))]
    private string? _hoverUpperText;

    /// <summary>Hover text: the expression one level below (a group: its own children; a leaf: the list it belongs to); null when the node has nothing below. Sub-groups appear by name, never expanded.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHoverText))]
    private string? _hoverLowerText;

    /// <summary>False for an empty list (no conditions): the row has nothing to show on hover, so it must not open an empty tooltip.</summary>
    public bool HasHoverText => HoverUpperText is not null || HoverLowerText is not null;
}
