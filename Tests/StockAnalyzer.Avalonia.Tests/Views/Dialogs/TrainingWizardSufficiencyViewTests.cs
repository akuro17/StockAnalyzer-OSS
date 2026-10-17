using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Avalonia.Views.Dialogs;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views.Dialogs;

/// <summary>The two data-sufficiency warnings exist in the real wizard window, below the period status line, and show nothing until the view model has something to say.</summary>
public class TrainingWizardSufficiencyViewTests
{
    private static TextBlock Find(TrainingWizardWindow window, string automationId) =>
        window.GetVisualDescendants().OfType<TextBlock>().Single(c =>
            global::Avalonia.Automation.AutomationProperties.GetAutomationId(c) == automationId);

    [AvaloniaFact]
    public void BothWarnings_ExistWithTheirAutomationIds_AndAreHiddenWhileEmpty()
    {
        var vm = new TrainingWizardViewModel();
        var window = new TrainingWizardWindow { DataContext = vm };
        try
        {
            window.Show();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            var requirement = Find(window, "TrainingWizard_RequirementWarning");
            var limit = Find(window, "TrainingWizard_SampleLimitWarning");
            var period = Find(window, "TrainingWizard_PeriodStatus");
            Assert.False(requirement.IsEffectivelyVisible);
            Assert.False(limit.IsEffectivelyVisible);
            Assert.Equal(vm.RequirementWarningDisplay, requirement.Text ?? "");
            Assert.Equal(vm.SampleLimitWarningDisplay, limit.Text ?? "");
            Assert.Same(period.Parent, requirement.Parent);                         // all three share the data-period column
            var siblings = ((Panel)period.Parent!).Children;
            Assert.Equal(siblings.IndexOf(period) + 1, siblings.IndexOf(requirement));
            Assert.Equal(siblings.IndexOf(requirement) + 1, siblings.IndexOf(limit));
        }
        finally
        {
            window.Close();
        }
    }
}
