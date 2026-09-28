using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StockAnalyzer.Avalonia.ViewModels.Backtest;
using StockAnalyzer.Core.Models.Screener;

namespace StockAnalyzer.Avalonia.ViewModels.Screener;

/// <summary>
/// Editor state of the Filters condition tree: a single fixed root group (decision B of
/// Y:\Temp\sa_implementation_plan_ScreenerFilterConditionTree.md - no Section/Side dimension, unlike
/// <c>BacktestConditionTreeViewModel</c>'s six fixed roots), the current selection, and the edit commands. The editor
/// works on this tree only (never on a flat list) and converts it with <see cref="Build"/>. The depth/node bounds
/// come from configuration; a command whose result would exceed them is disabled. An empty nested group is a
/// transient editing state (removed automatically when a delete empties it).
/// Reuses the existing, domain-agnostic Add/Edit Group dialog contract (<see cref="ConditionGroupEditRequest"/> /
/// <see cref="ConditionGroupEditResult"/> / <c>IDialogService.ShowConditionGroupEditDialogAsync</c>) that
/// <c>BacktestConditionTreeViewModel</c> already uses - those types carry no Backtest-specific coupling, so this is a
/// safe reuse rather than a new dialog. The same reasoning applies to <see cref="MoveLeaf"/>'s
/// <see cref="ConditionDropPlacement"/>/<see cref="ConditionMoveResult"/> parameter/return types
/// (Y:\Temp\sa_implementation_plan_ScreenerFilterConditionDragMove.md).
/// </summary>
public sealed partial class ScreenerConditionTreeViewModel : ObservableObject
{
    private readonly Func<string> _limitReason;
    private readonly Func<ConditionGroupEditRequest, Task<ConditionGroupEditResult?>>? _groupEditor;
    private readonly Func<string> _staleEditReason;
    private int _revision;

    /// <param name="limitReason">The localized text explaining that a limit was reached (shown as the tooltip of a disabled "add group"); the view-model itself holds no localization.</param>
    /// <param name="maxNameLength">Longest group name; the configured <c>Screener:MaxFilterGroupNameLength</c>.</param>
    /// <param name="groupEditor">Opens the Add/Edit Group dialog; null = the Add/Edit Group commands do nothing (no dialog available).</param>
    /// <param name="staleEditReason">The localized text explaining that a dialog result was discarded because the tree changed while the dialog was open.</param>
    /// <param name="dragStartDistance">Pointer travel (DIP) that turns a press on a condition row into a drag; null = the configured default (<c>Screener:ConditionDragStartDistance</c>).</param>
    public ScreenerConditionTreeViewModel(
        int maxDepth,
        int maxNodes,
        int maxNameLength,
        Func<string>? limitReason = null,
        Func<ConditionGroupEditRequest, Task<ConditionGroupEditResult?>>? groupEditor = null,
        Func<string>? staleEditReason = null,
        int? dragStartDistance = null)
    {
        if (maxDepth < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDepth), maxDepth, "maxDepth must be >= 1.");
        }
        if (maxNodes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxNodes), maxNodes, "maxNodes must be >= 1.");
        }
        if (maxNameLength < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxNameLength), maxNameLength, "maxNameLength must be >= 1.");
        }
        int dragDistance = dragStartDistance ?? new StockAnalyzer.Avalonia.Common.ScreenerSettings().ConditionDragStartDistance;
        if (dragDistance < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(dragStartDistance), dragStartDistance, "dragStartDistance must be >= 1.");
        }

        _limitReason = limitReason ?? (() => string.Empty);
        _groupEditor = groupEditor;
        _staleEditReason = staleEditReason ?? (() => string.Empty);
        DragStartDistance = dragDistance;
        MaxNameLength = maxNameLength;
        MaxDepth = maxDepth;
        MaxNodes = maxNodes;
        Root = new ScreenerConditionGroupViewModel(isRoot: true) { Owner = this };
        Roots = new[] { Root };
        RefreshAllDisplayState();
    }

    /// <summary>Raised after every change of the tree structure (add, delete, load); the operator switch does not change the structure.</summary>
    public event EventHandler? StructureChanged;

    /// <summary>Raised with the localized reason when a confirmed dialog result is not applied (the tree changed while the dialog was open); the tree is unchanged.</summary>
    public event EventHandler<string>? EditRejected;

    /// <summary>
    /// Raised when <see cref="Delete"/> removes a leaf (never for a group or a root clear). The single owner of "a Filter no
    /// longer exists at all" stays <see cref="StockAnalyzer.Avalonia.ViewModels.IndicatorRegistrationViewModel.RegisteredEntries"/>;
    /// this lets that collection remove the entry too when the deletion is initiated from the tree's own context menu
    /// (Delete Node) - the only remaining removal path now that the flat list's own per-row delete button (and its
    /// <c>RemoveEntryCommand</c>) was retired along with that list - so an entry can never be left orphaned in
    /// <c>RegisteredEntries</c> with no corresponding tree leaf.
    /// </summary>
    public event EventHandler<ScreenerIndicatorEntry>? LeafDeleted;

    /// <summary>Counts the changes of the tree (add, delete, load, group edit); a dialog result is applied only while it is unchanged since the dialog opened.</summary>
    public int Revision => _revision;

    /// <summary>Deepest group nesting allowed (the root group is 1); the configured <c>Screener:MaxFilterTreeDepth</c>.</summary>
    public int MaxDepth { get; }

    /// <summary>Pointer travel (DIP) that starts a drag of a condition; the configured <c>Screener:ConditionDragStartDistance</c>.</summary>
    public int DragStartDistance { get; }

    /// <summary>Longest group name; the configured <c>Screener:MaxFilterGroupNameLength</c>.</summary>
    public int MaxNameLength { get; }

    /// <summary>Most nodes per root, root included; the configured <c>Screener:MaxFilterTreeNodes</c>.</summary>
    public int MaxNodes { get; }

    /// <summary>The single fixed root of the Filters tree (the item of its tree view).</summary>
    public ScreenerConditionGroupViewModel Root { get; }

    /// <summary>The single-element view of <see cref="Root"/> as a list, for binding to a <c>TreeView.ItemsSource</c>
    /// the same way Backtest's multi-root editor binds its <c>EntryRoots</c>/<c>ExitRoots</c>/<c>ReverseRoots</c>.</summary>
    public IReadOnlyList<ScreenerConditionGroupViewModel> Roots { get; }

    /// <summary>The single selected node of the editor; null when nothing is selected.</summary>
    [ObservableProperty]
    private ScreenerConditionNodeViewModel? _selectedNode;

    partial void OnSelectedNodeChanged(ScreenerConditionNodeViewModel? oldValue, ScreenerConditionNodeViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
    }

    /// <summary>Snapshot of the editor as an immutable tree.</summary>
    public ScreenerConditionGroup Build()
    {
        try
        {
            return Root.ToDomain();
        }
        catch (InsufficientExecutionStackException)
        {
            throw new ArgumentException("The condition tree is nested too deeply to be processed.");
        }
    }

    /// <summary>Replaces the editor content with <paramref name="tree"/> and clears the selection. Rejects a tree deeper than
    /// <see cref="MaxDepth"/> or with more nodes than <see cref="MaxNodes"/> (constraint-check finding, 2026-09-27: unlike
    /// <see cref="TryAddGroup"/>/<see cref="TryAddLeaf"/>, this used to attach an untrusted tree with no bound check at all).</summary>
    public void Load(ScreenerConditionGroup tree)
    {
        ArgumentNullException.ThrowIfNull(tree);

        // Prepare the candidate on a scratch group first: a failure (stack depth or bounds, below) leaves the current content and selection untouched.
        var candidate = new ScreenerConditionGroupViewModel(isRoot: true);
        try
        {
            AttachChildren(candidate, tree);
        }
        catch (InsufficientExecutionStackException)
        {
            throw new ArgumentException("The condition tree is nested too deeply to be processed.", nameof(tree));
        }

        ScreenerConditionTreeSize size = MeasureGroup(candidate, depth: 1);
        if (size.Depth > MaxDepth)
        {
            throw new ArgumentException($"The condition tree is nested {size.Depth} levels deep, exceeding the configured limit of {MaxDepth}.", nameof(tree));
        }
        if (size.NodeCount > MaxNodes)
        {
            throw new ArgumentException($"The condition tree has {size.NodeCount} nodes, exceeding the configured limit of {MaxNodes}.", nameof(tree));
        }

        foreach (ScreenerConditionNodeViewModel old in Root.Children) old.Parent = null;
        Root.MutableChildren.Clear();
        Root.Operator = tree.Operator;
        foreach (ScreenerConditionNodeViewModel child in candidate.Children.ToArray())
        {
            child.Parent = Root;
            Root.MutableChildren.Add(child);
        }

        SelectedNode = null;
        RaiseCommandStates();
    }

    private static void AttachChildren(ScreenerConditionGroupViewModel target, ScreenerConditionGroup source)
    {
        System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();
        foreach (IScreenerConditionNode child in source.Children)
        {
            ScreenerConditionNodeViewModel node;
            switch (child)
            {
                case ScreenerConditionGroup group:
                    var nested = new ScreenerConditionGroupViewModel(isRoot: false, group.Operator) { Name = group.Name };
                    nested.Parent = target;
                    AttachChildren(nested, group);
                    node = nested;
                    break;
                case ScreenerConditionLeaf leaf:
                    node = new ScreenerConditionLeafViewModel(leaf.Entry);
                    break;
                default:
                    throw new ArgumentException($"Unsupported condition node type {child.GetType().Name}.", nameof(source));
            }
            node.Parent = target;
            target.MutableChildren.Add(node);
        }
    }

    // ---- bounds ----

    private ScreenerConditionTreeSize Measure() => MeasureGroup(Root, depth: 1);

    private static ScreenerConditionTreeSize MeasureGroup(ScreenerConditionGroupViewModel group, int depth)
    {
        int nodeCount = 1;
        int deepest = depth;
        foreach (ScreenerConditionNodeViewModel child in group.Children)
        {
            if (child is ScreenerConditionGroupViewModel nested)
            {
                ScreenerConditionTreeSize nestedSize = MeasureGroup(nested, depth + 1);
                deepest = Math.Max(deepest, nestedSize.Depth);
                nodeCount += nestedSize.NodeCount;
            }
            else
            {
                nodeCount++;
            }
        }
        return new ScreenerConditionTreeSize(deepest, nodeCount);
    }

    private readonly record struct ScreenerConditionTreeSize(int Depth, int NodeCount);

    /// <summary>True when one more leaf fits under <paramref name="group"/> (the tree's node count stays within <see cref="MaxNodes"/>).</summary>
    public bool CanAddLeaf(ScreenerConditionGroupViewModel? group)
        => group is not null && IsAttached(group) && Measure().NodeCount < MaxNodes;

    /// <summary>True when one more (initially empty) group fits under <paramref name="group"/> within both <see cref="MaxNodes"/> and <see cref="MaxDepth"/>.</summary>
    public bool CanAddGroup(ScreenerConditionGroupViewModel? group)
        => group is not null && IsAttached(group) && group.Depth < MaxDepth && Measure().NodeCount < MaxNodes;

    // ---- edit operations ----

    /// <summary>The group a new leaf is added to: the selected group when one is selected, otherwise the root.</summary>
    public ScreenerConditionGroupViewModel ResolveAddTarget()
        => SelectedNode is ScreenerConditionGroupViewModel selected ? selected : Root;

    /// <summary>
    /// Adds a leaf for <paramref name="entry"/> to <see cref="ResolveAddTarget"/> and returns that target; null (nothing added) when the node bound is reached.
    /// </summary>
    public ScreenerConditionGroupViewModel? TryAddLeaf(ScreenerIndicatorEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ScreenerConditionGroupViewModel target = ResolveAddTarget();
        if (!CanAddLeaf(target)) return null;

        AddChild(target, new ScreenerConditionLeafViewModel(entry));
        return target;
    }

    /// <summary>
    /// Adds an empty group with <paramref name="operator"/> and the optional <paramref name="name"/> to <paramref name="parent"/>; false (nothing added) when a bound is reached.
    /// </summary>
    public bool TryAddGroup(ScreenerConditionGroupViewModel parent, LogicalOperator @operator, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(parent);
        EnsureAttached(parent);
        EnsureDefined(@operator);
        string? normalized = CheckedName(name);
        if (!CanAddGroup(parent)) return false;

        AddChild(parent, new ScreenerConditionGroupViewModel(isRoot: false, @operator) { Name = normalized });
        return true;
    }

    private void EnsureAttached(ScreenerConditionNodeViewModel node)
    {
        if (!IsAttached(node))
        {
            throw new ArgumentException("The node does not belong to this condition tree.", nameof(node));
        }
    }

    private static void EnsureDefined(LogicalOperator @operator)
    {
        if (!Enum.IsDefined(@operator))
        {
            throw new ArgumentOutOfRangeException(nameof(@operator), @operator, "Undefined logical operator.");
        }
    }

    private string? CheckedName(string? name)
    {
        string? normalized = ScreenerConditionGroup.NormalizeName(name);
        if (normalized is not null && normalized.Length > MaxNameLength)
        {
            throw new ArgumentException($"A group name may have at most {MaxNameLength} characters.", nameof(name));
        }
        return normalized;
    }

    private void AddChild(ScreenerConditionGroupViewModel parent, ScreenerConditionNodeViewModel child)
    {
        child.Parent = parent;
        parent.MutableChildren.Add(child);
        parent.IsExpanded = true;
        RaiseCommandStates();
    }

    /// <summary>Changes the name and the AND/OR operator of a group (the Edit Group dialog). The fixed root has no name: passing one is rejected.</summary>
    public void UpdateGroup(ScreenerConditionGroupViewModel group, string? name, LogicalOperator @operator)
    {
        ArgumentNullException.ThrowIfNull(group);
        EnsureAttached(group);
        EnsureDefined(@operator);
        string? normalized = CheckedName(name);
        if (group.IsRoot && normalized is not null)
        {
            throw new ArgumentException("The fixed root cannot have a name.", nameof(name));
        }

        group.Name = normalized;
        group.Operator = @operator;
        RaiseCommandStates();
    }

    /// <summary>
    /// Deletes <paramref name="node"/> with its subtree; the root is only cleared (it is fixed). A non-root group that a delete leaves empty is removed as well,
    /// upwards, so the tree stays valid without the user cleaning up. The selection is dropped when the selected node was removed.
    /// </summary>
    public void Delete(ScreenerConditionNodeViewModel node)
    {
        ArgumentNullException.ThrowIfNull(node);
        EnsureAttached(node);
        if (node is ScreenerConditionGroupViewModel { IsRoot: true } root)
        {
            foreach (ScreenerConditionNodeViewModel child in root.Children) child.Parent = null;
            root.MutableChildren.Clear();
        }
        else if (node.Parent is { } parent)
        {
            parent.MutableChildren.Remove(node);
            node.Parent = null;
            RemoveEmptiedGroups(parent);
        }

        if (SelectedNode is { } selected && !IsAttached(selected)) SelectedNode = null;
        RaiseCommandStates();

        if (node is ScreenerConditionLeafViewModel deletedLeaf)
        {
            LeafDeleted?.Invoke(this, deletedLeaf.Entry);
        }
    }

    /// <summary>Removes a leaf whose <see cref="ScreenerConditionLeafViewModel.Entry"/> is <paramref name="entry"/> wherever it is in the tree, if present.</summary>
    public void RemoveLeafFor(ScreenerIndicatorEntry entry)
    {
        ScreenerConditionLeafViewModel? found = FindLeaf(Root, entry);
        if (found is not null) Delete(found);
    }

    private static ScreenerConditionLeafViewModel? FindLeaf(ScreenerConditionGroupViewModel group, ScreenerIndicatorEntry entry)
    {
        foreach (ScreenerConditionNodeViewModel child in group.Children)
        {
            if (child is ScreenerConditionLeafViewModel leaf && ReferenceEquals(leaf.Entry, entry)) return leaf;
            if (child is ScreenerConditionGroupViewModel nested)
            {
                ScreenerConditionLeafViewModel? inNested = FindLeaf(nested, entry);
                if (inNested is not null) return inNested;
            }
        }
        return null;
    }

    /// <summary>True when <paramref name="node"/> is reachable from the root and every parent link matches the parent's child list.</summary>
    private bool IsAttached(ScreenerConditionNodeViewModel node)
    {
        ScreenerConditionNodeViewModel current = node;
        while (current.Parent is { } parent)
        {
            if (!parent.Children.Contains(current)) return false;
            current = parent;
        }
        return ReferenceEquals(current, Root);
    }

    private static void RemoveEmptiedGroups(ScreenerConditionGroupViewModel container)
    {
        while (!container.IsRoot && container.Children.Count == 0 && container.Parent is { } up)
        {
            up.MutableChildren.Remove(container);
            container.Parent = null;
            container = up;
        }
    }

    // ---- move (drag and drop; Y:\Temp\sa_implementation_plan_ScreenerFilterConditionDragMove.md Task 2) ----

    private readonly record struct MovePlan(ScreenerConditionGroupViewModel SourceParent, int SourceIndex, ScreenerConditionGroupViewModel Destination, int DestinationIndex);

    /// <summary>
    /// Says what <see cref="MoveLeaf"/> would do, without changing anything. Mirrors <c>BacktestConditionTreeViewModel.EvaluateMove</c>: a move is allowed
    /// only for one comparison inside the single fixed root; the destination may be any group of it and any position in it.
    /// </summary>
    public ConditionMoveResult EvaluateMove(ScreenerConditionNodeViewModel? leaf, ScreenerConditionNodeViewModel? target, ConditionDropPlacement placement)
        => Plan(leaf, target, placement, out _);

    /// <summary>
    /// Moves <paramref name="leaf"/> next to (<see cref="ConditionDropPlacement.Before"/>/<see cref="ConditionDropPlacement.After"/>) or into (<see cref="ConditionDropPlacement.Inside"/>,
    /// appended) <paramref name="target"/> as one edit: the leaf is re-parented, a non-root group the move empties is removed upwards, the destination is opened and the leaf stays selected.
    /// A rejected or unchanged move leaves the tree exactly as it was (no edit is counted).
    /// </summary>
    public ConditionMoveResult MoveLeaf(ScreenerConditionNodeViewModel? leaf, ScreenerConditionNodeViewModel? target, ConditionDropPlacement placement)
    {
        ConditionMoveResult result = Plan(leaf, target, placement, out MovePlan plan);
        if (result != ConditionMoveResult.Moved) return result;

        ScreenerConditionNodeViewModel moved = leaf!;
        if (ReferenceEquals(plan.SourceParent, plan.Destination))
        {
            plan.Destination.MutableChildren.Move(plan.SourceIndex, plan.DestinationIndex);
        }
        else
        {
            plan.SourceParent.MutableChildren.RemoveAt(plan.SourceIndex);
            moved.Parent = plan.Destination;
            plan.Destination.MutableChildren.Insert(plan.DestinationIndex, moved);
            RemoveEmptiedGroups(plan.SourceParent);
        }

        plan.Destination.IsExpanded = true;
        SelectedNode = null;
        SelectedNode = moved;
        RaiseCommandStates();
        return ConditionMoveResult.Moved;
    }

    private ConditionMoveResult Plan(ScreenerConditionNodeViewModel? leaf, ScreenerConditionNodeViewModel? target, ConditionDropPlacement placement, out MovePlan plan)
    {
        plan = default;
        if (leaf is not ScreenerConditionLeafViewModel || target is null || !Enum.IsDefined(placement)) return ConditionMoveResult.Rejected;
        if (leaf.Parent is not { } sourceParent || !IsAttached(leaf) || !IsAttached(target)) return ConditionMoveResult.Rejected;

        ScreenerConditionGroupViewModel destination;
        int index; // position in the destination's list before the leaf is taken out
        switch (placement)
        {
            case ConditionDropPlacement.Inside:
                if (target is not ScreenerConditionGroupViewModel group) return ConditionMoveResult.Rejected;
                destination = group;
                index = group.Children.Count;
                break;
            default:
                if (ReferenceEquals(target, leaf)) return ConditionMoveResult.Unchanged;
                if (target.Parent is not { } siblingParent) return ConditionMoveResult.Rejected;
                destination = siblingParent;
                index = siblingParent.Children.IndexOf(target) + (placement == ConditionDropPlacement.After ? 1 : 0);
                break;
        }

        // Screener has a single fixed root (decision B), so this equality always holds for two attached nodes; kept for
        // structural symmetry with BacktestConditionTreeViewModel.Plan and as a safety net should the tree ever gain more than one root.
        if (!ReferenceEquals(RootOf(destination), RootOf(sourceParent))) return ConditionMoveResult.Rejected;

        int sourceIndex = sourceParent.Children.IndexOf(leaf);
        if (ReferenceEquals(destination, sourceParent) && sourceIndex < index) index--;
        if (ReferenceEquals(destination, sourceParent) && sourceIndex == index) return ConditionMoveResult.Unchanged;

        plan = new MovePlan(sourceParent, sourceIndex, destination, index);
        return ConditionMoveResult.Moved;
    }

    private static ScreenerConditionGroupViewModel RootOf(ScreenerConditionNodeViewModel node)
    {
        ScreenerConditionNodeViewModel top = node;
        while (top.Parent is not null) top = top.Parent;
        return (ScreenerConditionGroupViewModel)top;
    }

    // ---- commands (parameter: the node the context menu was opened on) ----

    /// <summary>Opens the Add Group dialog (name + AND/OR) and adds the confirmed group under <paramref name="group"/>.</summary>
    [RelayCommand(CanExecute = nameof(CanAddGroup))]
    private async Task AddGroupAsync(ScreenerConditionGroupViewModel? group)
    {
        if (group is null || _groupEditor is null) return;

        int revision = _revision;
        ConditionGroupEditResult? result = await _groupEditor(new ConditionGroupEditRequest(IsNew: true, Name: null, LogicalOperator.And, AllowName: true, MaxNameLength));
        if (result is null) return;
        if (revision != _revision || !IsAttached(group))
        {
            EditRejected?.Invoke(this, _staleEditReason());
            return;
        }
        TryAddGroup(group, result.Operator, result.Name);
    }

    /// <summary>Opens the same dialog with the group's current name and operator and applies the confirmed values.</summary>
    [RelayCommand(CanExecute = nameof(IsAttachedGroup))]
    private async Task EditGroupAsync(ScreenerConditionGroupViewModel? group)
    {
        if (group is null || _groupEditor is null) return;

        int revision = _revision;
        ConditionGroupEditResult? result = await _groupEditor(new ConditionGroupEditRequest(IsNew: false, group.Name, group.Operator, AllowName: !group.IsRoot, MaxNameLength));
        if (result is null) return;
        if (revision != _revision || !IsAttached(group))
        {
            EditRejected?.Invoke(this, _staleEditReason());
            return;
        }
        UpdateGroup(group, group.IsRoot ? null : result.Name, result.Operator);
    }

    [RelayCommand(CanExecute = nameof(CanDeleteNode))]
    private void DeleteNode(ScreenerConditionNodeViewModel? node) => Delete(node!);

    private void RefreshAllDisplayState()
    {
        bool nodesFull = Measure().NodeCount >= MaxNodes;
        RefreshDisplayState(Root, depth: 1, nodesFull, upperText: null);
    }

    private void RefreshDisplayState(ScreenerConditionGroupViewModel group, int depth, bool nodesFull, string? upperText)
    {
        group.AddGroupToolTip = !nodesFull && depth < MaxDepth ? null : _limitReason();
        string? ownText = group.Children.Count == 0 ? null : JoinChildren(group);
        group.HoverUpperText = upperText;
        group.HoverLowerText = ownText;
        foreach (ScreenerConditionNodeViewModel child in group.Children)
        {
            if (child is ScreenerConditionGroupViewModel nested)
            {
                RefreshDisplayState(nested, depth + 1, nodesFull, ownText);
            }
            else
            {
                child.HoverUpperText = null;
                child.HoverLowerText = ownText;
            }
        }
    }

    private static string JoinChildren(ScreenerConditionGroupViewModel container)
        => string.Join($" {OperatorWord(container.Operator)} ", container.Children.Select(Describe));

    private static string Describe(ScreenerConditionNodeViewModel node) => node switch
    {
        ScreenerConditionLeafViewModel leaf => leaf.DisplayText,
        ScreenerConditionGroupViewModel group => $"[{group.Name ?? OperatorWord(group.Operator)}]",
        _ => string.Empty,
    };

    private static string OperatorWord(LogicalOperator @operator) => @operator.ToString().ToUpperInvariant();

    private bool IsAttachedGroup(ScreenerConditionGroupViewModel? group) => group is not null && IsAttached(group);

    /// <summary>The root can only be cleared, so deleting it is possible only while it has children.</summary>
    private bool CanDeleteNode(ScreenerConditionNodeViewModel? node)
        => node is not null && IsAttached(node) && (node is not ScreenerConditionGroupViewModel { IsRoot: true } root || root.Children.Count > 0);

    private void RaiseCommandStates()
    {
        _revision++;
        RefreshAllDisplayState();
        StructureChanged?.Invoke(this, EventArgs.Empty);

        AddGroupCommand.NotifyCanExecuteChanged();
        EditGroupCommand.NotifyCanExecuteChanged();
        DeleteNodeCommand.NotifyCanExecuteChanged();
    }
}
