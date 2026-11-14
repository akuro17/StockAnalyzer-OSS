using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.LogicalTree;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Views.Controls;
using StockAnalyzer.Avalonia.Views.Dialogs;
using StockAnalyzer.Core.Constants;
using StockAnalyzer.Core.Drawing;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views;

/// <summary>
/// The tool-specific parameters ExtendMode (Trend Line / Extended Line) and ChannelMode (Parallel Channel) must be editable
/// in the drawing settings dialog, and for the Trend Line the dedicated panel (projection) and the shared editor (extend)
/// must both be saved by OK.
/// </summary>
public sealed class TrendLineAndChannelSettingsDialogTests
{
    private static readonly DateTime T0 = new(2026, 1, 1);

    private static IDrawingSettingsPanelRegistry TrendLineRegistry()
    {
        var registry = new DrawingSettingsPanelRegistry();
        registry.Register(new TrendLineSettingsPanelDefinition());
        return registry;
    }

    private static void Pump()
    {
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static ComboBox FindEnumCombo<TEnum>(DrawingSettingsDialog dialog) where TEnum : struct, Enum
    {
        var dynamicView = dialog.FindControl<DynamicDrawingSettingsView>("DynamicSettingsView");
        Assert.NotNull(dynamicView);
        Assert.True(dynamicView!.IsVisible);
        return Assert.Single(dynamicView.GetLogicalDescendants().OfType<ComboBox>(),
            c => c.ItemsSource is System.Collections.IEnumerable items && items.Cast<object>().All(i => i is TEnum) && items.Cast<object>().Any());
    }

    [Fact]
    public void TrendLinePanelDefinition_UsesTheSharedEditor_AndHidesWhatTheOtherControlsOwn()
    {
        var definition = new TrendLineSettingsPanelDefinition();

        Assert.True(definition.UsesDynamicSettings);
        Assert.NotNull(definition.DynamicHiddenParameterTags);
        Assert.Contains(DrawingParameterTags.Common, definition.DynamicHiddenParameterTags!);
        Assert.Contains(DrawingParameterTags.Geometry, definition.DynamicHiddenParameterTags!);
        Assert.Contains(DrawingParameterTags.Projection, definition.DynamicHiddenParameterTags!);
    }

    [AvaloniaFact]
    public void TrendLineDialog_ShowsTheExtendEditor_WithoutDuplicatingTheProjectionControls_AndPopulatesBoth()
    {
        var trend = new TrendLineObject(new ChartPoint(T0, 100m), new ChartPoint(T0.AddDays(10), 120m))
        {
            ExtendMode = LineExtendMode.Left,
            ShowProjection = true,
            ProjectionColumns = 7
        };
        var dialog = new DrawingSettingsDialog(trend, TrendLineRegistry());
        try
        {
            dialog.Show();
            Pump();

            var combo = FindEnumCombo<LineExtendMode>(dialog);
            Assert.Equal(LineExtendMode.Left, combo.SelectedItem);

            var dynamicView = dialog.FindControl<DynamicDrawingSettingsView>("DynamicSettingsView")!;
            Assert.Empty(dynamicView.GetLogicalDescendants().OfType<CheckBox>());   // no second "Show Projection"
            Assert.Empty(dynamicView.GetLogicalDescendants().OfType<NumericUpDown>());

            var panel = dialog.FindControl<StackPanel>("TrendLinePanel");
            Assert.NotNull(panel);
            Assert.True(panel!.IsVisible);
            Assert.True(dialog.FindControl<CheckBox>("ShowProjectionCheck")!.IsChecked);
            Assert.Equal(7m, dialog.FindControl<NumericUpDown>("ProjectionColumnsSpin")!.Value);
        }
        finally
        {
            dialog.Close();
        }
    }

    [AvaloniaFact]
    public void TrendLineDialog_Ok_SavesTheSharedEditorValueAndTheDedicatedPanelValuesTogether()
    {
        var trend = new TrendLineObject(new ChartPoint(T0, 100m), new ChartPoint(T0.AddDays(10), 120m));
        Assert.Equal(LineExtendMode.None, trend.ExtendMode);
        Assert.False(trend.ShowProjection);

        var dialog = new DrawingSettingsDialog(trend, TrendLineRegistry());
        try
        {
            dialog.Show();
            Pump();

            FindEnumCombo<LineExtendMode>(dialog).SelectedItem = LineExtendMode.Right;                    // shared editor (live binding)
            dialog.FindControl<CheckBox>("ShowProjectionCheck")!.IsChecked = true;                         // dedicated panel (Commit on OK)
            dialog.FindControl<NumericUpDown>("ProjectionColumnsSpin")!.Value = 25;

            Assert.Equal(LineExtendMode.Right, trend.ExtendMode);
            Assert.False(trend.ShowProjection);   // not committed before OK

            dialog.FindControl<Button>("OkButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump();

            Assert.Equal(LineExtendMode.Right, trend.ExtendMode);
            Assert.True(trend.ShowProjection);
            Assert.Equal(25, trend.ProjectionColumns);
        }
        finally
        {
            if (dialog.IsVisible) dialog.Close();
        }
    }

    [AvaloniaFact]
    public void ParallelChannelDialog_ShowsTheChannelModeEditor_AndTheChoiceReachesTheObject()
    {
        var channel = new ParallelChannelObject(new ChartPoint(T0, 100m), new ChartPoint(T0.AddDays(10), 120m), new ChartPoint(T0.AddDays(4), 90m));
        var dialog = new DrawingSettingsDialog(channel, new DrawingSettingsPanelRegistry());
        try
        {
            dialog.Show();
            Pump();

            var combo = FindEnumCombo<ChannelMode>(dialog);
            Assert.Equal(ChannelMode.Parallel, combo.SelectedItem);

            combo.SelectedItem = ChannelMode.FlatOpposite;

            Assert.Equal(ChannelMode.FlatOpposite, channel.ChannelMode);
        }
        finally
        {
            dialog.Close();
        }
    }

    [AvaloniaFact]
    public void PresetEditor_HidesOnlyCommonAndGeometry_SoTheToolParametersRemainEditable()
    {
        var builder = new DrawingParameterViewBuilder();
        var hidden = new[] { DrawingParameterTags.Common, DrawingParameterTags.Geometry };   // DrawingObjectsViewModel.PresetHiddenParameterTags
        var trend = new TrendLineObject(new ChartPoint(T0, 100m), new ChartPoint(T0.AddDays(10), 120m));
        var channel = new ParallelChannelObject(new ChartPoint(T0, 100m), new ChartPoint(T0.AddDays(10), 120m), new ChartPoint(T0.AddDays(4), 90m));

        var trendView = builder.Build(trend, hidden);
        var channelView = builder.Build(channel, hidden);

        Assert.Single(trendView.GetLogicalDescendants().OfType<ComboBox>());
        Assert.Single(channelView.GetLogicalDescendants().OfType<ComboBox>());
    }
}
