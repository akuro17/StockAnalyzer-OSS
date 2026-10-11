using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Avalonia.Views.Dialogs;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views.Dialogs;

/// <summary>The wizard footer: "Start" and "Cancel" are two separate buttons, Cancel to the right of Start, both always shown.</summary>
[Collection("LocalizationSharedState")]
public sealed class TrainingWizardFooterTests
{
    private static Button FooterButton(TrainingWizardWindow window, object command) =>
        window.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.Command, command));

    [AvaloniaFact]
    public void StartAndCancelAreSeparateButtons_CancelRightOfStart_BothAlwaysShown_CancelOnlyEnabledWhileTraining()
    {
        LocalizationManager.Instance.Initialize("en");
        var vm = new TrainingWizardViewModel();
        var window = new TrainingWizardWindow { DataContext = vm };
        try
        {
            window.Show();
            Pump();
            var start = FooterButton(window, vm.StartTrainingCommand);
            var cancel = FooterButton(window, vm.CancelTrainingCommand);
            double X(Button b) => b.TranslatePoint(new Point(0, 0), window)!.Value.X;

            Assert.NotSame(start, cancel);
            Assert.Equal("Start", start.Content);
            Assert.Equal("Cancel", cancel.Content);
            Assert.True(start.IsVisible && cancel.IsVisible);
            Assert.True(X(start) < X(cancel), "Cancel sits to the right of Start");
            Assert.False(cancel.IsEffectivelyEnabled, "nothing to cancel before a run");

            vm.IsTraining = true;
            Pump();

            Assert.True(start.IsVisible && cancel.IsVisible, "the two buttons are not swapped while a run is active");
            Assert.True(cancel.IsEffectivelyEnabled);
            Assert.False(start.IsEffectivelyEnabled);
        }
        finally { window.Close(); vm.Dispose(); }
    }

    private static void Pump()
    {
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }
}
