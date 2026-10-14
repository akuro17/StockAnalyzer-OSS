using System.Linq;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Tests.TestHelpers;
using StockAnalyzer.Avalonia.Tests.Views;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.Views;
using StockAnalyzer.Avalonia.Views.Controls;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

public sealed partial class RlUiTests
{
    /// <summary>S2: the progress and the message are in the run-status strip above the footer, outside the scrolled body; the message keeps its automation id.</summary>
    [AvaloniaFact]
    public async Task RunStatusStrip_IsAboveTheFooterOutsideTheScrollAndCarriesTheMessageAndTheProgress()
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
            FooterOrderStandardTests.Pump();
            var strip = window.GetVisualDescendants().OfType<RunStatusStrip>().Single();
            var train = window.GetVisualDescendants().OfType<Button>().Single(b => b.Command == vm.TrainCommand);
            Assert.False(strip.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "StripRoot").IsVisible);
            Assert.Equal(0d, strip.Bounds.Height);

            var trainTask = vm.TrainCommand.ExecuteAsync(null);
            await training.Started.Task;
            FooterOrderStandardTests.Pump();
            Assert.True(vm.ShowProgressBar);
            Assert.True(strip.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "ProgressRow").IsVisible);
            RunStatusStripTests.AssertPlacedBetweenBodyAndFooter(window, strip, train);
            var message = window.GetVisualDescendants().OfType<TextBlock>().Single(t => AutomationProperties.GetAutomationId(t) == RlUiConstants.Message);
            Assert.Same(strip, message.GetVisualAncestors().OfType<RunStatusStrip>().Single());

            vm.CancelCommand.Execute(null);
            training.Result.SetCanceled();
            await trainTask;
            FooterOrderStandardTests.Pump();
            Assert.Equal(vm.Message, message.Text);
        }
        finally { window.Close(); }
    }
}
