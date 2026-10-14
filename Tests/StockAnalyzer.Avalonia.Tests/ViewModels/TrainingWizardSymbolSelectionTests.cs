using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Tests.TestHelpers;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Models.Watchlist;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

/// <summary>The symbols a run trains on are exactly those of the ticked rows of the ticker-list tree: never the whole population when "All tickers" is not ticked.</summary>
[Collection("LocalizationSharedState")]
public sealed class TrainingWizardSymbolSelectionTests
{
    private static readonly string[] Population = { "AAA", "BBB", "CCC", "DDD", "EEE" };

    private static WatchlistProfile Profile(string name, bool portfolio, params string[] tickers)
        => new(Guid.NewGuid(), name, IndicatorColor.Gray, portfolio, tickers.Select(t => new WatchlistItem(t, DateTimeOffset.UtcNow)).ToList());

    private static async Task<(TrainingWizardViewModel Wizard, List<TrainingJobConfig> Started)> Create()
    {
        var started = new List<TrainingJobConfig>();
        var orchestrator = new Mock<ITrainingOrchestrator>();
        orchestrator.Setup(o => o.StartTrainingAsync(It.IsAny<TrainingJobConfig>(), It.IsAny<IProgress<TrainingProgress>>(), It.IsAny<CancellationToken>()))
            .Returns((TrainingJobConfig config, IProgress<TrainingProgress> _, CancellationToken _) =>
            {
                started.Add(config);
                return Task.FromResult(new TrainingRunResult { RunId = "run", Success = true });
            });
        var market = new Mock<IMarketDataProvider>();
        market.Setup(p => p.GetAvailableTickersAsync()).ReturnsAsync(Population.ToList());
        var store = FakeTickerStateStore.FromProfiles(new[]
        {
            Profile("Core", false, "AAA", "BBB"), Profile("Growth", false, "CCC"), Profile("Held", true, "DDD", "AAA"),
        });
        var wizard = new TrainingWizardViewModel(orchestrator: orchestrator.Object, marketDataProvider: market.Object, tickerStore: store);
        for (int i = 0; i < 200 && !wizard.HasSourceTree; i++) await Task.Delay(10);
        Assert.True(wizard.HasSourceTree);
        return (wizard, started);
    }

    private static TrainingSourceTreeItem Row(TrainingWizardViewModel wizard, string name) => wizard.SourceTree.Single(i => i.Name == name);

    private static async Task<string[]> Run(TrainingWizardViewModel wizard, List<TrainingJobConfig> started)
    {
        started.Clear();
        await wizard.StartTrainingCommand.ExecuteAsync(null);
        return Assert.Single(started).Symbols.OrderBy(s => s, StringComparer.Ordinal).ToArray();
    }

    [Fact]
    public async Task OnlyAllTickersIsTickedAtFirst_AndItTrainsOnThePopulation()
    {
        var (wizard, started) = await Create();
        using (wizard)
        {
            Assert.Equal(new[] { "All Tickers" }, wizard.SourceTree.Where(i => i.IsSelected).Select(i => i.Name).ToArray());
            Assert.Equal(Population, await Run(wizard, started));
        }
    }

    [Fact]
    public async Task TickingAListUnticksAllTickers_AndTrainsOnlyOnThatList()
    {
        var (wizard, started) = await Create();
        using (wizard)
        {
            Row(wizard, "Core").IsSelected = true;
            Assert.False(Row(wizard, "All Tickers").IsSelected);
            Assert.Equal(new[] { "AAA", "BBB" }, await Run(wizard, started));
        }
    }

    [Fact]
    public async Task UntickingAllTickersAndTickingTwoListsTrainsOnTheirUnionOnly()
    {
        var (wizard, started) = await Create();
        using (wizard)
        {
            Row(wizard, "All Tickers").IsSelected = false;
            Row(wizard, "Growth").IsSelected = true;
            Row(wizard, "Held").IsSelected = true;
            Assert.Equal(new[] { "AAA", "CCC", "DDD" }, await Run(wizard, started));
            Assert.DoesNotContain("BBB", started.Single().Symbols);
            Assert.DoesNotContain("EEE", started.Single().Symbols);
        }
    }

    [Fact]
    public async Task NothingTicked_StartIsDisabledWithTheReason_AndNothingIsStarted()
    {
        var (wizard, started) = await Create();
        using (wizard)
        {
            Row(wizard, "All Tickers").IsSelected = false;
            Assert.False(wizard.StartTrainingCommand.CanExecute(null));
            Assert.Equal(LocalizationManager.Instance["TrainingWizard_Start_NoTickers"], wizard.StartBlockerDisplay);
            Assert.Empty(started);
        }
    }

    [Fact]
    public async Task TickingAllTickersAgainAfterAListTrainsOnThePopulation_AndTheCountFollowsTheTicks()
    {
        var (wizard, started) = await Create();
        using (wizard)
        {
            Row(wizard, "Growth").IsSelected = true;
            Assert.Contains("1", wizard.SelectedSymbolSummary);
            Row(wizard, "All Tickers").IsSelected = true;                       // ticking All tickers itself changes nothing else
            Assert.True(Row(wizard, "Growth").IsSelected);
            Assert.Equal(Population, await Run(wizard, started));
        }
    }
}
