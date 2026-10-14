using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StockAnalyzer.Avalonia.Tests.TestHelpers;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Avalonia.Views.Dialogs;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services;
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

    [AvaloniaFact]
    public void TheFooterShowsTheStartBlockerReasonAndTheStartModeLabel()
    {
        LocalizationManager.Instance.Initialize("en");
        var vm = new TrainingWizardViewModel();
        var window = new TrainingWizardWindow { DataContext = vm };
        try
        {
            window.Show();
            Pump();
            var all = window.GetVisualDescendants().ToList();
            var reason = all.OfType<TextBlock>().Single(t => global::Avalonia.Automation.AutomationProperties.GetAutomationId(t) == "TrainingWizard_StartBlocker");
            var badges = all.OfType<ItemsControl>().Single(c => global::Avalonia.Automation.AutomationProperties.GetAutomationId(c) == "TrainingWizard_ModeBadges");
            var start = FooterButton(window, vm.StartTrainingCommand);
            vm.Horizon = 0;
            Pump();
            Assert.True(reason.IsEffectivelyVisible);
            Assert.Equal(LocalizationManager.Instance["TrainingWizard_Start_Horizon"], reason.Text);
            var footer = reason.GetVisualAncestors().OfType<Border>().First(b => Grid.GetRow(b) == 3);   // the footer row (strip row is 2)
            Assert.Contains(start, footer.GetVisualDescendants());
            Assert.False(badges.IsEffectivelyVisible);                                                   // default start mode: no label
            vm.SelectedInitializationMode = TrainingInitializationMode.Resume;
            Pump();
            Assert.True(badges.IsEffectivelyVisible);
            Assert.Equal(new[] { LocalizationManager.Instance["Enum_TrainingInitializationMode_Resume"] }, badges.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
            vm.Horizon = 5; vm.SelectedInitializationMode = TrainingInitializationMode.Fresh;
            Pump();
            Assert.False(badges.IsEffectivelyVisible);
            vm.UseCpcv = true;                                                                           // CPCV changes what is scored: labelled while it is on
            Pump();
            Assert.True(badges.IsEffectivelyVisible);
            Assert.Equal(new[] { LocalizationManager.Instance["TrainingWizard_Badge_Cpcv"] }, badges.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
            vm.UseCpcv = false;
            Pump();
            Assert.False(badges.IsEffectivelyVisible);
        }
        finally { window.Close(); vm.Dispose(); }
    }

    /// <summary>
    /// The footer holds, from the left: the deploy buttons, the blocking reason and the state labels, then Start / Cancel / Close. At the default and at the minimum
    /// width, in both languages, with every label on, before and after a run, nothing on the left may reach the buttons or leave the window, every element stays inside the
    /// footer (which grows past 64 px only when it must), and the reason keeps room to be read. Before a run the footer is still exactly 64 px.
    /// </summary>
    /// <summary>A reason that wraps is still readable at this width at least; the footer never grows past this height (about two lines of labels beside a two-line reason).</summary>
    private const double MinimumReasonWidth = 100, MaximumFooterHeight = 140;

    [AvaloniaFact]
    public void TheFooterIsExactly64pxInTheDefaultState_AtBothWidths()
    {
        foreach (double width in new[] { 1180d, 1000d })
        {
            LocalizationManager.Instance.Initialize("en");
            var vm = new TrainingWizardViewModel();
            var window = new TrainingWizardWindow { DataContext = vm, Width = width };
            try
            {
                window.Show();
                Pump();
                var footer = FooterButton(window, vm.StartTrainingCommand).GetVisualAncestors().OfType<Border>().First(b => Grid.GetRow(b) == 3);
                Assert.Equal(TrainingWizardUiLayout.FooterHeight, footer.Bounds.Height);
            }
            finally { window.Close(); vm.Dispose(); }
        }
    }

    [AvaloniaFact]
    public void TheFooterKeepsTheDeployButtonsTheButtonsTheReasonAndTheFourLabelsApart_AtEveryWidth_BeforeAndAfterARun()
    {
        var problems = new List<string>();
        foreach (string language in new[] { "en", "ja" })
        foreach (double width in new[] { 1180d, 1000d })
        foreach (bool afterRun in new[] { false, true })
        {
            LocalizationManager.Instance.Initialize(language);
            var vm = new TrainingWizardViewModel();
            var window = new TrainingWizardWindow { DataContext = vm, Width = width };
            try
            {
                window.Show();
                Pump();
                string runId = "20260101-123456789-abcdef12";
                if (afterRun)
                {
                    vm.LastResult = new TrainingRunResult { RunId = runId, Success = true };
                    vm.RegisteredEnsemble = new EnsembleRegistration(TrainingEnsembleMethod.Stacking, new EnsembleSpec(System.Array.Empty<EnsembleMember>()), System.Array.Empty<ModelGenerationManifest>());
                }
                vm.SelectedInitializationMode = TrainingInitializationMode.Resume; vm.UseCpcv = true; vm.UsePretraining = true; vm.SelectedEnsembleMethod = TrainingEnsembleMethod.Stacking;   // the most labels at once
                vm.Horizon = 0;                                                                                                                                                         // a blocking reason
                Pump(); Pump();
                string where = $"{language}, width {width}, {(afterRun ? "after" : "before")} a run";
                Assert.Equal(4, vm.ModeBadges.Count);
                Assert.True(vm.HasStartBlocker, where + ": a reason is shown together with the labels");
                var all = window.GetVisualDescendants().ToList();
                var reason = all.OfType<TextBlock>().Single(t => global::Avalonia.Automation.AutomationProperties.GetAutomationId(t) == "TrainingWizard_StartBlocker");
                var badges = all.OfType<ItemsControl>().Single(c => global::Avalonia.Automation.AutomationProperties.GetAutomationId(c) == "TrainingWizard_ModeBadges");
                var start = FooterButton(window, vm.StartTrainingCommand);
                var footer = start.GetVisualAncestors().OfType<Border>().First(b => Grid.GetRow(b) == 3);
                double X(Visual v) => v.TranslatePoint(new Point(0, 0), window)!.Value.X;
                double Y(Visual v) => v.TranslatePoint(new Point(0, 0), footer)!.Value.Y;
                const double tolerance = 0.5;
                double buttonsLeft = X(start.GetVisualParent()!);
                var left = new List<(string Name, Control Control)> { ("reason", reason) };
                left.AddRange(badges.GetVisualDescendants().OfType<Border>().Where(b => b.Child is TextBlock).Select(b => ("label", (Control)b)));
                if (afterRun)
                {
                    left.Add(("deploy", FooterButton(window, vm.DeployToModelsCommand)));
                    left.Add(("ensemble", FooterButton(window, vm.UseAsEnsembleCommand)));
                }
                foreach (var (name, control) in left)
                {
                    double right = X(control) + control.Bounds.Width;
                    if (right > buttonsLeft + tolerance) problems.Add($"{where}: the {name} ends at {right}, right of the buttons at {buttonsLeft}");
                    if (right > window.Bounds.Width + tolerance) problems.Add($"{where}: the {name} is outside the window");
                    if (Y(control) + control.Bounds.Height > footer.Bounds.Height + tolerance)
                        problems.Add($"{where}: the {name} leaves the footer height (y {Y(control):F0} + {control.Bounds.Height:F0} of {footer.Bounds.Height:F0}; label cell {badges.Bounds.Width:F0} wide)");
                }
                if (reason.Bounds.Width < MinimumReasonWidth) problems.Add($"{where}: the reason is only {reason.Bounds.Width:F0} px wide");
                if (footer.Bounds.Height > MaximumFooterHeight) problems.Add($"{where}: the footer grew to {footer.Bounds.Height:F0} px");
                if (!afterRun && Math.Abs(footer.Bounds.Height - TrainingWizardUiLayout.FooterHeight) > tolerance) problems.Add($"{where}: the footer is {footer.Bounds.Height:F0} px before a run");
            }
            finally { window.Close(); vm.Dispose(); }
        }
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [AvaloniaFact]
    public void TheRunIdIsShownUnderTheResultFileInTheBody_NotInTheFooter()
    {
        LocalizationManager.Instance.Initialize("en");
        var vm = new TrainingWizardViewModel();
        var window = new TrainingWizardWindow { DataContext = vm };
        try
        {
            window.Show();
            Pump();
            const string runId = "20260101-123456789-abcdef12";
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBox>(), t => t.Text == runId);
            vm.LastResult = new TrainingRunResult { RunId = runId, Success = true };
            vm.CheckpointManifestPath = @"C:/runs/manifest.json";
            Pump(); Pump();
            var all = window.GetVisualDescendants().ToList();
            var box = all.OfType<TextBox>().Single(t => global::Avalonia.Automation.AutomationProperties.GetAutomationId(t) == "TrainingWizard_RunId");
            var result = all.OfType<TextBox>().Single(t => t.Text == vm.CheckpointManifestPath);
            Assert.Equal(runId, box.Text);
            Assert.True(box.IsReadOnly && box.IsEffectivelyVisible);
            Assert.Equal(LocalizationManager.Instance["TrainingWizard_RunId"], all.OfType<TextBlock>().Single(t => t.Text == "Run ID").Text);
            Assert.True(box.TranslatePoint(new Point(0, 0), window)!.Value.Y > result.TranslatePoint(new Point(0, 0), window)!.Value.Y, "the run id is below the training result file");
            var footer = FooterButton(window, vm.StartTrainingCommand).GetVisualAncestors().OfType<Border>().First(b => Grid.GetRow(b) == 3);
            Assert.DoesNotContain(footer.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == runId);
            Assert.DoesNotContain(footer.GetVisualDescendants(), v => ReferenceEquals(v, box));
        }
        finally { window.Close(); vm.Dispose(); }
    }

    private static void Pump()
    {
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }
}
