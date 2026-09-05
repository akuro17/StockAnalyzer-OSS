using System;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Tests.Services;
using StockAnalyzer.Avalonia.ViewModels;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

public class DrawingToolSidebarViewModelTests
{
    [Fact]
    public void DrawingToolSidebarViewModel_ToggleObjectsPanel_TogglesStateAndClosesCategoryFlyout()
    {
        var dispatcher = new SynchronousDispatcherService();
        var chartVm = new ChartViewModel();
        var objectsVm = new DrawingObjectsViewModel(chartVm, dispatcher);
        var sidebarVm = new DrawingToolSidebarViewModel(chartVm, objectsVm);

        Assert.False(sidebarVm.IsObjectsPanelOpen);

        // Open a category flyout first
        var firstCategory = sidebarVm.Categories[0];
        sidebarVm.OpenCategory(firstCategory);
        Assert.True(sidebarVm.IsFlyoutOpen);
        Assert.Equal(firstCategory, sidebarVm.ExpandedCategory);

        // Toggle objects panel open
        sidebarVm.ToggleObjectsPanelCommand.Execute(null);
        Assert.True(sidebarVm.IsObjectsPanelOpen);
        Assert.False(sidebarVm.IsFlyoutOpen); // Category flyout should close

        // Toggle objects panel closed
        sidebarVm.ToggleObjectsPanelCommand.Execute(null);
        Assert.False(sidebarVm.IsObjectsPanelOpen);

        // Test explicit CloseObjectsPanelCommand
        sidebarVm.ToggleObjectsPanelCommand.Execute(null);
        Assert.True(sidebarVm.IsObjectsPanelOpen);
        sidebarVm.CloseObjectsPanelCommand.Execute(null);
        Assert.False(sidebarVm.IsObjectsPanelOpen);
    }

    // Regression test: OpenCategory() previously didn't close the Layer Management (Objects)
    // panel, so once it was open, hovering another drawing-tool category set IsFlyoutOpen = true
    // internally but the still-visible Objects panel (declared later in the XAML, so higher
    // z-order in the same grid cell) covered it -- the category flyout was invisible/unusable
    // until Layer Management was manually closed first.
    [Fact]
    public void DrawingToolSidebarViewModel_OpenCategory_ClosesObjectsPanel()
    {
        var dispatcher = new SynchronousDispatcherService();
        var chartVm = new ChartViewModel();
        var objectsVm = new DrawingObjectsViewModel(chartVm, dispatcher);
        var sidebarVm = new DrawingToolSidebarViewModel(chartVm, objectsVm);

        // Open the Layer Management panel first.
        sidebarVm.ToggleObjectsPanelCommand.Execute(null);
        Assert.True(sidebarVm.IsObjectsPanelOpen);

        // Hovering a drawing-tool category while Layer Management is open must close it.
        var firstCategory = sidebarVm.Categories[0];
        sidebarVm.OpenCategory(firstCategory);

        Assert.False(sidebarVm.IsObjectsPanelOpen);
        Assert.True(sidebarVm.IsFlyoutOpen);
        Assert.Equal(firstCategory, sidebarVm.ExpandedCategory);
    }

    [Fact]
    public void DrawingToolSidebarViewModel_SetChartViewModel_PropagatesToDrawingObjectsViewModel()
    {
        var dispatcher = new SynchronousDispatcherService();
        var chartVm1 = new ChartViewModel();
        var chartVm2 = new ChartViewModel();
        var objectsVm = new DrawingObjectsViewModel(chartVm1, dispatcher);
        var sidebarVm = new DrawingToolSidebarViewModel(chartVm1, objectsVm);

        Assert.Equal(chartVm1.ObjectManager, sidebarVm.DrawingObjectsViewModel.ObjectManager);

        // Switch to chartVm2
        sidebarVm.SetChartViewModel(chartVm2);
        Assert.Equal(chartVm2.ObjectManager, sidebarVm.DrawingObjectsViewModel.ObjectManager);
    }
}
