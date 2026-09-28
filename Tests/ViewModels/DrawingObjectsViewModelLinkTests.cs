using System;
using System.Linq;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Tests.Backtest;
using Xunit;

namespace StockAnalyzer.Tests.ViewModels;

/// <summary>
/// Layers Panel link / unlink commands: availability follows which rows are checked, execution goes through
/// <see cref="ChartObjectManager"/>, and check marks are left untouched so the operation can be repeated.
/// </summary>
public class DrawingObjectsViewModelLinkTests : IDisposable
{
    // The link mode is process-wide static state: pin it to the default for every test and restore it afterwards.
    public DrawingObjectsViewModelLinkTests()
        => DrawingThemeContext.SetDrawingLinkModeForTesting(DrawingLinkMode.SnapToParentAnchorPoint);

    public void Dispose()
        => DrawingThemeContext.SetDrawingLinkModeForTesting(DrawingLinkMode.SnapToParentAnchorPoint);

    private static TrendLineObject Line(int day)
        => new(new ChartPoint(new DateTime(2025, 1, day), 100m), new ChartPoint(new DateTime(2025, 1, day + 3), 110m));

    private static (DrawingObjectsViewModel vm, ChartObjectManager manager, TrendLineObject[] lines) Create(int count = 3)
    {
        var manager = new ChartObjectManager();
        var lines = Enumerable.Range(0, count).Select(i => Line(2 + i * 5)).ToArray();
        foreach (var line in lines) manager.AddObject(line);
        var vm = new DrawingObjectsViewModel(manager, new SynchronousDispatcherService());
        return (vm, manager, lines);
    }

    private static DrawingObjectItemViewModel Row(DrawingObjectsViewModel vm, IChartObject obj)
        => vm.Items.Single(i => i.Id == obj.Id);

    [Fact]
    public void LinkAsParent_IsDisabledUntilAnotherRowIsChecked()
    {
        var (vm, _, lines) = Create();
        using var _ = vm;
        var parentRow = Row(vm, lines[0]);

        Assert.False(parentRow.LinkAsParentCommand.CanExecute(null));

        Row(vm, lines[1]).IsTargeted = true;

        Assert.True(parentRow.LinkAsParentCommand.CanExecute(null));
    }

    [Fact]
    public void LinkAsParent_LinksCheckedRowsUnderThePressedRow_AndKeepsChecksAndBadges()
    {
        var (vm, manager, lines) = Create();
        using var _ = vm;
        Row(vm, lines[1]).IsTargeted = true;
        Row(vm, lines[2]).IsTargeted = true;
        var parentRow = Row(vm, lines[0]);

        parentRow.LinkAsParentCommand.Execute(null);

        Assert.Equal(LinkRole.Parent, manager.GetLinkRole(lines[0].Id));
        Assert.Equal(LinkRole.Child, manager.GetLinkRole(lines[1].Id));
        Assert.Equal(LinkRole.Child, manager.GetLinkRole(lines[2].Id));
        Assert.True(Row(vm, lines[1]).IsTargeted);
        Assert.True(Row(vm, lines[2]).IsTargeted);
        Assert.True(parentRow.IsLinkParent);
        Assert.True(Row(vm, lines[1]).IsLinkChild);
        Assert.False(parentRow.LinkAsParentCommand.CanExecute(null)); // nothing new left to add
        Assert.True(parentRow.UnlinkCommand.CanExecute(null));
    }

    [Fact]
    public void LinkAsParent_OnAChildRow_IsDisabled_SoTheParentRoleIsNeverStolen()
    {
        var (vm, manager, lines) = Create();
        using var _ = vm;
        manager.LinkObjects(lines[0].Id, new[] { lines[1].Id });
        vm.SyncFromManager();
        Row(vm, lines[2]).IsTargeted = true;

        Assert.False(Row(vm, lines[1]).LinkAsParentCommand.CanExecute(null));
    }

    [Fact]
    public void LinkAsParent_ParentCanGrowItsGroupWithNewlyCheckedRows()
    {
        var (vm, manager, lines) = Create();
        using var _ = vm;
        manager.LinkObjects(lines[0].Id, new[] { lines[1].Id });
        vm.SyncFromManager();
        var parentRow = Row(vm, lines[0]);
        Row(vm, lines[2]).IsTargeted = true;

        Assert.True(parentRow.LinkAsParentCommand.CanExecute(null));
        parentRow.LinkAsParentCommand.Execute(null);

        Assert.True(manager.TryGetLinkedMembers(lines[0].Id, out var members));
        Assert.Equal(3, members.Count);
    }

    [Fact]
    public void Unlink_IsDisabledWhenNothingRelevantIsLinked()
    {
        var (vm, manager, lines) = Create();
        using var _ = vm;
        manager.LinkObjects(lines[0].Id, new[] { lines[1].Id });
        vm.SyncFromManager();

        // lines[2] is unlinked and nothing linked is checked.
        Assert.False(Row(vm, lines[2]).UnlinkCommand.CanExecute(null));

        // Linked but not the pressed row and not checked: still disabled for the pressed unlinked row.
        Row(vm, lines[2]).IsTargeted = true;
        Assert.False(Row(vm, lines[2]).UnlinkCommand.CanExecute(null));
    }

    [Fact]
    public void Unlink_OnAnUnlinkedRow_DissolvesGroupsOfCheckedLinkedRows()
    {
        var (vm, manager, lines) = Create();
        using var _ = vm;
        manager.LinkObjects(lines[0].Id, new[] { lines[1].Id });
        vm.SyncFromManager();
        Row(vm, lines[1]).IsTargeted = true; // checked child of the group

        var pressed = Row(vm, lines[2]);
        Assert.True(pressed.UnlinkCommand.CanExecute(null));
        pressed.UnlinkCommand.Execute(null);

        Assert.Equal(LinkRole.None, manager.GetLinkRole(lines[0].Id));
        Assert.Equal(LinkRole.None, manager.GetLinkRole(lines[1].Id));
        Assert.False(Row(vm, lines[0]).IsLinked);
    }

    [Fact]
    public void Unlink_OnALinkedRow_DissolvesItsGroupWithoutAnyCheckedRow()
    {
        var (vm, manager, lines) = Create();
        using var _ = vm;
        manager.LinkObjects(lines[0].Id, new[] { lines[1].Id, lines[2].Id });
        vm.SyncFromManager();

        var childRow = Row(vm, lines[2]);
        Assert.True(childRow.UnlinkCommand.CanExecute(null));
        childRow.UnlinkCommand.Execute(null);

        Assert.Equal(0, manager.Objects.Count(o => manager.GetLinkRole(o.Id) != LinkRole.None));
    }

    [Fact]
    public void LinkAsParent_IgnoresCheckedRowsWhoseTranslateIsNoOp()
    {
        var manager = new ChartObjectManager();
        var line = Line(2);
        var information = new StockAnalyzer.Avalonia.Drawing.Objects.InformationObject();
        manager.AddObject(line);
        manager.AddObject(information);
        using var vm = new DrawingObjectsViewModel(manager, new SynchronousDispatcherService());
        vm.Items.Single(i => i.Id == information.Id).IsTargeted = true;

        Assert.False(vm.Items.Single(i => i.Id == line.Id).LinkAsParentCommand.CanExecute(null));
    }

    [Fact]
    public void LinkAsParent_SnapsNewChildrenToTheParentAnchorPoint_WithoutMovingTheParent()
    {
        var (vm, manager, lines) = Create();
        using var _ = vm;
        var parentBefore = lines[0].Points[0];
        Row(vm, lines[1]).IsTargeted = true;
        Row(vm, lines[2]).IsTargeted = true;

        Row(vm, lines[0]).LinkAsParentCommand.Execute(null);

        Assert.Equal(parentBefore, lines[0].Points[0]);
        Assert.Equal(lines[0].Points[0], lines[1].Points[0]);
        Assert.Equal(lines[0].Points[0], lines[2].Points[0]);
        // Shape is preserved: the second point keeps its offset from the first.
        Assert.Equal(TimeSpan.FromDays(3), lines[1].Points[1].Time - lines[1].Points[0].Time);
    }

    [Fact]
    public void LinkAsParent_OnAnExistingParent_SnapsOnlyTheNewlyAddedChild()
    {
        var (vm, manager, lines) = Create();
        using var _ = vm;
        manager.LinkObjects(lines[0].Id, new[] { lines[1].Id });
        vm.SyncFromManager();
        var existingChildBefore = lines[1].Points[0];
        Row(vm, lines[1]).IsTargeted = true; // already a member: must not be moved again
        Row(vm, lines[2]).IsTargeted = true;

        Row(vm, lines[0]).LinkAsParentCommand.Execute(null);

        Assert.Equal(existingChildBefore, lines[1].Points[0]);
        Assert.Equal(lines[0].Points[0], lines[2].Points[0]);
    }

    [Fact]
    public void LinkAsParent_PreserveRelativePosition_LinksWithoutMovingAnyObject()
    {
        DrawingThemeContext.SetDrawingLinkModeForTesting(DrawingLinkMode.PreserveRelativePosition);
        var (vm, manager, lines) = Create();
        using var _ = vm;
        var before = lines.Select(l => l.Points.ToArray()).ToArray();
        Row(vm, lines[1]).IsTargeted = true;
        Row(vm, lines[2]).IsTargeted = true;

        Row(vm, lines[0]).LinkAsParentCommand.Execute(null);

        for (int i = 0; i < lines.Length; i++)
        {
            Assert.Equal(before[i], lines[i].Points.ToArray());
        }
        Assert.Equal(LinkRole.Parent, manager.GetLinkRole(lines[0].Id));
        Assert.Equal(LinkRole.Child, manager.GetLinkRole(lines[1].Id));
        Assert.Equal(LinkRole.Child, manager.GetLinkRole(lines[2].Id));
        Assert.True(Row(vm, lines[1]).IsTargeted);
        Assert.True(Row(vm, lines[2]).IsTargeted);
    }

    [Fact]
    public void LinkAsParent_PreserveRelativePosition_KeepsOffsetAfterALinkedTranslate()
    {
        DrawingThemeContext.SetDrawingLinkModeForTesting(DrawingLinkMode.PreserveRelativePosition);
        var (vm, manager, lines) = Create(count: 2);
        using var _ = vm;
        var offsetBefore = lines[1].Points[0].Time - lines[0].Points[0].Time;
        var priceOffsetBefore = lines[1].Points[0].Price - lines[0].Points[0].Price;
        Row(vm, lines[1]).IsTargeted = true;
        Row(vm, lines[0]).LinkAsParentCommand.Execute(null);

        Assert.True(manager.TryGetLinkedMembers(lines[0].Id, out var members));
        var displacement = TimeSpan.FromDays(2);
        foreach (var id in members) manager.GetObject(id)!.Translate(displacement, 5m);

        Assert.Equal(offsetBefore, lines[1].Points[0].Time - lines[0].Points[0].Time);
        Assert.Equal(priceOffsetBefore, lines[1].Points[0].Price - lines[0].Points[0].Price);
    }

    [Fact]
    public void LinkAsParent_ModeIsReadPerPress_AndDoesNotRetroactivelyMoveExistingGroups()
    {
        var (vm, manager, lines) = Create();
        using var _ = vm;
        Row(vm, lines[1]).IsTargeted = true;
        Row(vm, lines[0]).LinkAsParentCommand.Execute(null); // snap mode: lines[1] joins at the parent AP
        var snappedChild = lines[1].Points[0];

        DrawingThemeContext.SetDrawingLinkModeForTesting(DrawingLinkMode.PreserveRelativePosition);
        var thirdBefore = lines[2].Points[0];
        Row(vm, lines[2]).IsTargeted = true;
        Row(vm, lines[0]).LinkAsParentCommand.Execute(null); // preserve mode: only the new child, unmoved

        Assert.Equal(snappedChild, lines[1].Points[0]);
        Assert.Equal(thirdBefore, lines[2].Points[0]);
        Assert.True(manager.TryGetLinkedMembers(lines[0].Id, out var members));
        Assert.Equal(3, members.Count);
    }

    [Fact]
    public void AreAllTargeted_CoalescesPerItemNotificationsIntoOne()
    {
        var (vm, _, _) = Create(count: 4);
        using var _ = vm;
        int selectionNotifications = 0;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DrawingObjectsViewModel.SelectedObjectItem)) selectionNotifications++;
        };

        vm.AreAllTargeted = true;

        Assert.All(vm.Items, i => Assert.True(i.IsTargeted));
        Assert.Equal(1, selectionNotifications);
    }
}
