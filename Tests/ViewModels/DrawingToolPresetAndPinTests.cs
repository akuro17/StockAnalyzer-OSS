using System;
using Avalonia.Media;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Core.Constants;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Tests.ViewModels;

public class DrawingToolPresetAndPinTests
{
    private sealed class SynchronousDispatcherService : IDispatcherService
    {
        public void Post(Action action) => action();
        public void Post<T>(Action<T> action, T state) => action(state);
        public System.Threading.Tasks.Task PostAsync(Func<System.Threading.Tasks.Task> action) => action();
        public System.Threading.Tasks.Task PostAsync<TState>(Func<TState, System.Threading.Tasks.Task> action, TState state) => action(state);
        public bool CheckAccess() => true;
        public void VerifyAccess() { }
    }

    [Fact]
    public void PresetService_NonConfigurableTools_ReturnNull()
    {
        var service = new DrawingToolPresetService();
        Assert.Null(service.GetOrCreatePresetObject(DrawingTool.Pointer));
        Assert.Null(service.GetOrCreatePresetObject(DrawingTool.Eraser));
        Assert.Null(service.GetOrCreatePresetObject(DrawingTool.Ruler));
        Assert.Null(service.GetOrCreatePresetObject(DrawingTool.Eyedropper));
    }

    [Fact]
    public void PresetService_ConfigurableTools_ReturnCachedInstance()
    {
        var service = new DrawingToolPresetService();

        var trendLine1 = service.GetOrCreatePresetObject(DrawingTool.TrendLine);
        var trendLine2 = service.GetOrCreatePresetObject(DrawingTool.TrendLine);
        Assert.NotNull(trendLine1);
        Assert.Same(trendLine1, trendLine2);

        var freehand = service.GetOrCreatePresetObject(DrawingTool.Freehand);
        Assert.NotNull(freehand);
        Assert.IsType<FreehandObject>(freehand);

        var highlighter = service.GetOrCreatePresetObject(DrawingTool.Highlighter);
        Assert.NotNull(highlighter);
        Assert.IsType<HighlighterObject>(highlighter);

        var text = service.GetOrCreatePresetObject(DrawingTool.Text);
        Assert.NotNull(text);
        Assert.IsType<TextObject>(text);

        var info = service.GetOrCreatePresetObject(DrawingTool.Information);
        Assert.NotNull(info);
        Assert.IsType<StockAnalyzer.Avalonia.Drawing.Objects.InformationObject>(info);
    }

    [Fact]
    public void PresetService_ApplyPresetToNewObject_CopiesStyleAndParametersPreservingIdentity()
    {
        var service = new DrawingToolPresetService();
        var preset = service.GetOrCreatePresetObject(DrawingTool.TrendLine);
        Assert.NotNull(preset);

        preset.Color = Colors.Lime;
        preset.Thickness = 3.5;
        if (preset is RelativeGeometricRenderer renderer)
        {
            renderer.StrokeStyle = DrawingStrokeStyle.Dash;
        }

        var newTarget = new TrendLineObject(new ChartPoint(DateTime.UtcNow, 100m), new ChartPoint(DateTime.UtcNow.AddDays(1), 200m))
        {
            Color = Colors.Red,
            Thickness = 1.0,
            ZIndex = 42,
            IsSelected = true,
            IsVisible = true,
            IsLocked = false
        };
        var originalId = newTarget.Id;

        service.ApplyPresetToNewObject(DrawingTool.TrendLine, newTarget);

        Assert.Equal(Colors.Lime, newTarget.Color);
        Assert.Equal(3.5, newTarget.Thickness);
        Assert.Equal(DrawingStrokeStyle.Dash, newTarget.StrokeStyle);

        // Invariants: Identity and state must NOT be modified
        Assert.Equal(originalId, newTarget.Id);
        Assert.Equal(2, newTarget.Points.Count);
        Assert.True(newTarget.IsSelected);
        Assert.Equal(42, newTarget.ZIndex);
    }

    [Fact]
    public void DrawingObjectsViewModel_PinDefaultsToFalse_AndTogglesState()
    {
        var dispatcher = new SynchronousDispatcherService();
        var vm = new DrawingObjectsViewModel(dispatcher);

        // Default state is Off (auto-collapse) per specification
        Assert.False(vm.IsPinned);
        Assert.True(vm.IsPropertySectionExpanded);

        // Toggle On
        vm.TogglePinCommand.Execute(null);
        Assert.True(vm.IsPinned);
        Assert.True(vm.IsPropertySectionExpanded);

        // Attempting to collapse when pinned should be ignored (remains expanded)
        vm.TogglePropertySectionCommand.Execute(null);
        Assert.True(vm.IsPropertySectionExpanded);

        // Toggle Off
        vm.TogglePinCommand.Execute(null);
        Assert.False(vm.IsPinned);

        // Unpinned allows collapsing
        vm.TogglePropertySectionCommand.Execute(null);
        Assert.False(vm.IsPropertySectionExpanded);

        // Toggling Pin back on forces expansion to true
        vm.TogglePinCommand.Execute(null);
        Assert.True(vm.IsPinned);
        Assert.True(vm.IsPropertySectionExpanded);
    }

    [Fact]
    public void DrawingObjectsViewModel_ToolChange_UpdatesActiveToolPreset()
    {
        var dispatcher = new SynchronousDispatcherService();
        var chartVm = new ChartViewModel();
        var presetService = new DrawingToolPresetService();
        var vm = new DrawingObjectsViewModel(chartVm, dispatcher, presetService: presetService);

        chartVm.CurrentTool = DrawingTool.TrendLine;
        Assert.True(vm.HasConfigurableActiveTool);
        Assert.NotNull(vm.ActiveToolTemplateObject);
        Assert.False(string.IsNullOrWhiteSpace(vm.ActiveToolDisplayName));

        // Mutating preset color through ViewModel modifies preset and theme context
        vm.ActiveToolPresetColor = Colors.Cyan;
        Assert.Equal(Colors.Cyan, vm.ActiveToolTemplateObject.Color);
        Assert.Equal(Colors.Cyan, DrawingThemeContext.DefaultColor);

        // Switching to non-configurable tool (e.g. Pointer)
        chartVm.CurrentTool = DrawingTool.Pointer;
        Assert.False(vm.HasConfigurableActiveTool);
        Assert.Null(vm.ActiveToolTemplateObject);
    }

    [Fact]
    public void DrawingObjectsViewModel_PresetHiddenParameterTags_HidesCommonAndGeometryTags()
    {
        var dispatcher = new SynchronousDispatcherService();
        var vm = new DrawingObjectsViewModel(dispatcher);

        // Verify SSoT hidden tags match DrawingSettingsDialog FallbackHiddenTags
        Assert.Contains(DrawingParameterTags.Common, vm.PresetHiddenParameterTags);
        Assert.Contains(DrawingParameterTags.Geometry, vm.PresetHiddenParameterTags);
    }

    [Fact]
    public void DrawingToolSidebarViewModel_AutoCloseFromPointerExit_RespectsPinState()
    {
        var chartVm = new ChartViewModel();
        var sidebarVm = new DrawingToolSidebarViewModel(chartVm);
        var objectsVm = sidebarVm.DrawingObjectsViewModel;

        // Initial: unpinned
        Assert.False(objectsVm.IsPinned);
        sidebarVm.IsPresetPanelOpen = true;
        sidebarVm.IsObjectsPanelOpen = true;
        sidebarVm.IsFlyoutOpen = true;

        // Pointer exit when unpinned -> auto-collapses side panels and closes flyout
        sidebarVm.AutoCloseFromPointerExitCommand.Execute(null);
        Assert.False(sidebarVm.IsPresetPanelOpen);
        Assert.False(sidebarVm.IsObjectsPanelOpen);
        Assert.False(sidebarVm.IsFlyoutOpen);

        // Pin the panel
        objectsVm.TogglePinCommand.Execute(null);
        Assert.True(objectsVm.IsPinned);
        sidebarVm.IsPresetPanelOpen = true;
        sidebarVm.IsObjectsPanelOpen = true;
        sidebarVm.IsFlyoutOpen = true;

        // Pointer exit when pinned -> closes flyout but KEEPS open side panels
        sidebarVm.AutoCloseFromPointerExitCommand.Execute(null);
        Assert.True(sidebarVm.IsPresetPanelOpen);
        Assert.True(sidebarVm.IsObjectsPanelOpen);
        Assert.False(sidebarVm.IsFlyoutOpen);
    }

    [Fact]
    public void DrawingToolSidebarViewModel_SelectTool_RestoresPresetPanelWhenPinned()
    {
        var chartVm = new ChartViewModel();
        var sidebarVm = new DrawingToolSidebarViewModel(chartVm);
        var objectsVm = sidebarVm.DrawingObjectsViewModel;

        // Pin the preset panel
        objectsVm.TogglePinCommand.Execute(null);
        Assert.True(objectsVm.IsPinned);

        // Simulate choosing a tool from flyout (where preset panel was temporarily hidden)
        sidebarVm.IsPresetPanelOpen = false;
        sidebarVm.IsFlyoutOpen = true;

        var category = sidebarVm.Categories[1]; // Lines
        var toolItem = category.Tools[0];

        sidebarVm.SelectTool(toolItem);

        // Since panel is pinned, selecting a tool automatically keeps/restores preset panel open
        Assert.False(sidebarVm.IsFlyoutOpen);
        Assert.True(sidebarVm.IsPresetPanelOpen);
    }

    [Fact]
    public void DrawingToolSidebarViewModel_TogglePresetPanel_TogglesAndExcludesOtherPanels()
    {
        var chartVm = new ChartViewModel();
        var sidebarVm = new DrawingToolSidebarViewModel(chartVm);

        Assert.False(sidebarVm.IsPresetPanelOpen);
        Assert.False(sidebarVm.IsObjectsPanelOpen);
        Assert.False(sidebarVm.IsFlyoutOpen);

        // Toggle Preset Panel Open
        sidebarVm.TogglePresetPanelCommand.Execute(null);
        Assert.True(sidebarVm.IsPresetPanelOpen);
        Assert.False(sidebarVm.IsObjectsPanelOpen);

        // Toggle Objects Panel Open -> should close Preset Panel
        sidebarVm.ToggleObjectsPanelCommand.Execute(null);
        Assert.True(sidebarVm.IsObjectsPanelOpen);
        Assert.False(sidebarVm.IsPresetPanelOpen);

        // Toggle Preset Panel Open -> should close Objects Panel
        sidebarVm.TogglePresetPanelCommand.Execute(null);
        Assert.True(sidebarVm.IsPresetPanelOpen);
        Assert.False(sidebarVm.IsObjectsPanelOpen);
    }

    [Fact]
    public void DrawingObjectsViewModel_HasActiveToolSpecificSettings_AndDirectPresetEditing()
    {
        var dispatcher = new SynchronousDispatcherService();
        var chartVm = new ChartViewModel();
        var presetService = new DrawingToolPresetService();
        var vm = new DrawingObjectsViewModel(chartVm, dispatcher, presetService: presetService);

        // Pointer: no specific settings
        chartVm.CurrentTool = DrawingTool.Pointer;
        Assert.False(vm.HasActiveToolSpecificSettings);

        // Freehand: only general settings, no specific settings
        chartVm.CurrentTool = DrawingTool.Freehand;
        Assert.False(vm.HasActiveToolSpecificSettings);

        // FibonacciRetracement: has specific settings (ShowExtensions, ExtendMode)
        chartVm.CurrentTool = DrawingTool.FibonacciRetracement;
        Assert.True(vm.HasActiveToolSpecificSettings);
        Assert.NotNull(vm.ActiveToolTemplateObject);

        // TrendLine: has specific settings (ShowProjection, ProjectionColumns)
        chartVm.CurrentTool = DrawingTool.TrendLine;
        Assert.True(vm.HasActiveToolSpecificSettings);

        // Verify FibonacciRetracement properties are reflected for dynamic in-preset UI
        var metadata = DrawingParameterViewBuilder.GetOrReflectMetadata(typeof(FibonacciRetracementObject));
        Assert.Contains(metadata, m => m.Prop.Name == nameof(FibonacciRetracementObject.ShowExtensions));
        Assert.Contains(metadata, m => m.Prop.Name == nameof(FibonacciRetracementObject.ExtendMode));

        // Verify base properties are cleanly excluded from specific settings
        Assert.DoesNotContain(metadata, m => m.Prop.Name == nameof(IChartObject.Color));
        Assert.DoesNotContain(metadata, m => m.Prop.Name == nameof(IChartObject.Thickness));
        Assert.DoesNotContain(metadata, m => m.Prop.Name == nameof(IChartObject.Id));
        Assert.DoesNotContain(metadata, m => m.Prop.Name == nameof(IChartObject.Points));

        // Verify builder generates in-preset controls for FibonacciRetracement specific settings
        chartVm.CurrentTool = DrawingTool.FibonacciRetracement;
        var builder = new DrawingParameterViewBuilder();
        var control = builder.Build(vm.ActiveToolTemplateObject!, vm.PresetHiddenParameterTags);
        Assert.NotNull(control);
        var panel = Assert.IsType<global::Avalonia.Controls.StackPanel>(control);
        Assert.NotEmpty(panel.Children);

        // Verify in-preset property modifications directly apply to newly created shapes
        var fibPreset = (FibonacciRetracementObject)vm.ActiveToolTemplateObject!;
        fibPreset.ShowExtensions = true;
        fibPreset.ExtendMode = FibonacciRetracementExtendMode.ExtendBoth;

        var newFib = new FibonacciRetracementObject(new ChartPoint(DateTime.UtcNow, 100m), new ChartPoint(DateTime.UtcNow.AddDays(1), 200m));
        presetService.ApplyPresetToNewObject(DrawingTool.FibonacciRetracement, newFib);

        Assert.True(newFib.ShowExtensions);
        Assert.Equal(FibonacciRetracementExtendMode.ExtendBoth, newFib.ExtendMode);
    }
}
