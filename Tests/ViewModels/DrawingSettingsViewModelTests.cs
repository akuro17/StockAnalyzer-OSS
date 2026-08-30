using System;
using System.Threading.Tasks;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Settings;
using Xunit;

namespace StockAnalyzer.Tests.ViewModels;

public class DrawingSettingsViewModelTests
{
    [Fact]
    public void ViewModel_InitializesWithDefaultSmartGuideSettings()
    {
        var settingsManager = new MockChartSettingsManager();
        var vm = new DrawingSettingsViewModel(settingsManager);

        Assert.True(vm.SmartGuidesEnabled);
        Assert.Equal(5.0, vm.SmartGuideSnapDistance);
        Assert.False(vm.IsModified);
    }

    [Fact]
    public void ViewModel_IsModified_TracksSmartGuideChanges()
    {
        var settingsManager = new MockChartSettingsManager();
        var vm = new DrawingSettingsViewModel(settingsManager);

        Assert.False(vm.IsModified);

        // Modify SmartGuidesEnabled
        vm.SmartGuidesEnabled = false;
        Assert.True(vm.IsModified);

        // Revert
        vm.SmartGuidesEnabled = true;
        Assert.False(vm.IsModified);

        // Modify SmartGuideSnapDistance
        vm.SmartGuideSnapDistance = 10.0;
        Assert.True(vm.IsModified);

        // Revert
        vm.SmartGuideSnapDistance = 5.0;
        Assert.False(vm.IsModified);
    }

    [Fact]
    public void ViewModel_InitializesWithDefaultControlPointHideTimeout()
    {
        var settingsManager = new MockChartSettingsManager();
        var vm = new DrawingSettingsViewModel(settingsManager);

        Assert.Equal(ChartSettingsConstants.DefaultControlPointHideTimeoutSeconds, vm.ControlPointHideTimeoutSeconds);
        Assert.False(vm.IsModified);
    }

    [Fact]
    public void ViewModel_IsModified_TracksControlPointHideTimeoutChanges()
    {
        var settingsManager = new MockChartSettingsManager();
        var vm = new DrawingSettingsViewModel(settingsManager);

        Assert.False(vm.IsModified);

        vm.ControlPointHideTimeoutSeconds = 30;
        Assert.True(vm.IsModified);

        vm.ControlPointHideTimeoutSeconds = ChartSettingsConstants.DefaultControlPointHideTimeoutSeconds;
        Assert.False(vm.IsModified);
    }

    [Fact]
    public async Task SaveChangesAsync_ClampsControlPointHideTimeoutAboveMaximum()
    {
        var settingsManager = new MockChartSettingsManager();
        var vm = new DrawingSettingsViewModel(settingsManager);

        vm.ControlPointHideTimeoutSeconds = ChartSettingsConstants.MaxControlPointHideTimeoutSeconds + 100;
        await vm.SaveChangesAsync();

        Assert.Equal(ChartSettingsConstants.MaxControlPointHideTimeoutSeconds, settingsManager.Current.ControlPointHideTimeoutSeconds);
    }

    [Fact]
    public async Task SaveChangesAsync_ClampsControlPointHideTimeoutBelowMinimum()
    {
        var settingsManager = new MockChartSettingsManager();
        var vm = new DrawingSettingsViewModel(settingsManager);

        vm.ControlPointHideTimeoutSeconds = ChartSettingsConstants.MinControlPointHideTimeoutSeconds - 1;
        await vm.SaveChangesAsync();

        Assert.Equal(ChartSettingsConstants.MinControlPointHideTimeoutSeconds, settingsManager.Current.ControlPointHideTimeoutSeconds);
    }

    [Fact]
    public void ViewModel_InitializesWithDefaultContinuationMode()
    {
        var settingsManager = new MockChartSettingsManager();
        var vm = new DrawingSettingsViewModel(settingsManager);

        Assert.Equal(DrawingToolContinuationMode.ReturnToPointer, vm.DrawingToolContinuationMode);
        Assert.False(vm.IsModified);
    }

    [Fact]
    public void ViewModel_IsModified_TracksContinuationModeChanges()
    {
        var settingsManager = new MockChartSettingsManager();
        var vm = new DrawingSettingsViewModel(settingsManager);

        Assert.False(vm.IsModified);

        vm.DrawingToolContinuationMode = DrawingToolContinuationMode.ContinueDrawing;
        Assert.True(vm.IsModified);

        vm.DrawingToolContinuationMode = DrawingToolContinuationMode.ReturnToPointer;
        Assert.False(vm.IsModified);
    }

    [Fact]
    public async Task SaveChangesAsync_PersistsContinuationMode()
    {
        var settingsManager = new MockChartSettingsManager();
        var vm = new DrawingSettingsViewModel(settingsManager);

        vm.DrawingToolContinuationMode = DrawingToolContinuationMode.ContinueDrawing;
        await vm.SaveChangesAsync();

        Assert.Equal(DrawingToolContinuationMode.ContinueDrawing, settingsManager.Current.DrawingToolContinuationMode);
    }
}
