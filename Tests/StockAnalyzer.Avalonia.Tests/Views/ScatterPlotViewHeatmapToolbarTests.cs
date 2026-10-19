using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Tests.Services;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.Views;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models.Analysis;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Analysis;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views;

public sealed class ScatterPlotViewHeatmapToolbarTests
{
    private sealed class HeadlessLocalizationService : ILocalizationService
    {
        public string GetString(string key) => key;
    }

    [AvaloniaFact]
    public void HeatmapToolbar_PresentsDateBadgeAndRightAlignedNumericUpDown_WithoutColorSchemeButton()
    {
        var messenger = new WeakReferenceMessenger();
        var vm = new ScatterPlotViewModel(
            messenger,
            marketDataProvider: null,
            new HeadlessLocalizationService(),
            NullLogger<ScatterPlotViewModel>.Instance,
            new SynchronousDispatcherService(),
            metricProvider: SyntheticScatterMetricProvider.Instance,
            heatmapProvider: SyntheticHeatmapReturnProvider.Instance
        );

        var view = new ScatterPlotView { DataContext = vm };
        var window = new Window
        {
            Width = 900,
            Height = 600,
            Content = view
        };

        window.Show();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        try
        {
            // Switch to heatmap mode
            vm.SetHeatmapModeCommand.Execute(null);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            // 1. Confirm NO button contains or references color scheme
            var buttons = view.GetVisualDescendants().OfType<Button>().ToList();
            Assert.DoesNotContain(buttons, b => b.CommandParameter?.ToString()?.Contains("ColorScheme", System.StringComparison.OrdinalIgnoreCase) == true);
            Assert.DoesNotContain(buttons, b => b.Content?.ToString()?.Contains("Colors", System.StringComparison.OrdinalIgnoreCase) == true);

            // 2. Locate Date Badge TextBlock
            var dateBadge = view.GetVisualDescendants()
                .OfType<TextBlock>()
                .FirstOrDefault(tb => tb.Text == vm.HeatmapTargetDateText);
            Assert.NotNull(dateBadge);

            // 3. Locate NumericUpDown
            var numUpDown = view.GetVisualDescendants().OfType<NumericUpDown>().Single();
            Assert.True(numUpDown.Classes.Contains("toolbar-num"));
            Assert.Equal(0, numUpDown.Minimum);
            Assert.Equal(9999, numUpDown.Maximum);
            Assert.Equal(0, numUpDown.Value);

            // Test 4-digit input alignment and containment
            vm.HeatmapOffset = 9999;
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            Assert.Equal(9999, numUpDown.Value);

            var textBox = numUpDown.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(tb => tb.Name == "PART_TextBox");
            Assert.NotNull(textBox);
            Assert.Equal(TextAlignment.Right, textBox.TextAlignment);
        }
        finally
        {
            window.Close();
        }
    }
}
