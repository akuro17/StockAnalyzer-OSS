using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Avalonia.Views.Dialogs;
using StockAnalyzer.Core.Models.Analysis;
using StockAnalyzer.Core.Models.Settings;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views.Dialogs;

public sealed class ScatterAnalysisSettingsViewTests
{
    [AvaloniaFact]
    public void ViewLoads_WithScrollViewerRoot_ColorPickers_NumericUpDowns_AndComboBox()
    {
        var manager = new MockChartSettingsManager();
        using var vm = new ScatterAnalysisSettingsViewModel(manager, NullLogger<ScatterAnalysisSettingsViewModel>.Instance);
        var view = Assert.IsType<ScatterAnalysisSettingsView>(new ViewLocator().Build(vm));
        var window = new Window { Content = view, Width = 800, Height = 700 };
        view.DataContext = vm;

        try
        {
            window.Show();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            // Root of view content is ScrollViewer
            var scrollViewer = Assert.IsType<ScrollViewer>(view.Content);
            Assert.Equal(ScrollBarVisibility.Auto, scrollViewer.VerticalScrollBarVisibility);
            Assert.Equal(ScrollBarVisibility.Disabled, scrollViewer.HorizontalScrollBarVisibility);
            Assert.False(scrollViewer.AllowAutoHide);

            // Four ColorPickers for Up, Neutral, Down, TextColor
            var colorPickers = view.GetVisualDescendants().OfType<ColorPicker>().ToArray();
            Assert.Equal(4, colorPickers.Length);

            // Four NumericUpDowns for Threshold, Multiplier, StepCount, FontSize
            var numericInputs = view.GetVisualDescendants().OfType<NumericUpDown>().ToArray();
            Assert.Equal(4, numericInputs.Length);

            // Threshold NumericUpDown
            Assert.Equal((decimal)ChartSettingsConstants.MinScatterHeatmapThresholdPercent, numericInputs[0].Minimum);
            Assert.Equal((decimal)ChartSettingsConstants.MaxScatterHeatmapThresholdPercent, numericInputs[0].Maximum);
            Assert.Equal((decimal)ChartSettingsConstants.ScatterHeatmapThresholdPercentStep, numericInputs[0].Increment);
            Assert.Equal("0.0", numericInputs[0].FormatString);

            // Multiplier NumericUpDown
            Assert.Equal((decimal)ChartSettingsConstants.MinScatterHeatmapMultiplier, numericInputs[1].Minimum);
            Assert.Equal((decimal)ChartSettingsConstants.MaxScatterHeatmapMultiplier, numericInputs[1].Maximum);
            Assert.Equal((decimal)ChartSettingsConstants.ScatterHeatmapMultiplierStep, numericInputs[1].Increment);
            Assert.Equal("0.0", numericInputs[1].FormatString);

            // StepCount NumericUpDown
            Assert.Equal((decimal)ChartSettingsConstants.MinScatterHeatmapStepCount, numericInputs[2].Minimum);
            Assert.Equal((decimal)ChartSettingsConstants.MaxScatterHeatmapStepCount, numericInputs[2].Maximum);
            Assert.Equal(1m, numericInputs[2].Increment);
            Assert.Equal("0", numericInputs[2].FormatString);

            // FontSize NumericUpDown
            Assert.Equal((decimal)ChartSettingsConstants.MinScatterHeatmapFontSize, numericInputs[3].Minimum);
            Assert.Equal((decimal)ChartSettingsConstants.MaxScatterHeatmapFontSize, numericInputs[3].Maximum);
            Assert.Equal((decimal)ChartSettingsConstants.ScatterHeatmapFontSizeStep, numericInputs[3].Increment);
            Assert.Equal("0", numericInputs[3].FormatString);

            // TextAlignment ComboBox
            var comboBox = view.GetVisualDescendants().OfType<ComboBox>().Single();
            Assert.NotNull(comboBox.ItemsSource);
            Assert.Equal(3, comboBox.ItemsSource.Cast<object>().Count());
            Assert.Equal(HeatmapTextAlignment.Center, comboBox.SelectedItem);
        }
        finally
        {
            window.Close();
        }
    }
}
