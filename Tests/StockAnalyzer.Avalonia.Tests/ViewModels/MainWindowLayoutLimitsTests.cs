using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Moq;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Tests.TestHelpers;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Models.UI;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

/// <summary>
/// The tab-count cap and the reorder-distance limit are configuration ("Layout" section of appsettings.json), read
/// through <see cref="LayoutStateStore"/>; the view models must follow the configured values, not compiled numbers.
/// </summary>
[Collection("MessengerSharedState")]
public class MainWindowLayoutLimitsTests
{
    private static void Fill(ObservableCollection<WorkspaceViewItem> panel, int count)
    {
        panel.Clear();
        for (int i = 0; i < count; i++) panel.Add(TestWorkspaceItems.Create($"T{i}"));
    }

    [Fact]
    public void Redock_FollowsTheConfiguredPanelCap()
    {
        var h = MainWindowViewModelFactory.Create(layoutSettings: new LayoutSettings { MaxPanelTabs = 3 });
        using var vm = h.Vm;
        Fill(vm.LeftPanelTabs, 3);
        var overflow = TestWorkspaceItems.Create("Overflow", originalPanel: "Left");

        vm.Receive(new RestoreRequestMessage(overflow));

        Assert.Equal(3, vm.LeftPanelTabs.Count);
        h.TearOff.Verify(t => t.TryRehostDetached(It.Is<IReadOnlyList<WorkspaceViewItem>>(hosted => hosted.Single() == overflow)), Times.Once);
    }

    [Fact]
    public void Redock_DocksWhileBelowTheConfiguredPanelCap()
    {
        var h = MainWindowViewModelFactory.Create(layoutSettings: new LayoutSettings { MaxPanelTabs = 3 });
        using var vm = h.Vm;
        Fill(vm.LeftPanelTabs, 2);
        var fits = TestWorkspaceItems.Create("Fits", originalPanel: "Left");

        vm.Receive(new RestoreRequestMessage(fits));

        Assert.Same(fits, vm.LeftPanelTabs.Last());
        Assert.Equal(2, vm.LeftSelectedTabIndex);
    }

    [Fact]
    public void ReorderTab_FollowsTheConfiguredDistanceLimit()
    {
        var h = MainWindowViewModelFactory.Create(layoutSettings: new LayoutSettings { MaxTabReorderDistance = 1 });
        using var vm = h.Vm;
        Fill(vm.LeftPanelTabs, 4);

        vm.ReorderTabCommand.Execute("Left:0:3");

        Assert.Equal(new[] { "T1", "T0", "T2", "T3" }, vm.LeftPanelTabs.Select(t => t.Id).ToArray());
    }

    [Fact]
    public void ExampleAppsettings_BindsTheLayoutSectionToTheSettingsDefaults()
    {
        var path = Path.Combine(TestSolution.Root, "StockAnalyzer.Avalonia", "appsettings.example.json");
        var configuration = new ConfigurationBuilder().AddJsonFile(path, optional: false).Build();
        var section = configuration.GetSection("Layout");

        Assert.True(section.Exists(), "appsettings.example.json has no \"Layout\" section.");

        var bound = section.Get<LayoutSettings>();
        var defaults = new LayoutSettings();
        Assert.NotNull(bound);
        Assert.Equal(defaults.MaxPanelTabs, bound!.MaxPanelTabs);
        Assert.Equal(defaults.MaxTabReorderDistance, bound.MaxTabReorderDistance);
    }
}
