using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Moq;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Tests.TestHelpers;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Core.Models.UI;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

/// <summary>
/// A torn-off panel tab that is redocked into a panel already holding MaxPanelTabs tabs must not be added
/// (the panel would exceed its cap and the selected-index update would throw); it stays detached instead, is
/// re-hosted in a Tab Window (the tabs of one closed window together) and the user is told once, including
/// when the re-host itself fails.
/// </summary>
[Collection("MessengerSharedState")]
public class MainWindowRedockCapacityTests
{
    private const string TitleText = "notice-title";
    private const string LeftLabelText = "left-label";

    private static ObservableCollection<WorkspaceViewItem> Panel(MainWindowViewModel vm, string region) => region switch
    {
        "Left" => vm.LeftPanelTabs,
        "Right" => vm.RightPanelTabs,
        "Top" => vm.TopPanelTabs,
        _ => vm.BottomPanelTabs
    };

    private static int SelectedIndex(MainWindowViewModel vm, string region) => region switch
    {
        "Left" => vm.LeftSelectedTabIndex,
        "Right" => vm.RightSelectedTabIndex,
        "Top" => vm.TopSelectedTabIndex,
        _ => vm.BottomSelectedTabIndex
    };

    private static void Fill(ObservableCollection<WorkspaceViewItem> panel, int count)
    {
        panel.Clear();
        for (int i = 0; i < count; i++) panel.Add(TestWorkspaceItems.Create($"T{i}"));
    }

    private static void SetNoticeTexts(MainWindowViewModelParts h)
    {
        h.Localization.Setup(l => l.GetString(MainWindowViewModel.RedockPanelFullTitleKey)).Returns(TitleText);
        h.Localization.Setup(l => l.GetString(MainWindowViewModel.RedockPanelFullMessageKey)).Returns("full|{0}|{1}");
        h.Localization.Setup(l => l.GetString(MainWindowViewModel.RedockRehostFailedMessageKey)).Returns("failed|{0}|{1}|{2}");
        h.Localization.Setup(l => l.GetString("Menu_ToggleLeftPanel")).Returns(LeftLabelText);
    }

    private static IReadOnlyList<WorkspaceViewItem> Group(params WorkspaceViewItem[] items) =>
        It.Is<IReadOnlyList<WorkspaceViewItem>>(hosted => hosted.SequenceEqual(items));

    private static void VerifyAlerts(MainWindowViewModelParts h, Times times) =>
        h.Dialog.Verify(d => d.ShowAlertAsync(It.IsAny<string>(), It.IsAny<string>()), times);

    [Theory]
    [InlineData("Left")]
    [InlineData("Right")]
    [InlineData("Top")]
    [InlineData("Bottom")]
    public void Redock_IntoFullPanel_KeepsTabDetachedAndDoesNotThrow(string region)
    {
        var h = MainWindowViewModelFactory.Create();
        using var vm = h.Vm;
        var panel = Panel(vm, region);
        Fill(panel, h.Store.MaxPanelTabs);
        var tab = TestWorkspaceItems.Create("Torn", originalPanel: region);

        vm.Receive(new RestoreRequestMessage(tab));

        Assert.Equal(h.Store.MaxPanelTabs, panel.Count);
        Assert.DoesNotContain(tab, panel);
        Assert.True(tab.IsDetached);
        h.TearOff.Verify(t => t.TryRehostDetached(Group(tab)), Times.Once);
        h.TabManager.Verify(m => m.RemoveActiveDetachedTab(It.IsAny<WorkspaceViewItem>()), Times.Never);
    }

    [Fact]
    public void Redock_WithoutOriginalPanel_UsesBottomAndRespectsItsCapacity()
    {
        var h = MainWindowViewModelFactory.Create();
        using var vm = h.Vm;
        Fill(vm.BottomPanelTabs, h.Store.MaxPanelTabs);
        var tab = TestWorkspaceItems.Create("Torn");
        tab.IsDetached = true;

        vm.Receive(new RestoreRequestMessage(tab));

        Assert.Equal(h.Store.MaxPanelTabs, vm.BottomPanelTabs.Count);
        h.TearOff.Verify(t => t.TryRehostDetached(Group(tab)), Times.Once);
    }

    [Theory]
    [InlineData("Left")]
    [InlineData("Right")]
    [InlineData("Top")]
    [InlineData("Bottom")]
    public void Redock_IntoPanelWithOneFreeSlot_DocksAndSelectsTheTab(string region)
    {
        var h = MainWindowViewModelFactory.Create();
        using var vm = h.Vm;
        var panel = Panel(vm, region);
        Fill(panel, h.Store.MaxPanelTabs - 1);
        var tab = TestWorkspaceItems.Create("Torn", originalPanel: region);

        vm.Receive(new RestoreRequestMessage(tab));

        Assert.Equal(h.Store.MaxPanelTabs, panel.Count);
        Assert.Same(tab, panel.Last());
        Assert.False(tab.IsDetached);
        Assert.Equal(h.Store.MaxPanelTabs - 1, SelectedIndex(vm, region));
        h.TabManager.Verify(m => m.RemoveActiveDetachedTab(tab), Times.Once);
        h.TearOff.Verify(t => t.TryRehostDetached(It.IsAny<IReadOnlyList<WorkspaceViewItem>>()), Times.Never);
        VerifyAlerts(h, Times.Never());
    }

    [Fact]
    public void Reorder_InFullPanel_MovesToLastValidIndex()
    {
        var h = MainWindowViewModelFactory.Create();
        using var vm = h.Vm;
        Fill(vm.LeftPanelTabs, h.Store.MaxPanelTabs);

        vm.ReorderTabCommand.Execute($"Left:0:{h.Store.MaxPanelTabs - 1}");

        Assert.Equal("T0", vm.LeftPanelTabs.Last().Id);
        Assert.Equal(h.Store.MaxPanelTabs - 1, vm.LeftSelectedTabIndex);
    }

    [Fact]
    public void Redock_IntoFullPanel_TellsTheUserOnceNamingThePanelAndTheCap()
    {
        var h = MainWindowViewModelFactory.Create();
        using var vm = h.Vm;
        SetNoticeTexts(h);
        Fill(vm.LeftPanelTabs, h.Store.MaxPanelTabs);

        vm.Receive(new RestoreRequestMessage(TestWorkspaceItems.Create("Torn", originalPanel: "Left")));

        h.Dialog.Verify(d => d.ShowAlertAsync(TitleText, $"full|{LeftLabelText}|{h.Store.MaxPanelTabs}"), Times.Once);
        VerifyAlerts(h, Times.Once());
    }

    [Fact]
    public void Redock_IntoFullPanel_WhenTheNoticeFails_StillRehostsTheTab()
    {
        var h = MainWindowViewModelFactory.Create();
        using var vm = h.Vm;
        Fill(vm.LeftPanelTabs, h.Store.MaxPanelTabs);
        h.Dialog.Setup(d => d.ShowAlertAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new System.InvalidOperationException("dialog failed"));
        var tab = TestWorkspaceItems.Create("Torn", originalPanel: "Left");

        var ex = Record.Exception(() => vm.Receive(new RestoreRequestMessage(tab)));

        Assert.Null(ex);
        h.TearOff.Verify(t => t.TryRehostDetached(Group(tab)), Times.Once);
    }

    [Fact]
    public void Redock_IntoFullPanel_WithAMalformedTranslation_DoesNotThrowAndTheTabIsRehostedAgainLater()
    {
        var h = MainWindowViewModelFactory.Create();
        using var vm = h.Vm;
        h.Localization.Setup(l => l.GetString(MainWindowViewModel.RedockPanelFullMessageKey)).Returns("broken {0");
        Fill(vm.LeftPanelTabs, h.Store.MaxPanelTabs);
        var tab = TestWorkspaceItems.Create("Torn", originalPanel: "Left");

        var first = Record.Exception(() => vm.Receive(new RestoreRequestMessage(tab)));
        var second = Record.Exception(() => vm.Receive(new RestoreRequestMessage(tab)));

        Assert.Null(first);
        Assert.Null(second);
        h.TearOff.Verify(t => t.TryRehostDetached(Group(tab)), Times.Exactly(2));
    }

    [Fact]
    public void CloseWindow_WithSeveralTabsIntoFullPanel_RehostsThemTogetherAndTellsTheUserOnce()
    {
        var dispatcher = new QueuedDispatcherService();
        var h = MainWindowViewModelFactory.Create(dispatcher);
        using var vm = h.Vm;
        dispatcher.Discard();
        Fill(vm.LeftPanelTabs, h.Store.MaxPanelTabs);
        var a = TestWorkspaceItems.Create("A", containerId: "closed-window", originalPanel: "Left");
        var b = TestWorkspaceItems.Create("B", containerId: "closed-window", originalPanel: "Left");
        var c = TestWorkspaceItems.Create("C", containerId: "closed-window", originalPanel: "Left");

        // A window X sends one request per tab, all before the dispatcher runs anything.
        vm.Receive(new RestoreRequestMessage(a));
        vm.Receive(new RestoreRequestMessage(b));
        vm.Receive(new RestoreRequestMessage(c));
        dispatcher.RunAll();

        h.TearOff.Verify(t => t.TryRehostDetached(Group(a, b, c)), Times.Once);
        h.TearOff.Verify(t => t.TryRehostDetached(It.IsAny<IReadOnlyList<WorkspaceViewItem>>()), Times.Once);
        VerifyAlerts(h, Times.Once());
    }

    [Fact]
    public void CloseWindows_WithDifferentContainers_RehostsEachContainerAsItsOwnGroup()
    {
        var dispatcher = new QueuedDispatcherService();
        var h = MainWindowViewModelFactory.Create(dispatcher);
        using var vm = h.Vm;
        dispatcher.Discard();
        Fill(vm.LeftPanelTabs, h.Store.MaxPanelTabs);
        var a1 = TestWorkspaceItems.Create("A1", containerId: "window-a", originalPanel: "Left");
        var a2 = TestWorkspaceItems.Create("A2", containerId: "window-a", originalPanel: "Left");
        var b1 = TestWorkspaceItems.Create("B1", containerId: "window-b", originalPanel: "Left");

        vm.Receive(new RestoreRequestMessage(a1));
        vm.Receive(new RestoreRequestMessage(b1));
        vm.Receive(new RestoreRequestMessage(a2));
        dispatcher.RunAll();

        h.TearOff.Verify(t => t.TryRehostDetached(Group(a1, a2)), Times.Once);
        h.TearOff.Verify(t => t.TryRehostDetached(Group(b1)), Times.Once);
        VerifyAlerts(h, Times.Once());
    }

    [Fact]
    public void CloseWindow_WithFewerFreeSlotsThanTabs_DocksWhatFitsAndRehostsTheRestTogether()
    {
        var dispatcher = new QueuedDispatcherService();
        var h = MainWindowViewModelFactory.Create(dispatcher);
        using var vm = h.Vm;
        dispatcher.Discard();
        Fill(vm.LeftPanelTabs, h.Store.MaxPanelTabs - 1);
        var a = TestWorkspaceItems.Create("A", containerId: "closed-window", originalPanel: "Left");
        var b = TestWorkspaceItems.Create("B", containerId: "closed-window", originalPanel: "Left");
        var c = TestWorkspaceItems.Create("C", containerId: "closed-window", originalPanel: "Left");

        vm.Receive(new RestoreRequestMessage(a));
        vm.Receive(new RestoreRequestMessage(b));
        vm.Receive(new RestoreRequestMessage(c));
        dispatcher.RunAll();

        Assert.Same(a, vm.LeftPanelTabs.Last());
        Assert.Equal(h.Store.MaxPanelTabs, vm.LeftPanelTabs.Count);
        h.TearOff.Verify(t => t.TryRehostDetached(Group(b, c)), Times.Once);
    }

    [Fact]
    public void TheSameTabQueuedTwiceInOneTurn_IsRehostedOnce()
    {
        var dispatcher = new QueuedDispatcherService();
        var h = MainWindowViewModelFactory.Create(dispatcher);
        using var vm = h.Vm;
        dispatcher.Discard();
        Fill(vm.LeftPanelTabs, h.Store.MaxPanelTabs);
        var tab = TestWorkspaceItems.Create("Torn", originalPanel: "Left");

        vm.Receive(new RestoreRequestMessage(tab));
        vm.Receive(new RestoreRequestMessage(tab));
        dispatcher.RunAll();

        h.TearOff.Verify(t => t.TryRehostDetached(Group(tab)), Times.Once);
        VerifyAlerts(h, Times.Once());
    }

    [Fact]
    public void WhenTheRehostFails_TheUserGetsTheFailureNoticeAndNoRehostIsRetried()
    {
        var dispatcher = new QueuedDispatcherService();
        var h = MainWindowViewModelFactory.Create(dispatcher);
        using var vm = h.Vm;
        dispatcher.Discard();
        SetNoticeTexts(h);
        Fill(vm.LeftPanelTabs, h.Store.MaxPanelTabs);
        var a = TestWorkspaceItems.Create("A", containerId: "closed-window", originalPanel: "Left");
        var b = TestWorkspaceItems.Create("B", containerId: "closed-window", originalPanel: "Left");
        h.TearOff.Setup(t => t.TryRehostDetached(It.IsAny<IReadOnlyList<WorkspaceViewItem>>())).Returns(false);

        vm.Receive(new RestoreRequestMessage(a));
        vm.Receive(new RestoreRequestMessage(b));
        dispatcher.RunAll();

        h.TearOff.Verify(t => t.TryRehostDetached(It.IsAny<IReadOnlyList<WorkspaceViewItem>>()), Times.Once);
        h.Dialog.Verify(d => d.ShowAlertAsync(TitleText, $"failed|{LeftLabelText}|{h.Store.MaxPanelTabs}|2"), Times.Once);
        VerifyAlerts(h, Times.Once());
        Assert.Equal(0, dispatcher.PendingCount);
        Assert.True(a.IsDetached);
        Assert.True(b.IsDetached);
        h.TabManager.Verify(m => m.RemoveActiveDetachedTab(It.IsAny<WorkspaceViewItem>()), Times.Never);
    }

    [Fact]
    public void WhenTheRehostThrows_TheFailureNoticeIsShownAndNothingEscapes()
    {
        var h = MainWindowViewModelFactory.Create();
        using var vm = h.Vm;
        SetNoticeTexts(h);
        Fill(vm.LeftPanelTabs, h.Store.MaxPanelTabs);
        var tab = TestWorkspaceItems.Create("Torn", originalPanel: "Left");
        h.TearOff.Setup(t => t.TryRehostDetached(It.IsAny<IReadOnlyList<WorkspaceViewItem>>())).Throws(new System.InvalidOperationException("window failed"));

        var ex = Record.Exception(() => vm.Receive(new RestoreRequestMessage(tab)));

        Assert.Null(ex);
        h.Dialog.Verify(d => d.ShowAlertAsync(TitleText, $"failed|{LeftLabelText}|{h.Store.MaxPanelTabs}|1"), Times.Once);
    }

    [Fact]
    public void ARedockedTabThatOverflowedBefore_DocksOnceASlotIsFree_AndIsNotRehostedAgain()
    {
        var h = MainWindowViewModelFactory.Create();
        using var vm = h.Vm;
        Fill(vm.LeftPanelTabs, h.Store.MaxPanelTabs);
        var tab = TestWorkspaceItems.Create("Torn", originalPanel: "Left");
        vm.Receive(new RestoreRequestMessage(tab));
        vm.LeftPanelTabs.RemoveAt(0);

        vm.Receive(new RestoreRequestMessage(tab));

        Assert.Same(tab, vm.LeftPanelTabs.Last());
        Assert.False(tab.IsDetached);
        h.TearOff.Verify(t => t.TryRehostDetached(It.IsAny<IReadOnlyList<WorkspaceViewItem>>()), Times.Once);
    }
}
