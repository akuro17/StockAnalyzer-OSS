using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services.Backtest.Engine;

namespace StockAnalyzer.Avalonia.ViewModels.Backtest;

/// <summary>
/// Editor state of the condition tree: six fixed root groups (section x side, canonical order of <see cref="BacktestConditionTree.CanonicalRoots"/>),
/// the current selection, and the edit commands. The editor works on this tree only (never on a flat list) and converts it with <see cref="Build"/>.
/// The depth/node bounds come from configuration; a command whose result would exceed them is disabled, and no count is re-implemented here:
/// sizes are measured by <see cref="BacktestConditionTreeRule.Measure"/>. An empty nested group is a transient editing state (it becomes valid as soon as it
/// receives a condition, and is removed automatically when a delete empties it); <see cref="Build"/> does not hide it - the Core validation at run/save rejects it.
/// </summary>
public sealed partial class BacktestConditionTreeViewModel : ObservableObject
{
    private readonly BacktestConditionGroupViewModel[] _roots;

    private readonly Func<string> _limitReason;
    private readonly Func<ConditionGroupEditRequest, Task<ConditionGroupEditResult?>>? _groupEditor;
    private readonly Func<string> _staleEditReason;
    private int _revision;

    /// <param name="limitReason">The localized text explaining that a limit was reached (shown as the tooltip of a disabled "add group"); the view-model itself holds no localization.</param>
    /// <param name="maxNameLength">Longest group name; null = the configured default (<c>Backtest:MaxConditionGroupNameLength</c>).</param>
    /// <param name="groupEditor">Opens the Add/Edit Group dialog; null = the Add/Edit Group commands do nothing (no dialog available).</param>
    /// <param name="dragStartDistance">Pointer travel (DIP) that turns a press on a condition row into a drag; null = the configured default (<c>Backtest:ConditionDragStartDistance</c>).</param>
    /// <param name="staleEditReason">The localized text explaining that a dialog result was discarded because the tree changed while the dialog was open (raised through <see cref="EditRejected"/>).</param>
    public BacktestConditionTreeViewModel(
        int maxDepth,
        int maxNodes,
        Func<string>? limitReason = null,
        int? maxNameLength = null,
        Func<ConditionGroupEditRequest, Task<ConditionGroupEditResult?>>? groupEditor = null,
        Func<string>? staleEditReason = null,
        int? dragStartDistance = null)
    {
        if (maxDepth < BacktestConditionTreeRule.MinBound)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDepth), maxDepth, $"maxDepth must be >= {BacktestConditionTreeRule.MinBound}.");
        }
        if (maxNodes < BacktestConditionTreeRule.MinBound)
        {
            throw new ArgumentOutOfRangeException(nameof(maxNodes), maxNodes, $"maxNodes must be >= {BacktestConditionTreeRule.MinBound}.");
        }

        int nameLimit = maxNameLength ?? new StockAnalyzer.Core.Models.Settings.BacktestSettings().MaxConditionGroupNameLength;
        if (nameLimit < BacktestConditionTreeRule.MinBound)
        {
            throw new ArgumentOutOfRangeException(nameof(maxNameLength), maxNameLength, $"maxNameLength must be >= {BacktestConditionTreeRule.MinBound}.");
        }

        _limitReason = limitReason ?? (() => string.Empty);
        _groupEditor = groupEditor;
        _staleEditReason = staleEditReason ?? (() => string.Empty);
        int dragDistance = dragStartDistance ?? new StockAnalyzer.Core.Models.Settings.BacktestSettings().ConditionDragStartDistance;
        if (dragDistance < BacktestConditionTreeRule.MinBound)
        {
            throw new ArgumentOutOfRangeException(nameof(dragStartDistance), dragStartDistance, $"dragStartDistance must be >= {BacktestConditionTreeRule.MinBound}.");
        }
        DragStartDistance = dragDistance;
        MaxNameLength = nameLimit;
        MaxDepth = maxDepth;
        MaxNodes = maxNodes;
        _roots = new BacktestConditionGroupViewModel[BacktestConditionTree.CanonicalRoots.Length];
        for (int i = 0; i < _roots.Length; i++)
        {
            (BacktestConditionSection section, TradeSide side) = BacktestConditionTree.CanonicalRoots[i];
            _roots[i] = new BacktestConditionGroupViewModel(section, side, isRoot: true) { Owner = this };
        }
        EntryRoots = SectionRoots(BacktestConditionSection.Entry);
        ExitRoots = SectionRoots(BacktestConditionSection.Exit);
        ReverseRoots = SectionRoots(BacktestConditionSection.Reverse);
        RefreshAllDisplayState();
    }

    private BacktestConditionGroupViewModel[] SectionRoots(BacktestConditionSection section)
        => Array.FindAll(_roots, root => root.Section == section);

    /// <summary>The Long and Short roots of the Entry section (the items of its tree view).</summary>
    public IReadOnlyList<BacktestConditionGroupViewModel> EntryRoots { get; }

    /// <summary>The Long and Short roots of the Exit section.</summary>
    public IReadOnlyList<BacktestConditionGroupViewModel> ExitRoots { get; }

    /// <summary>The Long and Short roots of the Reverse section.</summary>
    public IReadOnlyList<BacktestConditionGroupViewModel> ReverseRoots { get; }

    /// <summary>Raised after every change of the tree structure (add, delete, load); the operator switch does not change the structure.</summary>
    public event EventHandler? StructureChanged;

    /// <summary>Raised with the localized reason when a confirmed dialog result is not applied (the tree changed while the dialog was open); the tree is unchanged.</summary>
    public event EventHandler<string>? EditRejected;

    /// <summary>Counts the changes of the tree (add, delete, load, group edit); a dialog result is applied only while it is unchanged since the dialog opened.</summary>
    public int Revision => _revision;

    /// <summary>Deepest group nesting allowed (the root group is 1); the configured <c>Backtest:MaxConditionTreeDepth</c>.</summary>
    public int MaxDepth { get; }

    /// <summary>Pointer travel (DIP) that starts a drag of a condition; the configured <c>Backtest:ConditionDragStartDistance</c>.</summary>
    public int DragStartDistance { get; }

    /// <summary>Longest group name; the configured <c>Backtest:MaxConditionGroupNameLength</c>.</summary>
    public int MaxNameLength { get; }

    /// <summary>Most nodes per root, root included; the configured <c>Backtest:MaxConditionTreeNodes</c>.</summary>
    public int MaxNodes { get; }

    /// <summary>The six fixed roots in canonical order.</summary>
    public IReadOnlyList<BacktestConditionGroupViewModel> Roots => _roots;

    public BacktestConditionGroupViewModel Root(BacktestConditionSection section, TradeSide side)
        => _roots[BacktestConditionTree.CanonicalRoots.IndexOf((section, side))];

    /// <summary>The single selected node of the whole editor (all three section trees share it); null when nothing is selected.</summary>
    [ObservableProperty]
    private BacktestConditionNodeViewModel? _selectedNode;

    partial void OnSelectedNodeChanged(BacktestConditionNodeViewModel? oldValue, BacktestConditionNodeViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
    }

    /// <summary>Snapshot of the editor as an immutable tree (not validated: nested-group emptiness, limits and offsets are checked by <see cref="BacktestConditionValidator.SnapshotTree"/>).</summary>
    public BacktestConditionTree Build()
    {
        try
        {
            return BacktestConditionTree.Create((section, side) => Root(section, side).ToDomain());
        }
        catch (InsufficientExecutionStackException)
        {
            throw new ArgumentException(BacktestConditionTreeRule.NestingTooDeepMessage);
        }
    }

    /// <summary>Replaces the editor content with <paramref name="tree"/> (which the caller has validated, for example via the Core persistence mapping) and clears the selection.</summary>
    public void Load(BacktestConditionTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);

        // Prepare the whole candidate on scratch groups first: a failure leaves the current content, parents and selection untouched.
        var candidates = new BacktestConditionGroupViewModel[_roots.Length];
        try
        {
            for (int i = 0; i < _roots.Length; i++)
            {
                candidates[i] = new BacktestConditionGroupViewModel(_roots[i].Section, _roots[i].Side, isRoot: true);
                AttachChildren(candidates[i], tree.Root(_roots[i].Section, _roots[i].Side));
            }
        }
        catch (InsufficientExecutionStackException)
        {
            throw new ArgumentException(BacktestConditionTreeRule.NestingTooDeepMessage, nameof(tree));
        }

        for (int i = 0; i < _roots.Length; i++)
        {
            BacktestConditionGroupViewModel root = _roots[i];
            foreach (BacktestConditionNodeViewModel old in root.Children) old.Parent = null;
            root.MutableChildren.Clear();
            root.Operator = tree.Root(root.Section, root.Side).Operator;
            foreach (BacktestConditionNodeViewModel child in candidates[i].Children.ToArray())
            {
                child.Parent = root;
                root.MutableChildren.Add(child);
            }
        }
        SelectedNode = null;
        RaiseCommandStates();
    }

    private static void AttachChildren(BacktestConditionGroupViewModel target, BacktestConditionGroup source)
    {
        System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();
        foreach (IBacktestConditionNode child in source.Children)
        {
            BacktestConditionNodeViewModel node;
            switch (child)
            {
                case BacktestConditionGroup group:
                    var nested = new BacktestConditionGroupViewModel(target.Section, target.Side, isRoot: false, group.Operator) { Name = group.Name };
                    nested.Parent = target;
                    AttachChildren(nested, group);
                    node = nested;
                    break;
                case BacktestConditionLeaf leaf:
                    node = new BacktestConditionLeafViewModel(leaf.Comparison);
                    break;
                default:
                    throw new ArgumentException($"Unsupported condition node type {child.GetType().Name}.", nameof(source));
            }
            node.Parent = target;
            target.MutableChildren.Add(node);
        }
    }

    // ---- bounds ----

    private static BacktestConditionTreeSize SizeOfRootOf(BacktestConditionGroupViewModel group)
    {
        BacktestConditionGroupViewModel root = group;
        while (root.Parent is not null) root = root.Parent;
        return BacktestConditionTreeRule.Measure(root.ToDomain());
    }

    /// <summary>True when one more leaf fits under <paramref name="group"/> (the root's node count stays within <see cref="MaxNodes"/>).</summary>
    public bool CanAddLeaf(BacktestConditionGroupViewModel? group)
        => group is not null && IsAttached(group) && SizeOfRootOf(group).NodeCount < MaxNodes;

    /// <summary>True when one more (initially empty) group fits under <paramref name="group"/> within both <see cref="MaxNodes"/> and <see cref="MaxDepth"/>.</summary>
    public bool CanAddGroup(BacktestConditionGroupViewModel? group)
        => group is not null && IsAttached(group) && group.Depth < MaxDepth && SizeOfRootOf(group).NodeCount < MaxNodes;

    // ---- edit operations ----

    /// <summary>
    /// The group a leaf for (<paramref name="section"/>, <paramref name="side"/>) is added to: the selected group when both its section and side match,
    /// otherwise that (section, side) root (a selected leaf, or a group of another section/side, does not redirect the add).
    /// </summary>
    public BacktestConditionGroupViewModel ResolveAddTarget(BacktestConditionSection section, TradeSide side)
        => SelectedNode is BacktestConditionGroupViewModel selected && selected.Section == section && selected.Side == side
            ? selected
            : Root(section, side);

    /// <summary>
    /// Adds a leaf for <paramref name="entry"/> to <see cref="ResolveAddTarget"/> and returns that target; null (nothing added) when the node bound is reached.
    /// The entry's Role/Position/LogicalOperator must stay at their defaults - inside a tree the location defines them, and the Core validation rejects anything else.
    /// </summary>
    public BacktestConditionGroupViewModel? TryAddLeaf(BacktestConditionSection section, TradeSide side, BacktestConditionEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        BacktestConditionGroupViewModel target = ResolveAddTarget(section, side);
        if (!CanAddLeaf(target)) return null;

        AddChild(target, new BacktestConditionLeafViewModel(entry));
        return target;
    }

    /// <summary>
    /// Adds an empty group with <paramref name="operator"/> and the optional <paramref name="name"/> to <paramref name="parent"/>; false (nothing added) when a bound is reached.
    /// A name longer than <see cref="MaxNameLength"/> is rejected (the dialog's input already enforces it; nothing is truncated).
    /// </summary>
    public bool TryAddGroup(BacktestConditionGroupViewModel parent, LogicalOperator @operator, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(parent);
        EnsureAttached(parent);
        EnsureDefined(@operator);
        string? normalized = CheckedName(name);
        if (!CanAddGroup(parent)) return false;

        AddChild(parent, new BacktestConditionGroupViewModel(parent.Section, parent.Side, isRoot: false, @operator) { Name = normalized });
        return true;
    }

    private void EnsureAttached(BacktestConditionNodeViewModel node)
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
        string? normalized = BacktestConditionGroup.NormalizeName(name);
        if (normalized is not null && normalized.Length > MaxNameLength)
        {
            throw new ArgumentException($"A group name may have at most {MaxNameLength} characters.", nameof(name));
        }
        return normalized;
    }

    private void AddChild(BacktestConditionGroupViewModel parent, BacktestConditionNodeViewModel child)
    {
        child.Parent = parent;
        parent.MutableChildren.Add(child);
        parent.IsExpanded = true;
        RaiseCommandStates();
    }

    /// <summary>Changes the name and the AND/OR operator of a group (the Edit Group dialog). A fixed root has no name: passing one is rejected.</summary>
    public void UpdateGroup(BacktestConditionGroupViewModel group, string? name, LogicalOperator @operator)
    {
        ArgumentNullException.ThrowIfNull(group);
        EnsureAttached(group);
        EnsureDefined(@operator);
        string? normalized = CheckedName(name);
        if (group.IsRoot && normalized is not null)
        {
            throw new ArgumentException("A fixed Long/Short root cannot have a name.", nameof(name));
        }

        group.Name = normalized;
        group.Operator = @operator;
        RaiseCommandStates();
    }

    /// <summary>
    /// Deletes <paramref name="node"/> with its subtree; a root is only cleared (it is fixed). A non-root group that a delete leaves empty is removed as well,
    /// upwards, so the tree stays valid without the user cleaning up. The selection is dropped when the selected node was removed.
    /// </summary>
    public void Delete(BacktestConditionNodeViewModel node)
    {
        ArgumentNullException.ThrowIfNull(node);
        EnsureAttached(node);
        if (node is BacktestConditionGroupViewModel { IsRoot: true } root)
        {
            foreach (BacktestConditionNodeViewModel child in root.Children) child.Parent = null;
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
    }

    /// <summary>True when <paramref name="node"/> is reachable from one of this tree's six roots and every parent link matches the parent's child list.</summary>
    private bool IsAttached(BacktestConditionNodeViewModel node)
    {
        BacktestConditionNodeViewModel current = node;
        while (current.Parent is { } parent)
        {
            if (!parent.Children.Contains(current)) return false;
            current = parent;
        }
        return current is BacktestConditionGroupViewModel { IsRoot: true } root && Array.IndexOf(_roots, root) >= 0;
    }

    // ---- move (drag and drop) ----

    private readonly record struct MovePlan(BacktestConditionGroupViewModel SourceParent, int SourceIndex, BacktestConditionGroupViewModel Destination, int DestinationIndex);

    /// <summary>
    /// Says what <see cref="MoveLeaf"/> would do, without changing anything. A move is allowed only for one comparison inside the SAME fixed root (never across
    /// Entry/Exit/Reverse or Long/Short, which would change the meaning of the condition); the destination may be any group of that root and any position in it.
    /// </summary>
    public ConditionMoveResult EvaluateMove(BacktestConditionNodeViewModel? leaf, BacktestConditionNodeViewModel? target, ConditionDropPlacement placement)
        => Plan(leaf, target, placement, out _);

    /// <summary>
    /// Moves <paramref name="leaf"/> next to (<see cref="ConditionDropPlacement.Before"/>/<see cref="ConditionDropPlacement.After"/>) or into (<see cref="ConditionDropPlacement.Inside"/>,
    /// appended) <paramref name="target"/> as one edit: the leaf is re-parented, a non-root group the move empties is removed upwards, the destination is opened and the leaf stays selected.
    /// A rejected or unchanged move leaves the tree exactly as it was (no edit is counted).
    /// </summary>
    public ConditionMoveResult MoveLeaf(BacktestConditionNodeViewModel? leaf, BacktestConditionNodeViewModel? target, ConditionDropPlacement placement)
    {
        ConditionMoveResult result = Plan(leaf, target, placement, out MovePlan plan);
        if (result != ConditionMoveResult.Moved) return result;

        BacktestConditionNodeViewModel moved = leaf!;
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

    private ConditionMoveResult Plan(BacktestConditionNodeViewModel? leaf, BacktestConditionNodeViewModel? target, ConditionDropPlacement placement, out MovePlan plan)
    {
        plan = default;
        if (leaf is not BacktestConditionLeafViewModel || target is null || !Enum.IsDefined(placement)) return ConditionMoveResult.Rejected;
        if (leaf.Parent is not { } sourceParent || !IsAttached(leaf) || !IsAttached(target)) return ConditionMoveResult.Rejected;

        BacktestConditionGroupViewModel destination;
        int index; // position in the destination's list before the leaf is taken out
        switch (placement)
        {
            case ConditionDropPlacement.Inside:
                if (target is not BacktestConditionGroupViewModel group) return ConditionMoveResult.Rejected;
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

        if (!ReferenceEquals(RootOf(destination), RootOf(sourceParent))) return ConditionMoveResult.Rejected;

        int sourceIndex = sourceParent.Children.IndexOf(leaf);
        if (ReferenceEquals(destination, sourceParent) && sourceIndex < index) index--;
        if (ReferenceEquals(destination, sourceParent) && sourceIndex == index) return ConditionMoveResult.Unchanged;

        plan = new MovePlan(sourceParent, sourceIndex, destination, index);
        return ConditionMoveResult.Moved;
    }

    private static BacktestConditionGroupViewModel RootOf(BacktestConditionNodeViewModel node)
    {
        BacktestConditionNodeViewModel top = node;
        while (top.Parent is not null) top = top.Parent;
        return (BacktestConditionGroupViewModel)top;
    }

    /// <summary>Removes <paramref name="container"/> and then each further ancestor while it is a non-root group left without children.</summary>
    private static void RemoveEmptiedGroups(BacktestConditionGroupViewModel container)
    {
        while (!container.IsRoot && container.Children.Count == 0 && container.Parent is { } up)
        {
            up.MutableChildren.Remove(container);
            container.Parent = null;
            container = up;
        }
    }

    // ---- commands (parameter: the node the context menu was opened on) ----

    /// <summary>Opens the Add Group dialog (name + AND/OR) and adds the confirmed group under <paramref name="group"/>.</summary>
    [RelayCommand(CanExecute = nameof(CanAddGroup))]
    private async Task AddGroupAsync(BacktestConditionGroupViewModel? group)
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
    private async Task EditGroupAsync(BacktestConditionGroupViewModel? group)
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
    private void DeleteNode(BacktestConditionNodeViewModel? node) => Delete(node!);

    /// <summary>
    /// Refreshes what the rows display that depends on the whole tree: the disabled-reason tooltip of "add group" and the hover expressions.
    /// Each root is measured once and each group's expression is joined once (its leaves and sub-groups share it), so a refresh is linear in the size of the tree.
    /// </summary>
    private void RefreshAllDisplayState()
    {
        foreach (BacktestConditionGroupViewModel root in _roots)
        {
            bool nodesFull = BacktestConditionTreeRule.Measure(root.ToDomain()).NodeCount >= MaxNodes;
            RefreshDisplayState(root, depth: 1, nodesFull, upperText: null);
        }
    }

    private void RefreshDisplayState(BacktestConditionGroupViewModel group, int depth, bool nodesFull, string? upperText)
    {
        group.AddGroupToolTip = !nodesFull && depth < MaxDepth ? null : _limitReason();
        string? ownText = group.Children.Count == 0 ? null : JoinChildren(group);
        group.HoverUpperText = upperText;
        group.HoverLowerText = ownText;
        foreach (BacktestConditionNodeViewModel child in group.Children)
        {
            if (child is BacktestConditionGroupViewModel nested)
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

    /// <summary>The children of <paramref name="container"/> joined by its operator: a comparison as its text, a sub-group by its name (never expanded).</summary>
    private static string JoinChildren(BacktestConditionGroupViewModel container)
        => string.Join($" {OperatorWord(container.Operator)} ", container.Children.Select(Describe));

    private static string Describe(BacktestConditionNodeViewModel node) => node switch
    {
        BacktestConditionLeafViewModel leaf => leaf.DisplayText,
        BacktestConditionGroupViewModel group => $"[{group.Name ?? OperatorWord(group.Operator)}]",
        _ => string.Empty,
    };

    private static string OperatorWord(LogicalOperator @operator) => @operator.ToString().ToUpperInvariant();

    private bool IsAttachedGroup(BacktestConditionGroupViewModel? group) => group is not null && IsAttached(group);

    /// <summary>A root can only be cleared, so deleting it is possible only while it has children.</summary>
    private bool CanDeleteNode(BacktestConditionNodeViewModel? node)
        => node is not null && IsAttached(node) && (node is not BacktestConditionGroupViewModel { IsRoot: true } root || root.Children.Count > 0);

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
