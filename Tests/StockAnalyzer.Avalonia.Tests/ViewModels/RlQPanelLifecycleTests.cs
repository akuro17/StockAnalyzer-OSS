using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using SkiaSharp;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.Views.Chart;
using StockAnalyzer.Avalonia.Views.Chart.Renderers;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

public sealed partial class RlUiTests
{
    private const int PanelWidth = 600, PanelHeight = 300, ResizedPanelWidth = 400, RepeatedPaintCount = 5;
    private const float PanelBaseFontSize = 14, PanelHelperFontSize = 12;
    private static void PaintFrame(Window window) { window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }

    [AvaloniaFact]
    public async Task M04_PanelSurvivesRepeatedPaintResizePresentationChangeAndDetachReattach()
    {
        using var chart = Chart(new ImmediateData());
        var panel = new RlQPanel { ViewModel = chart, BaseFontSize = PanelBaseFontSize, HelperFontSize = PanelHelperFontSize, TextBrush = Brushes.Gray };
        var window = new Window { Content = panel, Width = PanelWidth, Height = PanelHeight };
        window.Show();
        try
        {
            PaintFrame(window);                                    // null presentation: nothing is submitted
            await chart.ApplyRlReplayAsync(Snapshot(0, 1, -1), StockAnalyzer.Core.Interfaces.NullLocalizationService.Instance);
            for (int i = 0; i < RepeatedPaintCount; i++) PaintFrame(window);        // repeated paint of the same scene
            window.Width = ResizedPanelWidth; PaintFrame(window);                // resize
            await chart.ApplyRlReplayAsync(Snapshot(-1, 0.5f, 2), StockAnalyzer.Core.Interfaces.NullLocalizationService.Instance);
            PaintFrame(window);                                    // presentation change
            panel.TextBrush = Brushes.White; PaintFrame(window);   // theme brush change
            window.Content = null; PaintFrame(window);             // detach disposes the renderer while earlier frames may still be queued
            panel.TextBrush = Brushes.Gray;                        // detached property changes are deferred to reattach and create no renderer
            window.Content = panel; PaintFrame(window);            // reattach rebuilds from the same view model
            Assert.NotNull(chart.RlPresentation);
            Assert.False(string.IsNullOrEmpty(global::Avalonia.Automation.AutomationProperties.GetName(panel)));
        }
        finally { window.Close(); }
    }

    [Fact]
    public void M04_DrawAfterRendererDisposalIsANoOpSoQueuedOperationsCannotTouchNativeResources()
    {
        var presentation = RlPresentation.From(Snapshot(-0.01f, 0.2f, -0.2f), StockAnalyzer.Core.Interfaces.NullLocalizationService.Instance);
        var area = new global::Avalonia.Rect(0, 0, ResizedPanelWidth, (double)RlContract.PanelHeight);
        byte[] Draw(bool dispose)
        {
            using var bitmap = new SKBitmap(ResizedPanelWidth, (int)RlContract.PanelHeight); using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.White);
            var renderer = new RlQRenderer(); renderer.PreparePanel(presentation, PanelBaseFontSize, PanelHelperFontSize);
            if (dispose) renderer.Dispose();
            renderer.DrawPanel(canvas, area, presentation, PanelBaseFontSize, PanelHelperFontSize, SKColors.Black, SKColors.Gray, SKColors.Green, SKColors.Red);
            renderer.Dispose(); renderer.Dispose(); // exactly-once native disposal tolerates repeated calls
            return bitmap.Bytes;
        }
        Assert.Contains(Draw(dispose: false), b => b != 0xFF);       // control: a live renderer paints
        Assert.All(Draw(dispose: true), b => Assert.Equal(0xFF, b)); // a disposed renderer leaves the canvas untouched and does not throw
    }
}
