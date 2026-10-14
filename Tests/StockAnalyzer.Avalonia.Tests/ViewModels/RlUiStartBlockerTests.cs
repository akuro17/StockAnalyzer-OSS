using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Tests.TestHelpers;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

public sealed partial class RlUiTests
{
    private const string BlockerStart = "2020-01-01", BlockerEnd = "2026-01-01";

    private async Task<RlTrainingDialogViewModel> StartableRlViewModel(ChartViewModel chart)
    {
        var direction = new RlGeneration(new string('a', 64), "SPY", "source", RlContract.ContractHash, "evaluation", 0m, 0m, new(), DateTimeOffset.UtcNow);
        var registry = new EmptyRegistry { Capabilities = new(true, true, true), RlArtifactPolicy = new(), Generations = new[] { direction } };
        var vm = new RlTrainingDialogViewModel(new DelayedTraining(), registry, new UnavailableQ(), new FakeBacktestDefaultSettingsManager(), chart, new LocalizationService());
        await vm.InitializeAsync();
        return vm;
    }

    /// <summary>
    /// Characterization of <c>CanTrain</c> (S4a, written before the start-blocker change and kept after it): the whole cross product of the inputs that can make a
    /// start impossible, against an independent statement of the rule. The table must hold before and after the change.
    /// </summary>
    [Fact]
    public async Task CanTrain_CrossProductMatchesTheStatedRule_AndTheBlockerIsNoneExactlyWhenTheStartIsPossible()
    {
        LocalizationManager.Instance.Initialize("en");
        using var chart = Chart(new ImmediateData());
        using var vm = await StartableRlViewModel(chart);
        int rows = 0;
        foreach (string symbol in new[] { "", "SPY" })
        foreach (string start in new[] { "", BlockerStart })
        foreach (string end in new[] { "", BlockerEnd })
        foreach (bool swapped in new[] { false, true })               // both dates valid but start >= end
        foreach (bool costsInvalid in new[] { false, true })
        foreach (bool continuous in new[] { false, true })
        foreach (bool directionChosen in new[] { false, true })
        foreach (bool retentionInvalid in new[] { false, true })
        {
            if (swapped && (start.Length == 0 || end.Length == 0)) continue;
            vm.Symbol = symbol;
            vm.StartDate = swapped ? BlockerEnd : start; vm.EndDateExclusive = swapped ? BlockerStart : end;
            vm.SetEditorValidity(nameof(vm.InitialCapital), !costsInvalid);
            vm.UseContinuousPolicy = continuous;
            vm.SelectedDirectionOption = directionChosen ? vm.DirectionOptions.First() : null;
            vm.SetEditorValidity(nameof(vm.StorageQuotaGiB), !retentionInvalid);
            bool expected = symbol.Length > 0 && start.Length > 0 && end.Length > 0 && !swapped && !costsInvalid
                && (!continuous || directionChosen) && !retentionInvalid;
            string row = $"symbol='{symbol}' start='{start}' end='{end}' swapped={swapped} costsInvalid={costsInvalid} continuous={continuous} direction={directionChosen} retentionInvalid={retentionInvalid}";
            Assert.True(expected == vm.CanTrain, "CanTrain: " + row);
            Assert.True(expected == (vm.CurrentStartBlocker() == RlStartBlocker.None), "Blocker is None exactly when the start is possible: " + row);
            Assert.True(expected == !vm.HasStartBlocker, "HasStartBlocker: " + row);
            Assert.True(expected == (vm.StartBlockerDisplay.Length == 0), "StartBlockerDisplay: " + row);
            rows++;
        }
        Assert.True(rows > 100);
    }

    [Fact]
    public async Task StartBlocker_NamesTheFirstMissingInputInThePriorityOrder_AndDisappearsWhenItIsFixed()
    {
        LocalizationManager.Instance.Initialize("en");
        using var chart = Chart(new ImmediateData());
        using var vm = await StartableRlViewModel(chart);
        var text = LocalizationManager.Instance;

        vm.Symbol = ""; vm.StartDate = ""; vm.EndDateExclusive = "";
        Assert.Equal(RlStartBlocker.SymbolMissing, vm.CurrentStartBlocker());
        Assert.Equal(text.Get("Rl.Ui.Start.SymbolMissing"), vm.StartBlockerDisplay);
        vm.Symbol = "SPY";
        Assert.Equal(RlStartBlocker.StartDateMissing, vm.CurrentStartBlocker());
        Assert.Equal(text.Get("Rl.Ui.Start.StartDateMissing"), vm.StartBlockerDisplay);
        vm.StartDate = BlockerStart;
        Assert.Equal(RlStartBlocker.EndDateMissing, vm.CurrentStartBlocker());
        Assert.Equal(text.Get("Rl.Ui.Start.EndDateMissing"), vm.StartBlockerDisplay);
        vm.EndDateExclusive = BlockerStart;
        Assert.Equal(RlStartBlocker.DateOrderInvalid, vm.CurrentStartBlocker());
        Assert.Equal(text.Get("Rl.Ui.Guide.InvalidDateOrder"), vm.StartBlockerDisplay);
        vm.EndDateExclusive = BlockerEnd;
        Assert.Equal(RlStartBlocker.None, vm.CurrentStartBlocker());
        Assert.Equal("", vm.StartBlockerDisplay);
        Assert.False(vm.HasStartBlocker);

        vm.SetEditorValidity(nameof(vm.InitialCapital), false);
        Assert.Equal(RlStartBlocker.CostsInvalid, vm.CurrentStartBlocker());
        Assert.Equal(text.Get("Rl.Ui.Start.CostsInvalid"), vm.StartBlockerDisplay);
        vm.SetEditorValidity(nameof(vm.InitialCapital), true);

        vm.UseContinuousPolicy = true; vm.SelectedDirectionOption = null;
        Assert.Equal(RlStartBlocker.DirectionModelMissing, vm.CurrentStartBlocker());
        Assert.Equal(text.Get("Rl.Ui.Start.DirectionModelMissing"), vm.StartBlockerDisplay);
        vm.SelectedDirectionOption = vm.DirectionOptions.First();
        Assert.Equal(RlStartBlocker.None, vm.CurrentStartBlocker());
        vm.UseContinuousPolicy = false;

        vm.SetEditorValidity(nameof(vm.StorageQuotaGiB), false);
        Assert.Equal(RlStartBlocker.RetentionInvalid, vm.CurrentStartBlocker());
        Assert.Equal(text.Get("Rl.Ui.Start.RetentionInvalid"), vm.StartBlockerDisplay);
        vm.SetEditorValidity(nameof(vm.StorageQuotaGiB), true);
        Assert.Equal("", vm.StartBlockerDisplay);
        // priority: the symbol is named before a bad cost editor
        vm.Symbol = ""; vm.SetEditorValidity(nameof(vm.InitialCapital), false);
        Assert.Equal(RlStartBlocker.SymbolMissing, vm.CurrentStartBlocker());
    }

    [global::Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task Footer_ShowsTheReasonAndTheModeLabelsNextToTheStatus_AndTheDefaultStateShowsNothingMore()
    {
        LocalizationManager.Instance.Initialize("en");
        using var chart = Chart(new ImmediateData());
        using var vm = await StartableRlViewModel(chart);
        vm.Symbol = "SPY"; vm.StartDate = ""; vm.EndDateExclusive = "";
        var window = new StockAnalyzer.Avalonia.Views.RlTrainingWindow { DataContext = vm };
        try
        {
            window.Show();
            StockAnalyzer.Avalonia.Tests.Views.FooterOrderStandardTests.Pump();
            var all = global::Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).ToList();
            var reason = all.OfType<global::Avalonia.Controls.TextBlock>().Single(t => global::Avalonia.Automation.AutomationProperties.GetAutomationId(t) == StockAnalyzer.Avalonia.Common.RlUiConstants.StartBlocker);
            var badges = all.OfType<global::Avalonia.Controls.ItemsControl>().Single(c => global::Avalonia.Automation.AutomationProperties.GetAutomationId(c) == StockAnalyzer.Avalonia.Common.RlUiConstants.ModeBadges);
            var status = all.OfType<global::Avalonia.Controls.TextBlock>().Single(t => global::Avalonia.Automation.AutomationProperties.GetAutomationId(t) == StockAnalyzer.Avalonia.Common.RlUiConstants.Status);
            var train = all.OfType<global::Avalonia.Controls.Button>().Single(b => b.Command == vm.TrainCommand);
            Assert.True(reason.IsEffectivelyVisible);
            Assert.Equal(LocalizationManager.Instance.Get("Rl.Ui.Start.StartDateMissing"), reason.Text);
            // the line is inside the footer bar (the grid row of the Train button), to the right of the status and to the left of the buttons
            var footer = global::Avalonia.VisualTree.VisualExtensions.GetVisualAncestors(reason).OfType<global::Avalonia.Controls.Border>().First(b => global::Avalonia.Controls.Grid.GetRow(b) == 3);
            Assert.Contains(train, global::Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(footer));
            double X(global::Avalonia.Visual v) => v.TranslatePoint(new global::Avalonia.Point(0, 0), window)!.Value.X;
            Assert.True(X(status) < X(reason) && X(reason) < X(train));
            Assert.False(badges.IsEffectivelyVisible);                                           // default state: no label

            vm.StartDate = BlockerStart; vm.EndDateExclusive = BlockerEnd;
            StockAnalyzer.Avalonia.Tests.Views.FooterOrderStandardTests.Pump();
            Assert.False(reason.IsEffectivelyVisible);                                           // fixed: the line is gone

            vm.SmokeOnly = true;
            StockAnalyzer.Avalonia.Tests.Views.FooterOrderStandardTests.Pump();
            Assert.True(badges.IsEffectivelyVisible);
            Assert.Equal(new[] { "Trial run" }, global::Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<global::Avalonia.Controls.TextBlock>().Where(t => global::Avalonia.VisualTree.VisualExtensions.GetVisualAncestors(t).Contains(badges)).Select(t => t.Text));
            vm.UseShortHistory = true;
            StockAnalyzer.Avalonia.Tests.Views.FooterOrderStandardTests.Pump();
            Assert.Equal(new[] { "Trial run", "Short history" }, vm.ModeBadges);
            vm.UseContinuousPolicy = true;                                                       // the short history is not offered with the continuous policy
            Assert.Equal(new[] { "Trial run", "Position size and stop" }, vm.ModeBadges);          // the continuous policy changes what is trained, so it is labelled
            vm.UseContinuousPolicy = false; vm.SmokeOnly = false; vm.UseShortHistory = false;
            Assert.Empty(vm.ModeBadges);
            Assert.False(vm.HasModeBadges);
        }
        finally { window.Close(); }
    }

    [Fact]
    public async Task StartBlockerDisplay_IsEmptyWhileAnOperationRuns()
    {
        LocalizationManager.Instance.Initialize("en");
        using var chart = Chart(new ImmediateData());
        var training = new DelayedTraining();
        var direction = new RlGeneration(new string('a', 64), "SPY", "source", RlContract.ContractHash, "evaluation", 0m, 0m, new(), DateTimeOffset.UtcNow);
        var registry = new EmptyRegistry { Capabilities = new(false, false, true), Generations = new[] { direction } };
        using var vm = new RlTrainingDialogViewModel(training, registry, new UnavailableQ(), new FakeBacktestDefaultSettingsManager(), chart, new LocalizationService());
        await vm.InitializeAsync();
        vm.Symbol = "SPY"; vm.StartDate = BlockerStart; vm.EndDateExclusive = BlockerEnd;
        var run = vm.TrainCommand.ExecuteAsync(null);
        await training.Started.Task;
        vm.Symbol = "";                                           // an edit during the run would otherwise name the symbol
        Assert.False(vm.Editable);
        Assert.Equal("", vm.StartBlockerDisplay);
        Assert.False(vm.HasStartBlocker);
        vm.CancelCommand.Execute(null);
        training.Result.SetCanceled();
        await run;
    }
}
