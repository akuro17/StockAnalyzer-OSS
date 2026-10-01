using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using CommunityToolkit.Mvvm.Messaging;
using Moq;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Tests.TestHelpers;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Core.Models.UI;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Services;

/// <summary>
/// <see cref="ITearOffService.TryRehostDetached"/> reports whether the group window was shown, undoes its own partial
/// changes on failure and never answers a failure with another redock request (the loop a full panel used to allow).
/// </summary>
public class TearOffServiceRehostTests
{
    private sealed class FakeContainer : IDetachedWindowContainer
    {
        public string ContainerId { get; } = "new-container";
        public List<WorkspaceViewItem> Added { get; } = new();
        public void AddItem(WorkspaceViewItem item) => Added.Add(item);
    }

    private sealed class Harness
    {
        public Mock<IDetachedWindowFactory> Factory { get; } = new();
        public Mock<IContainerRegistry> Registry { get; } = new();
        public List<RestoreRequestMessage> Requests { get; } = new();
        public TearOffService Service { get; }

        public Harness(bool withMainWindow = true)
        {
            var messenger = new StrongReferenceMessenger();
            messenger.Register<Harness, RestoreRequestMessage>(this, static (h, m) => h.Requests.Add(m));
            Service = new TearOffService(
                messenger, Factory.Object, Registry.Object, new SynchronousDispatcherService(),
                mainWindowProvider: () => withMainWindow ? new Window() : null);
        }
    }

    [Fact]
    public void TryRehostDetached_WithNoItems_ReturnsFalse()
    {
        var h = new Harness();

        Assert.False(h.Service.TryRehostDetached(new List<WorkspaceViewItem>()));
    }

    [AvaloniaFact]
    public void TryRehostDetached_WithoutAMainWindow_ReturnsFalseAndSendsNoRedockRequest()
    {
        var h = new Harness(withMainWindow: false);
        var item = TestWorkspaceItems.Create("A", containerId: "old-container");

        var hosted = h.Service.TryRehostDetached(new[] { item });

        Assert.False(hosted);
        Assert.Equal("old-container", item.ContainerId);
        Assert.Empty(h.Requests);
        h.Factory.Verify(f => f.ShowWindow(It.IsAny<object>(), It.IsAny<object?>()), Times.Never);
    }

    [AvaloniaFact]
    public void TryRehostDetached_WhenShowingTheWindowFails_UndoesItsChangesAndSendsNoRedockRequest()
    {
        var h = new Harness();
        var container = new FakeContainer();
        var first = TestWorkspaceItems.Create("A", containerId: "old-container");
        var second = TestWorkspaceItems.Create("B", containerId: "old-container");
        var window = new Window { DataContext = container };
        h.Factory.Setup(f => f.CreateWindow(It.IsAny<object>(), first)).Returns(() =>
        {
            first.ContainerId = container.ContainerId; // what the real factory's AddItem does
            return window;
        });
        h.Factory.Setup(f => f.ShowWindow(It.IsAny<object>(), It.IsAny<object?>())).Throws(new System.InvalidOperationException("show failed"));

        var hosted = h.Service.TryRehostDetached(new[] { first, second });

        Assert.False(hosted);
        Assert.Equal("old-container", first.ContainerId);
        Assert.Equal("old-container", second.ContainerId);
        h.Registry.Verify(r => r.Unregister(container.ContainerId), Times.AtLeastOnce);
        Assert.Empty(h.Requests);
    }

    [AvaloniaFact]
    public void TryRehostDetached_WhenTheWindowIsShown_HostsAllItemsInOneContainer()
    {
        var h = new Harness();
        var container = new FakeContainer();
        var first = TestWorkspaceItems.Create("A", containerId: "old-container");
        var second = TestWorkspaceItems.Create("B", containerId: "old-container");
        var third = TestWorkspaceItems.Create("C", containerId: "old-container");
        var window = new Window { DataContext = container };
        h.Factory.Setup(f => f.CreateWindow(It.IsAny<object>(), first)).Returns(window);

        var hosted = h.Service.TryRehostDetached(new[] { first, second, third });

        Assert.True(hosted);
        Assert.Equal(new[] { second, third }, container.Added.ToArray());
        Assert.All(new[] { second, third }, i => Assert.Equal(container.ContainerId, i.ContainerId));
        h.Factory.Verify(f => f.ShowWindow(window, It.IsAny<object?>()), Times.Once);
        Assert.Empty(h.Requests);
    }

    [AvaloniaFact]
    public void TryRehostDetached_WhenAGroupWindowHasNoContainer_FailsInsteadOfDroppingTheRestOfTheGroup()
    {
        var h = new Harness();
        var first = TestWorkspaceItems.Create("A", containerId: "old-container");
        var second = TestWorkspaceItems.Create("B", containerId: "old-container");
        h.Factory.Setup(f => f.CreateWindow(It.IsAny<object>(), first)).Returns(new Window());

        var hosted = h.Service.TryRehostDetached(new[] { first, second });

        Assert.False(hosted);
        h.Factory.Verify(f => f.ShowWindow(It.IsAny<object>(), It.IsAny<object?>()), Times.Never);
        Assert.Equal("old-container", second.ContainerId);
    }
}
