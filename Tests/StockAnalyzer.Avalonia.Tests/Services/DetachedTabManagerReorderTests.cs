using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.Messaging;
using Moq;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Avalonia.Tests.TestHelpers;
using StockAnalyzer.Core.Models.UI;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Services;

public class DetachedTabManagerReorderTests
{
    private const string ContainerX = "container-x";
    private const string ContainerY = "container-y";

    private static DetachedTabManager CreateManager(Mock<IWindowManagementService>? windowManagement = null) =>
        new(
            new StrongReferenceMessenger(),
            (windowManagement ?? new Mock<IWindowManagementService>()).Object,
            () => new ChartViewModel(),
            new SynchronousDispatcherService());

    private static string Ids(IEnumerable<WorkspaceViewItem> items) => string.Join(",", items.Select(i => i.Id));

    /// <summary>Registration order [A(X), B(X), C(Y), D(X)].</summary>
    private static (DetachedTabManager Manager, WorkspaceViewItem A, WorkspaceViewItem B, WorkspaceViewItem C, WorkspaceViewItem D) CreateRegistered()
    {
        var manager = CreateManager();
        var a = TestWorkspaceItems.Create("A", ContainerX);
        var b = TestWorkspaceItems.Create("B", ContainerX);
        var c = TestWorkspaceItems.Create("C", ContainerY);
        var d = TestWorkspaceItems.Create("D", ContainerX);
        foreach (var item in new[] { a, b, c, d }) manager.RegisterActiveDetachedTab(item);
        return (manager, a, b, c, d);
    }

    [Fact]
    public void ReorderContainerItems_PermutesOnlyTheContainersSlots()
    {
        var (manager, a, b, _, d) = CreateRegistered();

        bool applied = manager.ReorderContainerItems(ContainerX, new[] { d, a, b });

        Assert.True(applied);
        Assert.Equal("D,A,C,B", Ids(manager.DetachedTabs));
    }

    [Fact]
    public void ReorderContainerItems_ThenCapture_PersistsNewOrderWithContainerIds()
    {
        var (manager, a, b, _, d) = CreateRegistered();
        manager.ReorderContainerItems(ContainerX, new[] { d, a, b });

        var captured = new List<DetachedTabInfo>();
        manager.Capture(captured);

        Assert.Equal(new[] { "D", "A", "C", "B" }, captured.Select(i => i.TabId).ToArray());
        Assert.Equal(new[] { ContainerX, ContainerX, ContainerY, ContainerX }, captured.Select(i => i.ContainerId).ToArray());
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("")]
    public void ReorderContainerItems_UnknownOrEmptyContainer_ReturnsFalseAndKeepsOrder(string containerId)
    {
        var (manager, a, b, _, d) = CreateRegistered();

        Assert.False(manager.ReorderContainerItems(containerId, new[] { d, a, b }));
        Assert.Equal("A,B,C,D", Ids(manager.DetachedTabs));
    }

    [Fact]
    public void ReorderContainerItems_CountMismatch_ReturnsFalseAndKeepsOrder()
    {
        var (manager, a, b, _, _) = CreateRegistered();

        Assert.False(manager.ReorderContainerItems(ContainerX, new[] { b, a }));
        Assert.Equal("A,B,C,D", Ids(manager.DetachedTabs));
    }

    [Fact]
    public void ReorderContainerItems_ForeignItem_ReturnsFalseAndKeepsOrder()
    {
        var (manager, a, b, _, _) = CreateRegistered();
        var foreign = TestWorkspaceItems.Create("Z", ContainerX);

        Assert.False(manager.ReorderContainerItems(ContainerX, new[] { foreign, a, b }));
        Assert.Equal("A,B,C,D", Ids(manager.DetachedTabs));
    }

    [Fact]
    public void ReorderContainerItems_ItemOfAnotherContainer_ReturnsFalseAndKeepsOrder()
    {
        var (manager, a, b, c, _) = CreateRegistered();

        Assert.False(manager.ReorderContainerItems(ContainerX, new[] { c, a, b }));
        Assert.Equal("A,B,C,D", Ids(manager.DetachedTabs));
    }

    [Fact]
    public void ReorderContainerItems_DuplicateItem_ReturnsFalseAndKeepsOrder()
    {
        var (manager, a, _, _, d) = CreateRegistered();

        Assert.False(manager.ReorderContainerItems(ContainerX, new[] { a, a, d }));
        Assert.Equal("A,B,C,D", Ids(manager.DetachedTabs));
    }

    [Fact]
    public void ReorderContainerItems_IdenticalOrder_ReturnsTrueAndKeepsOrder()
    {
        var (manager, a, b, _, d) = CreateRegistered();

        Assert.True(manager.ReorderContainerItems(ContainerX, new[] { a, b, d }));
        Assert.Equal("A,B,C,D", Ids(manager.DetachedTabs));
    }

    [Fact]
    public void ReorderContainerItems_NullArguments_Throw()
    {
        var (manager, a, _, _, _) = CreateRegistered();

        Assert.Throws<ArgumentNullException>(() => manager.ReorderContainerItems(null!, new[] { a }));
        Assert.Throws<ArgumentNullException>(() => manager.ReorderContainerItems(ContainerX, null!));
    }

    [Fact]
    public void ReorderContainerItems_CaptureThenRestore_RestoresGroupInSavedOrder()
    {
        var (manager, a, b, _, d) = CreateRegistered();
        manager.ReorderContainerItems(ContainerX, new[] { d, a, b });

        var settings = new WorkspaceSettings();
        manager.Capture(settings.DetachedTabs);

        var recordedGroups = new List<List<string>>();
        var tearOff = new Mock<ITearOffService>();
        tearOff.Setup(t => t.RestoreDetachedGroup(It.IsAny<IEnumerable<WorkspaceViewItem>>()))
            .Callback<IEnumerable<WorkspaceViewItem>>(group => recordedGroups.Add(group.Select(i => i.Id).ToList()));
        var tabFactory = new Mock<IPanelTabFactory>();
        tabFactory.Setup(f => f.CreateTab(It.IsAny<string>()))
            .Returns<string>(id => TestWorkspaceItems.Create(id));
        var windowManagement = new Mock<IWindowManagementService>();
        windowManagement.SetupGet(w => w.TearOff).Returns(tearOff.Object);
        windowManagement.SetupGet(w => w.TabFactory).Returns(tabFactory.Object);
        windowManagement.SetupGet(w => w.BoundaryService).Returns(new Mock<IWindowBoundaryService>().Object);

        var restored = CreateManager(windowManagement);
        restored.Restore(settings);

        Assert.Contains(recordedGroups, g => g.SequenceEqual(new[] { "D", "A", "B" }));
        Assert.Contains(recordedGroups, g => g.SequenceEqual(new[] { "C" }));
    }
}
