using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Tests.Services;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.Tests.TestHelpers;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Models.UI;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

public class DetachedWindowTabReorderTests
{
    private sealed class OrderRecorder
    {
        public List<DetachedTabOrderChangedMessage> Received { get; } = new();
    }

    private static (DetachedWindowViewModel Vm, StrongReferenceMessenger Messenger, OrderRecorder Recorder, WorkspaceViewItem[] Items) Create(int itemCount = 4, LayoutStateStore? layoutStore = null)
    {
        var messenger = new StrongReferenceMessenger();
        var recorder = new OrderRecorder();
        messenger.Register<OrderRecorder, DetachedTabOrderChangedMessage>(recorder, static (r, m) => r.Received.Add(m));

        var services = new ServiceCollection().BuildServiceProvider();
        var vm = new DetachedWindowViewModel(
            services, new MockPanelTabFactory(), new MockTearOffService(), new ContainerRegistry(), messenger, new SynchronousDispatcherService(), stateStore: layoutStore);

        var items = Enumerable.Range(0, itemCount).Select(i => TestWorkspaceItems.Create(((char)('A' + i)).ToString())).ToArray();
        foreach (var item in items) vm.AddItem(item);
        return (vm, messenger, recorder, items);
    }

    private static string Order(DetachedWindowViewModel vm) => string.Concat(vm.Items.Select(i => i.Id));

    [Fact]
    public void MoveTab_ForwardMove_ReordersAndSelectsMovedTab()
    {
        var (vm, _, recorder, items) = Create();
        using (vm)
        {
            vm.MoveTabCommand.Execute(new TabMoveRequest(0, 2));

            Assert.Equal("BCAD", Order(vm));
            Assert.Same(items[0], vm.SelectedItem);
            Assert.Single(recorder.Received);
        }
    }

    [Fact]
    public void MoveTab_BackwardMove_ReordersAndSelectsMovedTab()
    {
        var (vm, _, _, items) = Create();
        using (vm)
        {
            vm.MoveTabCommand.Execute(new TabMoveRequest(3, 0));

            Assert.Equal("DABC", Order(vm));
            Assert.Same(items[3], vm.SelectedItem);
        }
    }

    [Fact]
    public void MoveTab_SameIndex_IsNoOpWithoutMessageOrSelectionChange()
    {
        var (vm, _, recorder, items) = Create();
        using (vm)
        {
            vm.SelectedItem = items[2];

            vm.MoveTabCommand.Execute(new TabMoveRequest(1, 1));

            Assert.Equal("ABCD", Order(vm));
            Assert.Same(items[2], vm.SelectedItem);
            Assert.Empty(recorder.Received);
        }
    }

    [Fact]
    public void MoveTab_TargetBeyondEnd_MovesToLast()
    {
        var (vm, _, _, _) = Create();
        using (vm)
        {
            vm.MoveTabCommand.Execute(new TabMoveRequest(0, 99));

            Assert.Equal("BCDA", Order(vm));
        }
    }

    [Fact]
    public void MoveTab_NegativeTarget_MovesToFirst()
    {
        var (vm, _, _, _) = Create();
        using (vm)
        {
            vm.MoveTabCommand.Execute(new TabMoveRequest(2, -5));

            Assert.Equal("CABD", Order(vm));
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    public void MoveTab_SourceOutOfRange_LeavesOrderAndSendsNothing(int source)
    {
        var (vm, _, recorder, _) = Create();
        using (vm)
        {
            vm.MoveTabCommand.Execute(new TabMoveRequest(source, 1));

            Assert.Equal("ABCD", Order(vm));
            Assert.Empty(recorder.Received);
        }
    }

    [Fact]
    public void MoveTab_AfterDispose_DoesNothing()
    {
        var (vm, _, recorder, _) = Create();
        // Items are cleared by Dispose, so capture the state through the message stream only.
        vm.Dispose();

        vm.MoveTabCommand.Execute(new TabMoveRequest(0, 1));

        Assert.Empty(recorder.Received);
        Assert.Empty(vm.Items);
    }

    [Fact]
    public void MoveTab_MessagePayload_CarriesContainerIdAndSnapshotInNewOrder()
    {
        var (vm, _, recorder, items) = Create();
        using (vm)
        {
            vm.MoveTabCommand.Execute(new TabMoveRequest(0, 2));

            var message = Assert.Single(recorder.Received);
            Assert.Equal(vm.ContainerId, message.ContainerId);
            Assert.Equal(new[] { items[1], items[2], items[0], items[3] }, message.OrderedItems);

            // Snapshot: later mutation of Items must not change the payload.
            vm.Items.Move(0, 3);
            Assert.Equal(new[] { items[1], items[2], items[0], items[3] }, message.OrderedItems);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void MoveTab_EmptyOrSingleItem_SendsNoMessage(int itemCount)
    {
        var (vm, _, recorder, _) = Create(itemCount);
        using (vm)
        {
            vm.MoveTabCommand.Execute(new TabMoveRequest(0, 1));

            Assert.Empty(recorder.Received);
        }
    }

    [Fact]
    public void MoveTabCommand_NullParameter_CannotExecute()
    {
        var (vm, _, _, _) = Create();
        using (vm)
        {
            Assert.False(vm.MoveTabCommand.CanExecute(null));
            Assert.True(vm.MoveTabCommand.CanExecute(new TabMoveRequest(0, 1)));
        }
    }

    [AvaloniaTheory]
    [InlineData(0)] // move the selected tab
    [InlineData(2)] // move a tab that is not selected
    public void MoveTab_BoundTabControl_KeepsSelectionOnMovedTab(int initiallySelectedIndex)
    {
        var (vm, _, _, items) = Create();
        using (vm)
        {
            var tabControl = new TabControl { DataContext = vm };
            tabControl.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(DetachedWindowViewModel.Items)));
            tabControl.Bind(SelectingItemsControl.SelectedItemProperty, new Binding(nameof(DetachedWindowViewModel.SelectedItem)) { Mode = BindingMode.TwoWay });
            var window = new Window { Content = tabControl, Width = 400, Height = 300 };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            vm.SelectedItem = items[initiallySelectedIndex];
            Dispatcher.UIThread.RunJobs();

            vm.MoveTabCommand.Execute(new TabMoveRequest(0, 3));
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("BCDA", Order(vm));
            Assert.Same(items[0], vm.SelectedItem);
            Assert.Same(items[0], tabControl.SelectedItem);

            window.Close();
        }
    }

    [Fact]
    public void MoveTab_HonorsTheConfiguredMaxTabReorderDistance()
    {
        var store = new LayoutStateStore(null, new LayoutSettings { MaxTabReorderDistance = 1 });
        var (vm, _, _, _) = Create(4, store);
        using (vm)
        {
            vm.MoveTabCommand.Execute(new TabMoveRequest(0, 3));

            Assert.Equal("BACD", Order(vm));
        }
    }

    [Fact]
    public void AddTab_HonorsTheConfiguredMaxPanelTabs()
    {
        var store = new LayoutStateStore(null, new LayoutSettings { MaxPanelTabs = 2 });
        var factory = new MockPanelTabFactory { CreateTabFunc = id => TestWorkspaceItems.Create(id) };
        var vm = new DetachedWindowViewModel(
            new ServiceCollection().BuildServiceProvider(), factory, new MockTearOffService(), new ContainerRegistry(),
            new StrongReferenceMessenger(), new SynchronousDispatcherService(), stateStore: store);
        using (vm)
        {
            vm.AddTabCommand.Execute("X");
            vm.AddTabCommand.Execute("Y");
            vm.AddTabCommand.Execute("Z");

            Assert.Equal(2, vm.Items.Count);
        }
    }
}
