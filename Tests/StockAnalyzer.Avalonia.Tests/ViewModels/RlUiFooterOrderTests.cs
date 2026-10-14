using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Tests.TestHelpers;
using StockAnalyzer.Avalonia.Tests.Views;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.Views;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

public sealed partial class RlUiTests
{
    /// <summary>SA_UI_SUBWINDOW_STANDARD 1.1: Train (primary) comes first, then Pause, then Cancel.</summary>
    [AvaloniaFact]
    public async Task FooterFollowsTheSubWindowStandard_TrainIsPrimaryAndLeftOfPauseAndCancel()
    {
        LocalizationManager.Instance.Initialize("en");
        using var chart = Chart(new ImmediateData());
        var training = new DelayedTraining();
        var directionGen = new RlGeneration(new string('a', 64), "SPY", "source", RlContract.ContractHash, "evaluation", 0m, 0m, new(), DateTimeOffset.UtcNow);
        var registry = new EmptyRegistry { Capabilities = new(false, false, true), Generations = new[] { directionGen } };
        using var vm = new RlTrainingDialogViewModel(training, registry, new UnavailableQ(), new FakeBacktestDefaultSettingsManager(), chart, new LocalizationService());
        await vm.InitializeAsync();
        vm.Symbol = "SPY"; vm.StartDate = "2020-01-01"; vm.EndDateExclusive = "2026-01-01";
        vm.UseContinuousPolicy = true; vm.SelectedDirectionOption = vm.DirectionOptions.First();
        var window = new RlTrainingWindow { DataContext = vm };
        try
        {
            window.Show();
            var trainTask = vm.TrainCommand.ExecuteAsync(null);
            await training.Started.Task;
            Assert.True(vm.ShowPause && vm.ShowCancel);
            FooterOrderStandardTests.Pump();
            var buttons = window.GetLogicalDescendants().OfType<Button>().ToArray();
            FooterOrderStandardTests.AssertPrimaryFirst(window,
                buttons.Single(b => b.Command == vm.TrainCommand),
                buttons.Single(b => b.Command == vm.PauseCommand),
                buttons.Single(b => b.Command == vm.CancelCommand));
            vm.CancelCommand.Execute(null);
            training.Result.SetCanceled();
            await trainTask;
        }
        finally { window.Close(); }
    }

    /// <summary>The footer holds the blocking reason and the state labels in the cell between the status and the buttons: at the default and at the minimum width neither pushes the other, or the buttons, out of the window.</summary>
    [AvaloniaFact]
    public async Task FooterKeepsTheStateLabelsInsideTheWindowLeftOfTheButtons_AndTheBlockingReasonStaysVisible_AtEveryWidth()
    {
        foreach (string language in new[] { "en", "ja" })
        foreach (double width in new[] { RlUiLayout.WindowWidth, RlUiLayout.MinimumWidth })
        {
            LocalizationManager.Instance.Initialize(language);
            using var chart = Chart(new ImmediateData());
            using var vm = new RlTrainingDialogViewModel(new DelayedTraining(), new EmptyRegistry(), new UnavailableQ(), new FakeBacktestDefaultSettingsManager(), chart, new LocalizationService());
            await vm.InitializeAsync();
            vm.SmokeOnly = true; vm.UseShortHistory = true; vm.UseContinuousPolicy = true;                      // the most labels at once (the symbol stays empty: a blocking reason is shown)
            var window = new RlTrainingWindow { DataContext = vm, Width = width };
            try
            {
                window.Show();
                FooterOrderStandardTests.Pump();
                string where = $"{language}, width {width}";
                Assert.True(vm.ModeBadges.Count >= 2 && vm.HasStartBlocker, where + ": labels and a reason are shown together");
                var all = window.GetVisualDescendants().ToList();
                var badges = all.OfType<ItemsControl>().Single(c => global::Avalonia.Automation.AutomationProperties.GetAutomationId(c) == RlUiConstants.ModeBadges);
                var reason = all.OfType<TextBlock>().Single(t => global::Avalonia.Automation.AutomationProperties.GetAutomationId(t) == RlUiConstants.StartBlocker);
                var train = all.OfType<Button>().Single(b => b.Command == vm.TrainCommand);
                var footer = train.GetVisualAncestors().OfType<Border>().First(b => b.BorderThickness.Top > 0 && b.Bounds.Height > 0 && b.Bounds.Bottom >= window.Bounds.Height - 1);
                double X(Visual v) => v.TranslatePoint(new Point(0, 0), window)!.Value.X;
                double Y(Visual v, Visual relativeTo) => v.TranslatePoint(new Point(0, 0), relativeTo)!.Value.Y;
                const double tolerance = 0.5;
                double right = X(badges) + badges.Bounds.Width;
                Assert.True(right <= window.Bounds.Width + tolerance, $"{where}: the labels end at {right}, inside the window");
                Assert.True(right <= X(train) + tolerance, $"{where}: the labels end at {right}, left of the Train button at {X(train)}");
                FooterLayoutAssert.ReasonIsShownInFull(reason, where);
                foreach (var label in badges.GetVisualDescendants().OfType<Border>().Where(b => b.Child is TextBlock))
                    Assert.True(Y(label, footer) + label.Bounds.Height <= footer.Bounds.Height + tolerance, $"{where}: every label is inside the footer height");
                Assert.True(Y(reason, footer) + reason.Bounds.Height <= footer.Bounds.Height + tolerance, $"{where}: the reason is inside the footer height");
            }
            finally { window.Close(); }
        }
    }
}
