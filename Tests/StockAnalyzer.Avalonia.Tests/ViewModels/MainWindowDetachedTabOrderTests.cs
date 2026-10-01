using System.Collections.Generic;
using System.Threading.Tasks;
using Moq;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Tests.TestHelpers;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Core.Models.UI;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

// Shares the static WeakReferenceMessenger.Default with other MainWindowViewModel/TickerListViewModel-hosting tests.
[Collection("MessengerSharedState")]
public class MainWindowDetachedTabOrderTests
{
    private const string ContainerId = "container-1";

    private static DetachedTabOrderChangedMessage NewMessage(out WorkspaceViewItem[] items)
    {
        items = new[]
        {
            TestWorkspaceItems.Create("A"),
            TestWorkspaceItems.Create("B"),
        };
        return new DetachedTabOrderChangedMessage(ContainerId, items);
    }

    [Fact]
    public async Task Receive_WhenLoadedAndApplied_ReordersRegistryThenRequestsDebouncedSave()
    {
        var h = MainWindowViewModelFactory.Create();
        using var vm = h.Vm;
        await vm.TryLoadDefaultWorkspaceAsync();
        var message = NewMessage(out _);
        h.TabManager.Setup(m => m.ReorderContainerItems(ContainerId, message.OrderedItems)).Returns(true);

        vm.Receive(message);

        h.TabManager.Verify(m => m.ReorderContainerItems(ContainerId, message.OrderedItems), Times.Once);
        h.Scheduler.Verify(s => s.RequestSave(LayoutChangeReason.TabMoved), Times.Once);
    }

    [Fact]
    public async Task Receive_WhenManagerRejects_DoesNotRequestSave()
    {
        var h = MainWindowViewModelFactory.Create();
        using var vm = h.Vm;
        await vm.TryLoadDefaultWorkspaceAsync();
        h.Scheduler.Invocations.Clear();
        var message = NewMessage(out _);
        h.TabManager.Setup(m => m.ReorderContainerItems(It.IsAny<string>(), It.IsAny<IReadOnlyList<WorkspaceViewItem>>())).Returns(false);

        vm.Receive(message);

        h.Scheduler.Verify(s => s.RequestSave(It.IsAny<LayoutChangeReason>()), Times.Never);
    }

    [Fact]
    public void Receive_WhenNotLoaded_AppliesOrderButDoesNotRequestSave()
    {
        var h = MainWindowViewModelFactory.Create();
        using var vm = h.Vm;
        var message = NewMessage(out _);
        h.TabManager.Setup(m => m.ReorderContainerItems(It.IsAny<string>(), It.IsAny<IReadOnlyList<WorkspaceViewItem>>())).Returns(true);

        vm.Receive(message);

        h.TabManager.Verify(m => m.ReorderContainerItems(ContainerId, message.OrderedItems), Times.Once);
        h.Scheduler.Verify(s => s.RequestSave(It.IsAny<LayoutChangeReason>()), Times.Never);
    }

    [Fact]
    public async Task Receive_WhileAutoSavePaused_AppliesOrderButDoesNotRequestSave()
    {
        var h = MainWindowViewModelFactory.Create();
        using var vm = h.Vm;
        await vm.TryLoadDefaultWorkspaceAsync();
        h.Scheduler.Invocations.Clear();
        var message = NewMessage(out _);
        h.TabManager.Setup(m => m.ReorderContainerItems(It.IsAny<string>(), It.IsAny<IReadOnlyList<WorkspaceViewItem>>())).Returns(true);

        using (vm.PauseAutoSave())
        {
            vm.Receive(message);
        }

        h.TabManager.Verify(m => m.ReorderContainerItems(ContainerId, message.OrderedItems), Times.Once);
        h.Scheduler.Verify(s => s.RequestSave(It.IsAny<LayoutChangeReason>()), Times.Never);
    }

    [Fact]
    public async Task Receive_WhenExiting_DoesNothing()
    {
        var h = MainWindowViewModelFactory.Create();
        using var vm = h.Vm;
        await vm.TryLoadDefaultWorkspaceAsync();
        vm.PrepareExit();
        h.Scheduler.Invocations.Clear();
        var message = NewMessage(out _);

        vm.Receive(message);

        h.TabManager.Verify(m => m.ReorderContainerItems(It.IsAny<string>(), It.IsAny<IReadOnlyList<WorkspaceViewItem>>()), Times.Never);
        h.Scheduler.Verify(s => s.RequestSave(It.IsAny<LayoutChangeReason>()), Times.Never);
    }

    [Fact]
    public void Receive_NullMessage_DoesNothing()
    {
        var h = MainWindowViewModelFactory.Create();
        using var vm = h.Vm;

        vm.Receive((DetachedTabOrderChangedMessage)null!);

        h.TabManager.Verify(m => m.ReorderContainerItems(It.IsAny<string>(), It.IsAny<IReadOnlyList<WorkspaceViewItem>>()), Times.Never);
    }
}
