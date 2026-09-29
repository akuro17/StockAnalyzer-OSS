using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Models.Drawing;
using Xunit;

namespace StockAnalyzer.Tests.Drawing;

/// <summary>
/// Invariant tests for <see cref="ObjectLinkGroupRegistry"/> (L1-L4) and the pure
/// <see cref="DrawingLinkEligibility"/> rules of the Layers Panel link/unlink buttons.
/// </summary>
public class ObjectLinkGroupRegistryTests
{
    private static Guid[] Ids(int n) => Enumerable.Range(0, n).Select(_ => Guid.NewGuid()).ToArray();

    [Fact]
    public void TryLink_CreatesGroup_ParentFirstThenChildrenInArgumentOrder()
    {
        var registry = new ObjectLinkGroupRegistry();
        var ids = Ids(3);

        var result = registry.TryLink(ids[0], new[] { ids[1], ids[2] });

        Assert.Equal(LinkResult.Ok, result);
        Assert.True(registry.TryGetMembers(ids[2], out var members));
        Assert.Equal(new[] { ids[0], ids[1], ids[2] }, members.ToArray());
        Assert.Equal(LinkRole.Parent, registry.GetRole(ids[0]));
        Assert.Equal(LinkRole.Child, registry.GetRole(ids[1]));
        Assert.Equal(1, registry.GroupCount);
    }

    [Fact]
    public void TryLink_ParentAlreadyParent_AppendsOnlyNewChildren()
    {
        var registry = new ObjectLinkGroupRegistry();
        var ids = Ids(4);
        registry.TryLink(ids[0], new[] { ids[1] });

        var result = registry.TryLink(ids[0], new[] { ids[1], ids[2], ids[3] });

        Assert.Equal(LinkResult.Ok, result);
        registry.TryGetMembers(ids[0], out var members);
        Assert.Equal(new[] { ids[0], ids[1], ids[2], ids[3] }, members.ToArray());
    }

    [Fact]
    public void TryLink_OnlyExistingChildren_ReturnsEmptyChildrenAndKeepsState()
    {
        var registry = new ObjectLinkGroupRegistry();
        var ids = Ids(2);
        registry.TryLink(ids[0], new[] { ids[1] });

        Assert.Equal(LinkResult.EmptyChildren, registry.TryLink(ids[0], new[] { ids[1] }));
        registry.TryGetMembers(ids[0], out var members);
        Assert.Equal(2, members.Count);
    }

    [Fact]
    public void TryLink_RejectsInvalidInput_WithoutChangingState()
    {
        var registry = new ObjectLinkGroupRegistry();
        var ids = Ids(3);

        Assert.Equal(LinkResult.EmptyChildren, registry.TryLink(ids[0], Array.Empty<Guid>()));
        Assert.Equal(LinkResult.EmptyGuid, registry.TryLink(Guid.Empty, new[] { ids[1] }));
        Assert.Equal(LinkResult.EmptyGuid, registry.TryLink(ids[0], new[] { Guid.Empty }));
        Assert.Equal(LinkResult.SelfLink, registry.TryLink(ids[0], new[] { ids[0] }));
        Assert.Equal(LinkResult.DuplicateId, registry.TryLink(ids[0], new[] { ids[1], ids[1] }));
        Assert.Equal(0, registry.GroupCount);
        Assert.Equal(LinkRole.None, registry.GetRole(ids[1]));
    }

    [Fact]
    public void TryLink_ChildInOtherGroup_IsRejected_AndParentThatIsChildIsRejected()
    {
        var registry = new ObjectLinkGroupRegistry();
        var ids = Ids(4);
        registry.TryLink(ids[0], new[] { ids[1] });

        Assert.Equal(LinkResult.ChildInOtherGroup, registry.TryLink(ids[2], new[] { ids[1], ids[3] }));
        Assert.Equal(LinkResult.ParentIsChildOfGroup, registry.TryLink(ids[1], new[] { ids[3] }));

        Assert.Equal(LinkRole.None, registry.GetRole(ids[2]));
        Assert.Equal(LinkRole.None, registry.GetRole(ids[3]));
        Assert.Equal(1, registry.GroupCount);
    }

    [Fact]
    public void UnlinkGroupsOf_DissolvesWholeGroups_EvenWhenOnlyOneMemberIsNamed()
    {
        var registry = new ObjectLinkGroupRegistry();
        var ids = Ids(6);
        registry.TryLink(ids[0], new[] { ids[1], ids[2] });
        registry.TryLink(ids[3], new[] { ids[4] });

        int dissolved = registry.UnlinkGroupsOf(new[] { ids[2], ids[5] });

        Assert.Equal(1, dissolved);
        Assert.Equal(LinkRole.None, registry.GetRole(ids[0]));
        Assert.Equal(LinkRole.None, registry.GetRole(ids[1]));
        Assert.NotEqual(LinkRole.None, registry.GetRole(ids[3]));
        Assert.Equal(0, registry.UnlinkGroupsOf(new[] { ids[5] }));
    }

    [Fact]
    public void OnObjectRemoved_ParentDissolvesGroup_ChildLeavesAndLastChildDissolves()
    {
        var registry = new ObjectLinkGroupRegistry();
        var ids = Ids(5);
        registry.TryLink(ids[0], new[] { ids[1], ids[2] });

        registry.OnObjectRemoved(ids[1]);
        Assert.True(registry.TryGetMembers(ids[0], out var members));
        Assert.Equal(new[] { ids[0], ids[2] }, members.ToArray());

        registry.OnObjectRemoved(ids[2]);
        Assert.Equal(0, registry.GroupCount);
        Assert.Equal(LinkRole.None, registry.GetRole(ids[0]));

        registry.TryLink(ids[3], new[] { ids[4] });
        registry.OnObjectRemoved(ids[3]);
        Assert.Equal(0, registry.GroupCount);
        Assert.Equal(LinkRole.None, registry.GetRole(ids[4]));
    }

    [Fact]
    public void ToRecords_ThenLoad_RoundTripsThroughIdMapping()
    {
        var registry = new ObjectLinkGroupRegistry();
        var runtime = Ids(3);
        var persistent = Ids(3);
        registry.TryLink(runtime[0], new[] { runtime[1], runtime[2] });

        var toPersistent = runtime.Zip(persistent).ToDictionary(p => p.First, p => p.Second);
        var records = registry.ToRecords(toPersistent);
        Assert.Equal(persistent[0], records[0].ParentObjectId);
        Assert.Equal(new[] { persistent[1], persistent[2] }, records[0].ChildObjectIds.ToArray());

        var newRuntime = Ids(3);
        var toRuntime = persistent.Zip(newRuntime).ToDictionary(p => p.First, p => p.Second);
        var restored = new ObjectLinkGroupRegistry();
        restored.Load(records, toRuntime, new HashSet<Guid>(newRuntime));

        restored.TryGetMembers(newRuntime[1], out var members);
        Assert.Equal(newRuntime, members.ToArray());
    }

    [Fact]
    public void Load_DropsDeadMembers_AndDiscardsGroupsWithoutLiveParentOrChild()
    {
        var ids = Ids(6);
        var records = new[]
        {
            new DrawingLinkGroupRecord(ids[0], new[] { ids[1], ids[2] }), // child 2 is dead
            new DrawingLinkGroupRecord(ids[3], new[] { ids[4] }),         // parent dead
            new DrawingLinkGroupRecord(ids[5], new[] { ids[4] }),         // no live child besides dead ones
        };
        var live = new HashSet<Guid> { ids[0], ids[1], ids[5] };

        var registry = new ObjectLinkGroupRegistry();
        registry.Load(records, null, live);

        Assert.Equal(1, registry.GroupCount);
        registry.TryGetMembers(ids[0], out var members);
        Assert.Equal(new[] { ids[0], ids[1] }, members.ToArray());
    }

    [Fact]
    public void Load_InconsistentRecords_NeverThrowsAndKeepsInvariantL1()
    {
        var ids = Ids(3);
        var records = new[]
        {
            new DrawingLinkGroupRecord(ids[0], new[] { ids[1] }),
            new DrawingLinkGroupRecord(ids[2], new[] { ids[1] }), // ids[1] already belongs to the first group
        };

        var registry = new ObjectLinkGroupRegistry();
        registry.Load(records, null, new HashSet<Guid>(ids));

        Assert.Equal(1, registry.GroupCount);
        Assert.Equal(LinkRole.None, registry.GetRole(ids[2]));
    }

    [Fact]
    public void IndexRecords_RoundTrip_AcrossFreshObjectIds()
    {
        var first = new IChartObject[] { NewLine(), NewLine(), NewLine() };
        var registry = new ObjectLinkGroupRegistry();
        registry.TryLink(first[2].Id, new[] { first[0].Id });

        var records = registry.ToIndexRecords(first);
        Assert.Equal(2, records[0].ParentIndex);
        Assert.Equal(new[] { 0 }, records[0].ChildIndices.ToArray());

        var second = new IChartObject[] { NewLine(), NewLine(), NewLine() };
        var restored = new ObjectLinkGroupRegistry();
        restored.LoadFromIndexRecords(records, second);

        Assert.Equal(LinkRole.Parent, restored.GetRole(second[2].Id));
        Assert.Equal(LinkRole.Child, restored.GetRole(second[0].Id));
        Assert.Equal(LinkRole.None, restored.GetRole(second[1].Id));
    }

    [Fact]
    public void LoadFromIndexRecords_OutOfRangeIndices_AreIgnored()
    {
        var objects = new IChartObject[] { NewLine(), NewLine() };
        var registry = new ObjectLinkGroupRegistry();

        registry.LoadFromIndexRecords(new[]
        {
            new DrawingLinkGroupIndexRecord(5, new[] { 0 }),
            new DrawingLinkGroupIndexRecord(0, new[] { -1, 9, 0 }),
            new DrawingLinkGroupIndexRecord(0, new[] { 1 }),
        }, objects);

        Assert.Equal(1, registry.GroupCount);
        Assert.Equal(LinkRole.Child, registry.GetRole(objects[1].Id));
    }

    private static TrendLineObject NewLine()
        => new(new ChartPoint(new DateTime(2025, 1, 1), 100m), new ChartPoint(new DateTime(2025, 1, 5), 110m));

    // ---- DrawingLinkEligibility -------------------------------------------------------------

    private static LinkCandidate Row(
        Guid id,
        bool targeted = false,
        bool linkable = true,
        bool editable = true,
        PanelKey? panel = null,
        LinkRole role = LinkRole.None)
        => new(id, targeted, linkable, editable, panel ?? PanelKey.Main, role);

    [Fact]
    public void IsLinkableType_ExcludesObjectsWhoseTranslateIsNoOp()
    {
        Assert.True(DrawingLinkEligibility.IsLinkableType(NewLine()));
        Assert.False(DrawingLinkEligibility.IsLinkableType(new FreehandObject()));
        Assert.False(DrawingLinkEligibility.IsLinkableType(new StockAnalyzer.Avalonia.Drawing.Objects.InformationObject()));
    }

    [Fact]
    public void GetNewChildIds_KeepsOnlyTargetedSamePanelEditableLinkableUnlinkedRows()
    {
        var ids = Ids(8);
        var subPanel = PanelKey.OverlayGroup("7");
        var rows = new List<LinkCandidate>
        {
            Row(ids[0]),                                   // pressed row P (not targeted itself)
            Row(ids[1], targeted: true),                   // eligible
            Row(ids[2], targeted: true, panel: subPanel),  // other panel
            Row(ids[3], targeted: true, editable: false),  // edit-locked layer
            Row(ids[4], targeted: true, linkable: false),  // Translate is a no-op
            Row(ids[5], targeted: true, role: LinkRole.Child), // already in a group
            Row(ids[6]),                                   // not targeted
            Row(ids[7], targeted: true),                   // eligible
        };

        var result = DrawingLinkEligibility.GetNewChildIds(rows[0], rows);

        Assert.Equal(new[] { ids[1], ids[7] }, result.ToArray());
        Assert.True(DrawingLinkEligibility.CanLink(rows[0], rows));
    }

    [Fact]
    public void CanLink_IsFalse_ForChildRowNonEditableRowOrNoEligibleChild()
    {
        var ids = Ids(3);
        var target = Row(ids[1], targeted: true);

        var childRow = Row(ids[0], role: LinkRole.Child);
        Assert.False(DrawingLinkEligibility.CanLink(childRow, new[] { childRow, target }));

        var lockedRow = Row(ids[0], editable: false);
        Assert.False(DrawingLinkEligibility.CanLink(lockedRow, new[] { lockedRow, target }));

        var alone = Row(ids[0]);
        Assert.False(DrawingLinkEligibility.CanLink(alone, new[] { alone, Row(ids[2]) }));
        Assert.False(DrawingLinkEligibility.CanLink(alone, new[] { alone }));
    }

    [Fact]
    public void CanLink_ParentRowThatAlreadyParents_OnlyWhenNewChildrenExist()
    {
        var ids = Ids(3);
        var parent = Row(ids[0], role: LinkRole.Parent);
        var existingChild = Row(ids[1], targeted: true, role: LinkRole.Child);
        var fresh = Row(ids[2], targeted: true);

        Assert.False(DrawingLinkEligibility.CanLink(parent, new[] { parent, existingChild }));
        Assert.True(DrawingLinkEligibility.CanLink(parent, new[] { parent, existingChild, fresh }));
    }

    [Fact]
    public void CanUnlink_TrueWhenPressedOrAnyTargetedRowIsLinked_IgnoringLockState()
    {
        var ids = Ids(3);
        var pressed = Row(ids[0], editable: false);
        var linkedTarget = Row(ids[1], targeted: true, role: LinkRole.Child, editable: false);
        var linkedNotTargeted = Row(ids[2], role: LinkRole.Parent);

        Assert.False(DrawingLinkEligibility.CanUnlink(pressed, new[] { pressed, linkedNotTargeted }));
        Assert.True(DrawingLinkEligibility.CanUnlink(pressed, new[] { pressed, linkedTarget }));

        var linkedPressed = Row(ids[0], role: LinkRole.Parent, editable: false);
        Assert.True(DrawingLinkEligibility.CanUnlink(linkedPressed, new[] { linkedPressed }));
    }

    [Fact]
    public void GetUnlinkOperandIds_IsPressedRowPlusEveryTargetedRow()
    {
        var ids = Ids(4);
        var pressed = Row(ids[0], targeted: true);
        var rows = new[] { pressed, Row(ids[1], targeted: true), Row(ids[2]), Row(ids[3], targeted: true) };

        var operands = DrawingLinkEligibility.GetUnlinkOperandIds(pressed, rows);

        Assert.Equal(new[] { ids[0], ids[1], ids[3] }, operands.ToArray());
    }
}
