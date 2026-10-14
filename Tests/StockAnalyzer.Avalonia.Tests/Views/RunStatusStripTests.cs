using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Avalonia.Views.Controls;
using StockAnalyzer.Avalonia.Views.Dialogs;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views;

/// <summary>
/// The shared run-status strip (S2): the control's own visibility and bindings, and its placement in the Training Wizard window. The RL and Portfolio RL
/// placements are checked next to the fixtures of those windows with <see cref="AssertPlacedBetweenBodyAndFooter"/>.
/// </summary>
[Collection("LocalizationSharedState")]
public sealed class RunStatusStripTests
{
    private const double LayoutTolerance = 0.5;

    private static T Part<T>(Visual root, string name) where T : Control
        => root.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    /// <summary>The strip is outside every ScrollViewer, in the grid row directly above the footer row, and its bottom edge is the footer's top edge.</summary>
    public static void AssertPlacedBetweenBodyAndFooter(Window window, RunStatusStrip strip, Button footerButton)
    {
        Assert.Empty(strip.GetVisualAncestors().OfType<ScrollViewer>());
        var grid = Assert.IsType<Grid>(strip.GetVisualParent());
        var footer = footerButton.GetVisualAncestors().OfType<Control>().First(a => ReferenceEquals(a.GetVisualParent(), grid));
        Assert.Equal(Grid.GetRow(strip) + 1, Grid.GetRow(footer));
        double stripBottom = strip.TranslatePoint(new Point(0, strip.Bounds.Height), window)!.Value.Y;
        double footerTop = footer.TranslatePoint(new Point(0, 0), window)!.Value.Y;
        Assert.InRange(footerTop - stripBottom, -LayoutTolerance, LayoutTolerance);
    }

    [AvaloniaFact]
    public void StripIsHiddenUntilThereIsProgressOrAMessage()
    {
        var strip = new RunStatusStrip();
        var window = new Window { Content = strip };
        try
        {
            window.Show();
            FooterOrderStandardTests.Pump();
            Assert.False(Part<Border>(strip, "StripRoot").IsVisible);

            strip.Message = "Check the training settings and try again.";
            Assert.True(Part<Border>(strip, "StripRoot").IsVisible);
            Assert.True(Part<TextBlock>(strip, "MessageLabel").IsVisible);
            Assert.False(Part<Grid>(strip, "ProgressRow").IsVisible);

            strip.Message = null;
            Assert.False(Part<Border>(strip, "StripRoot").IsVisible);

            strip.ShowProgress = true;
            Assert.True(Part<Border>(strip, "StripRoot").IsVisible);
            Assert.True(Part<Grid>(strip, "ProgressRow").IsVisible);
            Assert.False(Part<TextBlock>(strip, "MessageLabel").IsVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void PercentLabelFollowsPercentTextThenPercentAndIsHiddenWhenIndeterminate()
    {
        var strip = new RunStatusStrip { ShowProgress = true, Percent = 42.4 };
        var window = new Window { Content = strip };
        try
        {
            window.Show();
            FooterOrderStandardTests.Pump();
            var label = Part<TextBlock>(strip, "PercentLabel");
            var bar = strip.GetVisualDescendants().OfType<ProgressBar>().Single();
            Assert.Equal("42%", label.Text);
            Assert.True(label.IsVisible);
            Assert.Equal(42.4, bar.Value);

            strip.PercentText = "42.4%";
            Assert.Equal("42.4%", label.Text);

            strip.PercentText = null;
            strip.IsIndeterminate = true;
            Assert.False(label.IsVisible);
            Assert.True(bar.IsIndeterminate);

            strip.PercentText = "";
            Assert.False(label.IsVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void PhaseAndMessageAreShownSeparatelyAndTheMessageKeepsItsAutomationId()
    {
        var strip = new RunStatusStrip { ShowProgress = true, Phase = "Training", Message = "Starting", MessageAutomationId = "Some_Message" };
        var window = new Window { Content = strip };
        try
        {
            window.Show();
            FooterOrderStandardTests.Pump();
            var phase = Part<TextBlock>(strip, "PhaseLabel");
            var message = Part<TextBlock>(strip, "MessageLabel");
            Assert.True(phase.IsVisible);
            Assert.Equal("Training", phase.Text);
            Assert.Equal("Starting", message.Text);
            Assert.Equal("Some_Message", AutomationProperties.GetAutomationId(message));
            Assert.Equal(RunStatusStripLayout.MessageMaxLines, message.MaxLines);

            strip.Phase = "";
            Assert.False(phase.IsVisible);

            // a phase text alone (a failure detail while no run is active) is not lost
            strip.ShowProgress = false;
            strip.Message = null;
            Assert.False(Part<Border>(strip, "StripRoot").IsVisible);
            strip.Phase = "list unavailable";
            Assert.True(Part<Border>(strip, "StripRoot").IsVisible);
            Assert.True(phase.IsVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void TrainingWizard_StripIsAboveTheFooterOutsideTheScrollAndShowsTheStatusMessage()
    {
        LocalizationManager.Instance.Initialize("en");
        var vm = new TrainingWizardViewModel();
        var window = new TrainingWizardWindow { DataContext = vm };
        try
        {
            window.Show();
            FooterOrderStandardTests.Pump();
            var strip = window.GetVisualDescendants().OfType<RunStatusStrip>().Single();
            var start = window.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.Command, vm.StartTrainingCommand));
            Assert.False(Part<Border>(strip, "StripRoot").IsVisible, "idle: nothing to show");
            Assert.Equal(0d, strip.Bounds.Height);

            // what a rejected start writes (TrainingWizardViewModel.StartTrainingAsync, config.Validate() failure)
            vm.StatusMessage = "The end date must be after the start date.";
            FooterOrderStandardTests.Pump();
            Assert.True(Part<Border>(strip, "StripRoot").IsVisible);
            Assert.Equal(vm.StatusMessage, Part<TextBlock>(strip, "MessageLabel").Text);
            AssertPlacedBetweenBodyAndFooter(window, strip, start);

            vm.IsTraining = true;
            vm.ProgressPercent = 30;
            vm.CurrentStage = "Training";
            FooterOrderStandardTests.Pump();
            Assert.True(Part<Grid>(strip, "ProgressRow").IsVisible);
            Assert.Equal("30%", Part<TextBlock>(strip, "PercentLabel").Text);
            Assert.Equal(vm.CurrentStageDisplay, Part<TextBlock>(strip, "PhaseLabel").Text);
            AssertPlacedBetweenBodyAndFooter(window, strip, start);
        }
        finally { window.Close(); vm.Dispose(); }
    }
}
