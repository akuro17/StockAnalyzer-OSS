#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Tests.Services;
using StockAnalyzer.Avalonia.Tests.TestHelpers;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.Views;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Analysis;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services.Analysis;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views;

/// <summary>The Scatter toolbar with registered metrics: the combo boxes, the registration (gear) button, the log toggles of a fixed axis and the empty state.</summary>
public sealed class ScatterPlotViewMetricToolbarTests
{
    private sealed class TestLocalization : ILocalizationService
    {
        public string GetString(string key) => key;
    }

    private sealed class CountingDialog : FakeDialogService, IDialogService
    {
        public int OpenCount { get; private set; }

        Task<bool> IDialogService.ShowScatterMetricRegistrationDialogAsync()
        {
            OpenCount++;
            return Task.FromResult(true);
        }
    }

    private static ScatterMetricDefinition Metric(string label) => new()
    {
        Source = new ScreenerIndicatorSideConfig
        {
            IndicatorType = IndicatorType.SMA,
            OutputName = label,
            CustomDisplayName = label,
            CategoryType = ScreenerItemCategoryType.Column,
            TimeFrame = TimeFrame.D1,
        },
    };

    private static (ScatterPlotView View, Window Window, ScatterPlotViewModel Vm) Show(
        int metricCount = 2, StubScatterMetricEvaluator? evaluator = null, IDialogService? dialog = null)
    {
        var registry = new FakeScatterMetricRegistry(new[] { Metric("Metric A"), Metric("Metric B"), Metric("A very long metric name that does not fit in the combo") }.Take(metricCount));
        var vm = new ScatterPlotViewModel(
            new WeakReferenceMessenger(),
            marketDataProvider: null,
            new TestLocalization(),
            NullLogger<ScatterPlotViewModel>.Instance,
            new SynchronousDispatcherService(),
            heatmapProvider: SyntheticHeatmapReturnProvider.Instance,
            registry: registry,
            evaluator: evaluator ?? new StubScatterMetricEvaluator(),
            dialogService: dialog);
        var view = new ScatterPlotView { DataContext = vm };
        var window = new Window { Width = 1400, Height = 600, Content = view };
        window.Show();
        Layout(window);
        return (view, window, vm);
    }

    private static void Layout(Window window)
    {
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static T ById<T>(Visual root, string id) where T : Control
        => root.GetVisualDescendants().OfType<T>().First(c => AutomationProperties.GetAutomationId(c) == id);

    private static string ViewMarkup() => File.ReadAllText(Path.Combine(
        StockAnalyzer.TestSupport.TestRepositoryRoot.Find(), "StockAnalyzer.Avalonia", "Views", "ScatterPlotView.axaml"));

    // ===== Combo boxes and the gear button =====

    [AvaloniaFact]
    public void TheCombos_ListOnlyTheRegisteredMetrics_AndAreAsHighAsTheModeButtons()
    {
        var (view, window, vm) = Show();
        try
        {
            var x = ById<ComboBox>(view, "Scatter_XMetricCombo");
            var y = ById<ComboBox>(view, "Scatter_YMetricCombo");
            var scatterButton = view.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("toolbar-btn"));

            Assert.Equal(new[] { "Metric A", "Metric B" }, x.Items.OfType<ScatterMetricOption>().Select(o => o.Label).ToArray());
            Assert.Equal("Metric A", ((ScatterMetricOption)x.SelectedItem!).Label);
            Assert.Equal("Metric B", ((ScatterMetricOption)y.SelectedItem!).Label);
            Assert.Equal(180, x.Width);
            Assert.Equal(180, y.Width);
            Assert.Equal(scatterButton.Bounds.Height, x.Bounds.Height);
            Assert.Equal(scatterButton.Bounds.Height, y.Bounds.Height);
            Assert.Equal(32, x.Bounds.Height);
        }
        finally { window.Close(); }
    }

    // The height alone is not enough: the Fluent ComboBox is top-aligned, so in a line that is taller than 32 it sat above the buttons (seen on screen).
    private static (double Top, double Bottom) Extent(Visual root, Control control)
    {
        double top = control.TranslatePoint(new global::Avalonia.Point(0, 0), root)!.Value.Y;
        return (top, top + control.Bounds.Height);
    }

    [AvaloniaFact]
    public void TheCombosAndTheGear_SpanTheSameRowAsTheModeButtons()
    {
        var (view, window, vm) = Show();
        try
        {
            var scatterButton = view.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("toolbar-btn"));
            var expected = Extent(view, scatterButton);

            foreach (Control control in new Control[]
            {
                ById<ComboBox>(view, "Scatter_XMetricCombo"),
                ById<ComboBox>(view, "Scatter_YMetricCombo"),
                ById<Button>(view, "Scatter_RegisterMetricsButton"),
            })
            {
                var actual = Extent(view, control);
                Assert.Equal(expected.Top, actual.Top, 0.5);
                Assert.Equal(expected.Bottom, actual.Bottom, 0.5);
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void TheHeatmapOffsetBox_SpansTheSameRowAsTheModeButtons()
    {
        var (view, window, vm) = Show();
        try
        {
            vm.SetHeatmapModeCommand.Execute(null);
            Layout(window);
            var scatterButton = view.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("toolbar-btn"));
            var expected = Extent(view, scatterButton);

            var actual = Extent(view, view.GetVisualDescendants().OfType<NumericUpDown>().Single());

            Assert.Equal(expected.Top, actual.Top, 0.5);
            Assert.Equal(expected.Bottom, actual.Bottom, 0.5);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void TheGearButton_FollowsTheYCombo_IsSquare_AndUsesTheSecondaryButtonTheme()
    {
        var (view, window, vm) = Show();
        try
        {
            var y = ById<ComboBox>(view, "Scatter_YMetricCombo");
            var gear = ById<Button>(view, "Scatter_RegisterMetricsButton");
            var panel = Assert.IsType<StackPanel>(y.Parent);

            Assert.Same(panel, gear.Parent);
            Assert.Equal(panel.Children.IndexOf(y) + 1, panel.Children.IndexOf(gear));
            Assert.Equal(32, gear.Bounds.Width, 0.5);
            Assert.Equal(32, gear.Bounds.Height, 0.5);
            Assert.True(window.TryFindResource("SecondaryButtonStyle", window.ActualThemeVariant, out object? secondary));
            Assert.Same(secondary, gear.Theme);
            Assert.True(window.TryFindResource("Brush.Button.Text", window.ActualThemeVariant, out object? buttonText));
            Assert.Equal(((global::Avalonia.Media.ISolidColorBrush)buttonText!).Color, ((global::Avalonia.Media.ISolidColorBrush)gear.Foreground!).Color);
            Assert.Same(vm.OpenMetricRegistrationCommand, gear.Command);
            Assert.False(string.IsNullOrWhiteSpace(ToolTip.GetTip(gear)?.ToString()));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task TheGearButton_OpensTheRegistrationDialog_WhenThereIsADialogService()
    {
        var dialog = new CountingDialog();
        var (view, window, vm) = Show(dialog: dialog);
        try
        {
            var gear = ById<Button>(view, "Scatter_RegisterMetricsButton");
            Assert.True(gear.IsEffectivelyEnabled);

            await vm.OpenMetricRegistrationCommand.ExecuteAsync(null);

            Assert.Equal(1, dialog.OpenCount);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void TheGearButton_IsDisabled_WithoutADialogService()
    {
        var (view, window, _) = Show(dialog: null);
        try
        {
            Assert.False(ById<Button>(view, "Scatter_RegisterMetricsButton").IsEffectivelyEnabled);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void TheTabOrder_IsModes_Metrics_Gear_Toggles_Checks_HeatmapOffset()
    {
        var (view, window, vm) = Show();
        try
        {
            var buttons = view.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("toolbar-btn")).ToList();
            var toggles = view.GetVisualDescendants().OfType<ToggleButton>().Where(t => t.Classes.Contains("toolbar-btn")).ToList();
            var checks = view.GetVisualDescendants().OfType<CheckBox>().ToList();
            vm.SetHeatmapModeCommand.Execute(null);
            Layout(window);
            var offset = view.GetVisualDescendants().OfType<NumericUpDown>().Single();

            int[] order =
            {
                buttons[0].TabIndex, buttons[1].TabIndex,
                ById<ComboBox>(view, "Scatter_XMetricCombo").TabIndex, ById<ComboBox>(view, "Scatter_YMetricCombo").TabIndex,
                ById<Button>(view, "Scatter_RegisterMetricsButton").TabIndex,
                toggles[0].TabIndex, toggles[1].TabIndex,
                checks[0].TabIndex, checks[1].TabIndex, checks[2].TabIndex, checks[3].TabIndex,
                offset.TabIndex,
            };
            Assert.Equal(Enumerable.Range(1, 12).ToArray(), order);
        }
        finally { window.Close(); }
    }

    [Fact]
    public void TheMarkup_ShowsMetricsWithAnEllipsis_AndTheFullLabelAsATooltip()
    {
        string axaml = ViewMarkup();

        Assert.Equal(2, Regex.Matches(axaml, @"SelectedItem=""\{Binding Selected[XY]Metric\}"" Width=""\{StaticResource ScatterMetricComboWidth\}""").Count);
        Assert.Equal(2, Regex.Matches(axaml, @"ToolTip\.Tip=""\{Binding Selected[XY]Metric\.Label\}""").Count);
        Assert.Equal(2, Regex.Matches(axaml, @"<TextBlock Text=""\{Binding Label\}"" TextTrimming=""CharacterEllipsis""").Count);
    }

    // ===== Log toggles of a fixed axis =====

    [AvaloniaFact]
    public async Task AFixedRangeAxis_DisablesItsLogToggle_WithTheReasonAsATooltip()
    {
        var evaluator = new StubScatterMetricEvaluator { FixedRangeOf = label => label == "Metric A" ? new AxisRange(0, 100) : null };
        var (view, window, vm) = Show(evaluator: evaluator);
        try
        {
            if (vm.PendingUpdateTask != null) await vm.PendingUpdateTask;
            Layout(window);
            var toggles = view.GetVisualDescendants().OfType<ToggleButton>().Where(t => t.Classes.Contains("toolbar-btn")).ToList();
            var (xLog, yLog) = (toggles[0], toggles[1]);

            Assert.False(xLog.IsEnabled);
            Assert.True(ToolTip.GetShowOnDisabled(xLog));
            Assert.Equal("Scatter_Tip_FixedAxisLog", ToolTip.GetTip(xLog));
            Assert.True(yLog.IsEnabled);
            Assert.Null(ToolTip.GetTip(yLog));
            Assert.Equal(new AxisRange(0, 100), vm.Dataset!.XFixedRange);
        }
        finally { window.Close(); }
    }

    // ===== Empty state =====

    [AvaloniaFact]
    public void WithoutRegisteredMetrics_TheGuidanceShowsInScatterMode_AndNotInHeatmapMode()
    {
        var (view, window, vm) = Show(metricCount: 0);
        try
        {
            var message = ById<TextBlock>(view, "Scatter_EmptyState");
            Assert.True(message.IsVisible);
            Assert.False(string.IsNullOrWhiteSpace(message.Text));
            Assert.Empty(ById<ComboBox>(view, "Scatter_XMetricCombo").Items);

            vm.SetHeatmapModeCommand.Execute(null);
            Layout(window);
            Assert.False(message.IsVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void WithRegisteredMetrics_TheGuidanceIsHidden()
    {
        var (view, window, _) = Show();
        try
        {
            Assert.False(ById<TextBlock>(view, "Scatter_EmptyState").IsVisible);
        }
        finally { window.Close(); }
    }
}
