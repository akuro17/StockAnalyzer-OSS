using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Avalonia.Views.Dialogs;
using StockAnalyzer.Core.Models.Training;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views.Dialogs;

/// <summary>T13: the extended-learning controls are present in the real wizard window and follow the selected method.</summary>
public class TrainingWizardExtendedLearningViewTests
{
    private static Control Find(TrainingWizardWindow window, string automationId) =>
        window.GetVisualDescendants().OfType<Control>().Single(c =>
            global::Avalonia.Automation.AutomationProperties.GetAutomationId(c) == automationId);

    private static void Pump()
    {
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void CpcvControls_RenderAndShowTheCombinationCountOnlyWhileRequested()
    {
        var vm = new TrainingWizardViewModel();
        var window = new TrainingWizardWindow { DataContext = vm };
        try
        {
            window.Show();
            Pump();
            Assert.NotNull(Find(window, "TrainingWizard_CpcvCheck"));
            Assert.False(Find(window, "TrainingWizard_CpcvGroups").IsEffectivelyVisible);
            Assert.False(Find(window, "TrainingWizard_CpcvTestGroups").IsEffectivelyVisible);

            vm.UseCpcv = true;
            Pump();
            Assert.True(Find(window, "TrainingWizard_CpcvGroups").IsEffectivelyVisible);
            Assert.True(Find(window, "TrainingWizard_CpcvTestGroups").IsEffectivelyVisible);
            var count = Assert.IsType<TextBlock>(Find(window, "TrainingWizard_CpcvCount"));
            Assert.True(count.IsEffectivelyVisible);
            Assert.Equal(vm.CpcvCountText, count.Text);

            vm.CpcvGroups = 10;
            vm.CpcvTestGroups = 3; // 120 combinations: over the cap
            Pump();
            Assert.False(count.IsEffectivelyVisible);
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(),
                t => t.IsEffectivelyVisible && t.Text == vm.ExtendedLearningValidationMessage);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ExtendedLearningControls_RenderAndShowOnlyTheFieldsOfTheSelectedMethod()
    {
        var vm = new TrainingWizardViewModel();
        var window = new TrainingWizardWindow { DataContext = vm };
        try
        {
            window.Show();
            Pump();

            var combo = Assert.IsType<ComboBox>(Find(window, "TrainingWizard_EnsembleMethodCombo"));
            Assert.Equal(TrainingWizardViewModel.AvailableEnsembleMethods.Count, combo.ItemCount);
            Assert.Equal(TrainingEnsembleMethod.None, combo.SelectedItem);
            Assert.NotNull(Find(window, "TrainingWizard_PretrainingCheck"));
            Assert.False(Find(window, "TrainingWizard_EnsembleMembers").IsEffectivelyVisible);
            Assert.False(Find(window, "TrainingWizard_StackingBases").IsEffectivelyVisible);

            vm.SelectedEnsembleMethod = TrainingEnsembleMethod.Bagging;
            Pump();
            Assert.True(Find(window, "TrainingWizard_EnsembleMembers").IsEffectivelyVisible);
            Assert.False(Find(window, "TrainingWizard_StackingBases").IsEffectivelyVisible);

            vm.SelectedEnsembleMethod = TrainingEnsembleMethod.Stacking;
            Pump();
            Assert.False(Find(window, "TrainingWizard_EnsembleMembers").IsEffectivelyVisible);
            Assert.True(Find(window, "TrainingWizard_StackingBases").IsEffectivelyVisible);

            // the validation message appears next to the controls once the choice cannot run
            var messages = window.GetVisualDescendants().OfType<TextBlock>()
                .Where(t => t.Text == vm.ExtendedLearningValidationMessage && t.Text is not null).ToArray();
            Assert.Single(messages);
            Assert.True(messages[0].IsEffectivelyVisible);
        }
        finally
        {
            window.Close();
        }
    }
}
