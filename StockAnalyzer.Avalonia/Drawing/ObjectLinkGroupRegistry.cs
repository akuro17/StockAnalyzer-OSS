using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models.Drawing;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>Role of a drawing object inside a link group.</summary>
public enum LinkRole
{
    None = 0,
    Parent = 1,
    Child = 2
}

/// <summary>Outcome of <see cref="ObjectLinkGroupRegistry.TryLink"/>.</summary>
public enum LinkResult
{
    Ok,
    EmptyChildren,
    ParentIsChildOfGroup,
    ChildInOtherGroup,
    DuplicateId,
    EmptyGuid,
    SelfLink,

    /// <summary>Returned by the manager when a given id is not an object of the current context.</summary>
    UnknownObject
}

/// <summary>
/// Restart-stable persistence form of a link group used by the legacy drawing payload, whose objects do not
/// persist their runtime ids: members are addressed by their ordinal position in the context's object list.
/// </summary>
public readonly record struct DrawingLinkGroupIndexRecord(int ParentIndex, IReadOnlyList<int> ChildIndices);

/// <summary>
/// Pure-logic registry of link groups (one parent + one or more children) for a single drawing context.
/// Invariants: L1 an object belongs to at most one group; L2 a group has one parent and at least one child
/// with no duplicates and the parent never among the children; L3 <see cref="Guid.Empty"/> is never a member.
/// The parent's id is the unique key of a group. Member arrays are pre-allocated per group so the
/// drag hot path (<see cref="TryGetMembers"/>) does not allocate.
/// </summary>
public sealed class ObjectLinkGroupRegistry
{
    private sealed class Group
    {
        public Guid Parent;
        public readonly List<Guid> Children = new();
        public Guid[] Members = Array.Empty<Guid>();

        public void RebuildMembers()
        {
            var members = new Guid[Children.Count + 1];
            members[0] = Parent;
            for (int i = 0; i < Children.Count; i++) members[i + 1] = Children[i];
            Members = members;
        }
    }

    private readonly List<Group> _groups = new();
    private readonly Dictionary<Guid, Group> _byObject = new();

    /// <summary>Number of groups currently registered.</summary>
    public int GroupCount => _groups.Count;

    /// <summary>
    /// Links <paramref name="childIds"/> under <paramref name="parentId"/>. When the parent already parents a group,
    /// children not yet in it are appended (children already in that group are ignored). State is unchanged on failure.
    /// </summary>
    public LinkResult TryLink(Guid parentId, IReadOnlyList<Guid> childIds)
    {
        if (parentId == Guid.Empty) return LinkResult.EmptyGuid;
        if (childIds == null || childIds.Count == 0) return LinkResult.EmptyChildren;

        _byObject.TryGetValue(parentId, out var parentGroup);
        if (parentGroup != null && parentGroup.Parent != parentId) return LinkResult.ParentIsChildOfGroup;

        var seen = new HashSet<Guid>();
        var toAdd = new List<Guid>(childIds.Count);
        for (int i = 0; i < childIds.Count; i++)
        {
            var id = childIds[i];
            if (id == Guid.Empty) return LinkResult.EmptyGuid;
            if (id == parentId) return LinkResult.SelfLink;
            if (!seen.Add(id)) return LinkResult.DuplicateId;

            if (_byObject.TryGetValue(id, out var existing))
            {
                if (!ReferenceEquals(existing, parentGroup)) return LinkResult.ChildInOtherGroup;
                continue; // already a member of the parent's group
            }
            toAdd.Add(id);
        }

        if (toAdd.Count == 0) return LinkResult.EmptyChildren;

        if (parentGroup == null)
        {
            parentGroup = new Group { Parent = parentId };
            _groups.Add(parentGroup);
            _byObject[parentId] = parentGroup;
        }
        for (int i = 0; i < toAdd.Count; i++)
        {
            parentGroup.Children.Add(toAdd[i]);
            _byObject[toAdd[i]] = parentGroup;
        }
        parentGroup.RebuildMembers();
        return LinkResult.Ok;
    }

    /// <summary>Members of the group containing <paramref name="objectId"/> as [parent, children...] (no per-call allocation).</summary>
    public bool TryGetMembers(Guid objectId, out IReadOnlyList<Guid> members)
    {
        if (_byObject.TryGetValue(objectId, out var group))
        {
            members = group.Members;
            return true;
        }
        members = Array.Empty<Guid>();
        return false;
    }

    public LinkRole GetRole(Guid objectId)
    {
        if (!_byObject.TryGetValue(objectId, out var g)) return LinkRole.None;
        return g.Parent == objectId ? LinkRole.Parent : LinkRole.Child;
    }

    /// <summary>Dissolves every group that contains any id of <paramref name="operandIds"/>. Returns the number of groups dissolved.</summary>
    public int UnlinkGroupsOf(IReadOnlyCollection<Guid> operandIds)
    {
        if (operandIds == null || operandIds.Count == 0) return 0;

        var targets = new List<Group>();
        foreach (var id in operandIds)
        {
            if (_byObject.TryGetValue(id, out var g) && !targets.Contains(g))
            {
                targets.Add(g);
            }
        }
        for (int i = 0; i < targets.Count; i++) Dissolve(targets[i]);
        return targets.Count;
    }

    /// <summary>Removes an object that no longer exists: a parent dissolves its group; a child leaves it (empty group dissolves).</summary>
    public void OnObjectRemoved(Guid id)
    {
        if (!_byObject.TryGetValue(id, out var group)) return;

        if (group.Parent == id)
        {
            Dissolve(group);
            return;
        }

        group.Children.Remove(id);
        _byObject.Remove(id);
        if (group.Children.Count == 0)
        {
            Dissolve(group);
        }
        else
        {
            group.RebuildMembers();
        }
    }

    public void Clear()
    {
        _groups.Clear();
        _byObject.Clear();
    }

    /// <summary>Exports groups as persistence records, mapping runtime ids to persistent ids where a mapping exists.</summary>
    public IReadOnlyList<DrawingLinkGroupRecord> ToRecords(IReadOnlyDictionary<Guid, Guid>? runtimeToPersistentIds = null)
    {
        if (_groups.Count == 0) return Array.Empty<DrawingLinkGroupRecord>();

        var records = new List<DrawingLinkGroupRecord>(_groups.Count);
        foreach (var group in _groups)
        {
            var children = new List<Guid>(group.Children.Count);
            for (int i = 0; i < group.Children.Count; i++) children.Add(Map(group.Children[i], runtimeToPersistentIds));
            records.Add(new DrawingLinkGroupRecord(Map(group.Parent, runtimeToPersistentIds), children));
        }
        return records;
    }

    /// <summary>
    /// Replaces all groups from persistence records. Persistent ids are mapped to runtime ids; members that are not
    /// alive are dropped, and groups without a live parent or without a live child are discarded. Never throws on inconsistent data.
    /// </summary>
    public void Load(
        IReadOnlyList<DrawingLinkGroupRecord>? records,
        IReadOnlyDictionary<Guid, Guid>? persistentToRuntimeIds,
        ISet<Guid> liveRuntimeIds)
    {
        Clear();
        if (records == null || liveRuntimeIds == null) return;

        foreach (var record in records)
        {
            var parent = MapBack(record.ParentObjectId, persistentToRuntimeIds);
            if (!liveRuntimeIds.Contains(parent)) continue;

            var children = new List<Guid>(record.ChildObjectIds.Count);
            foreach (var child in record.ChildObjectIds)
            {
                var runtime = MapBack(child, persistentToRuntimeIds);
                if (liveRuntimeIds.Contains(runtime) && runtime != parent) children.Add(runtime);
            }
            if (children.Count == 0) continue;

            // Result deliberately ignored: TryLink rejects records violating L1-L3 and leaves state unchanged.
            TryLink(parent, children);
        }
    }

    /// <summary>Exports groups addressed by position in <paramref name="orderedObjects"/> (members not present are dropped).</summary>
    public IReadOnlyList<DrawingLinkGroupIndexRecord> ToIndexRecords(IReadOnlyList<IChartObject> orderedObjects)
    {
        if (_groups.Count == 0) return Array.Empty<DrawingLinkGroupIndexRecord>();

        var indexById = new Dictionary<Guid, int>(orderedObjects.Count);
        for (int i = 0; i < orderedObjects.Count; i++) indexById[orderedObjects[i].Id] = i;

        var records = new List<DrawingLinkGroupIndexRecord>(_groups.Count);
        foreach (var group in _groups)
        {
            if (!indexById.TryGetValue(group.Parent, out var parentIndex)) continue;
            var children = new List<int>(group.Children.Count);
            foreach (var child in group.Children)
            {
                if (indexById.TryGetValue(child, out var childIndex)) children.Add(childIndex);
            }
            if (children.Count > 0) records.Add(new DrawingLinkGroupIndexRecord(parentIndex, children));
        }
        return records;
    }

    /// <summary>Replaces all groups from positional records; out-of-range indices are ignored. Never throws on inconsistent data.</summary>
    public void LoadFromIndexRecords(IReadOnlyList<DrawingLinkGroupIndexRecord>? records, IReadOnlyList<IChartObject> orderedObjects)
    {
        Clear();
        if (records == null) return;

        foreach (var record in records)
        {
            if (record.ParentIndex < 0 || record.ParentIndex >= orderedObjects.Count || record.ChildIndices == null) continue;

            var children = new List<Guid>(record.ChildIndices.Count);
            foreach (var index in record.ChildIndices)
            {
                if (index >= 0 && index < orderedObjects.Count && index != record.ParentIndex) children.Add(orderedObjects[index].Id);
            }
            if (children.Count == 0) continue;

            // Result deliberately ignored: TryLink rejects records violating L1-L3 and leaves state unchanged.
            TryLink(orderedObjects[record.ParentIndex].Id, children);
        }
    }

    /// <summary>
    /// Converts persistent-id link records (V2 document) into positional records for objects that were materialized with
    /// fresh runtime ids. Members that do not exist in <paramref name="orderedObjects"/> are dropped exactly as in <see cref="Load"/>.
    /// </summary>
    public static IReadOnlyList<DrawingLinkGroupIndexRecord> ToIndexRecords(
        IReadOnlyList<DrawingLinkGroupRecord> records,
        IReadOnlyDictionary<Guid, Guid> persistentToRuntimeIds,
        IReadOnlyList<IChartObject> orderedObjects)
    {
        var live = new HashSet<Guid>(orderedObjects.Count);
        for (int i = 0; i < orderedObjects.Count; i++) live.Add(orderedObjects[i].Id);

        var registry = new ObjectLinkGroupRegistry();
        registry.Load(records, persistentToRuntimeIds, live);
        return registry.ToIndexRecords(orderedObjects);
    }

    private void Dissolve(Group group)
    {
        _byObject.Remove(group.Parent);
        for (int i = 0; i < group.Children.Count; i++) _byObject.Remove(group.Children[i]);
        _groups.Remove(group);
    }

    private static Guid Map(Guid id, IReadOnlyDictionary<Guid, Guid>? map)
        => map != null && map.TryGetValue(id, out var mapped) ? mapped : id;

    private static Guid MapBack(Guid id, IReadOnlyDictionary<Guid, Guid>? map)
        => map != null && map.TryGetValue(id, out var mapped) ? mapped : id;
}
