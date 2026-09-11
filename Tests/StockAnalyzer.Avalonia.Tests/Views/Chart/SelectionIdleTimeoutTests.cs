using System;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.Views.Chart;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views.Chart;

/// <summary>
/// Selected drawing objects' anchor handles must auto-hide (deselect) after
/// SelectionIdleTimeoutMs of no pointer activity. SelectionIdleTimeoutMs is
/// exposed as an internal, test-overridable property so these tests don't wait the real timeout.
/// </summary>
[Collection("DrawingThemeContext State")]
public class SelectionIdleTimeoutTests : IDisposable
{
    public void Dispose()
    {
        // DrawingThemeContext is process-wide static state; restore the default so other tests
        // in this collection aren't affected by whichever value this test last set.
        DrawingThemeContext.SetControlPointHideTimeoutMsForTesting(
            StockAnalyzer.Core.Models.Settings.ChartSettingsConstants.DefaultControlPointHideTimeoutSeconds * 1000);
    }

    [AvaloniaFact] // needs a running headless dispatcher: the timer callback marshals via Dispatcher.UIThread.Post
    public async Task NoActivityAfterSelection_AutoDeselectsAfterTimeout()
    {
        var controller = new ChartInteractionController { SelectionIdleTimeoutMs = 50 };
        var viewModel = new ChartViewModel();
        var trendLine = new TrendLineObject(
            new ChartPoint(new DateTime(2024, 1, 1), 10m),
            new ChartPoint(new DateTime(2024, 1, 2), 20m));
        viewModel.ObjectManager.AddObject(trendLine);
        viewModel.ObjectManager.SelectObject(trendLine.Id);
        Assert.NotNull(viewModel.ObjectManager.SelectedObject);

        bool timeoutFired = false;
        controller.OnSelectionIdleTimeout = () => timeoutFired = true;

        controller.ArmOrResetSelectionIdleTimer(viewModel.ObjectManager);

        await Task.Delay(300); // well past the 50ms timeout

        Assert.Null(viewModel.ObjectManager.SelectedObject);
        Assert.True(timeoutFired);
    }

    [Fact]
    public async Task RepeatedActivityBeforeTimeout_KeepsSelectionAlive()
    {
        var controller = new ChartInteractionController { SelectionIdleTimeoutMs = 150 };
        var viewModel = new ChartViewModel();
        var trendLine = new TrendLineObject(
            new ChartPoint(new DateTime(2024, 1, 1), 10m),
            new ChartPoint(new DateTime(2024, 1, 2), 20m));
        viewModel.ObjectManager.AddObject(trendLine);
        viewModel.ObjectManager.SelectObject(trendLine.Id);

        // Reset the timer 3 times, each within the 150ms window, spanning longer than the
        // timeout would allow if activity weren't resetting it.
        for (int i = 0; i < 3; i++)
        {
            controller.ArmOrResetSelectionIdleTimer(viewModel.ObjectManager);
            await Task.Delay(80);
        }

        Assert.NotNull(viewModel.ObjectManager.SelectedObject);
    }

    [Fact]
    public void NothingSelected_ArmOrResetSelectionIdleTimer_IsNoOp()
    {
        var controller = new ChartInteractionController { SelectionIdleTimeoutMs = 50 };
        var viewModel = new ChartViewModel();

        // Must not throw when nothing is selected (the common case for most pointer moves).
        controller.ArmOrResetSelectionIdleTimer(viewModel.ObjectManager);

        Assert.Null(viewModel.ObjectManager.SelectedObject);
    }

    [AvaloniaFact] // needs a running headless dispatcher: the timer callback marshals via Dispatcher.UIThread.Post
    public async Task SettingsChangedAfterConstruction_TakesEffectOnAlreadyOpenChart()
    {
        // Regression test: SelectionIdleTimeoutMs previously captured
        // DrawingThemeContext.ControlPointHideTimeoutMs only once, at controller-construction
        // time, so a Settings -> Chart -> Drawing change never took effect on an already-open
        // chart tab. It must now be read live on every ArmOrResetSelectionIdleTimer call.
        DrawingThemeContext.SetControlPointHideTimeoutMsForTesting(5000); // simulates the app default at tab-open time

        // No SelectionIdleTimeoutMs override here: this exercises the real (non-test-seam) getter.
        var controller = new ChartInteractionController();
        var viewModel = new ChartViewModel();
        var trendLine = new TrendLineObject(
            new ChartPoint(new DateTime(2024, 1, 1), 10m),
            new ChartPoint(new DateTime(2024, 1, 2), 20m));
        viewModel.ObjectManager.AddObject(trendLine);
        viewModel.ObjectManager.SelectObject(trendLine.Id);

        // Simulates the user saving a new, shorter Control Point Hide Timeout setting while
        // this chart tab (and its ChartInteractionController) is already open.
        DrawingThemeContext.SetControlPointHideTimeoutMsForTesting(50);

        controller.ArmOrResetSelectionIdleTimer(viewModel.ObjectManager);

        await Task.Delay(300); // well past the newly-set 50ms timeout, well short of the original 5000ms

        Assert.Null(viewModel.ObjectManager.SelectedObject);
    }
}
