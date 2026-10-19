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
using StockAnalyzer.Avalonia.ViewModels.TickerList;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Models.Watchlist;
using StockAnalyzer.Core.Services;
using CommunityToolkit.Mvvm.Messaging;
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

[Collection("LocalizationSharedState")]
public sealed class TrainingWizardRealStoreIntegrationTests
{
    private static readonly string[] AllSymbols = { "AAA", "BBB", "CCC", "DDD", "EEE" };

    private static WatchlistProfile Profile(string name, bool portfolio, params string[] tickers)
        => new(Guid.NewGuid(), name, IndicatorColor.Gray, portfolio, tickers.Select(t => new WatchlistItem(t, DateTimeOffset.UtcNow)).ToList());

    private static IEnumerable<TrainingSourceTreeItem> Flatten(IEnumerable<TrainingSourceTreeItem> items)
    {
        foreach (var item in items)
        {
            yield return item;
            foreach (var child in Flatten(item.Children)) yield return child;
        }
    }

    private static TrainingSourceTreeItem FindRow(TrainingWizardViewModel wizard, string name)
        => Flatten(wizard.SourceTree).Single(i => i.Name == name);

    private static async Task<(TrainingWizardViewModel Wizard, List<TrainingJobConfig> Started, TickerListViewModel Store)> CreateHarness(Action<TickerListViewModel>? setupStore = null)
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
        market.Setup(p => p.GetAvailableTickersAsync()).Returns(Task.FromResult<IReadOnlyList<string>>(AllSymbols));

        var wlManager = new Mock<IWatchlistManager>();
        var core = Profile("Core", false, "AAA", "BBB");
        var growth = Profile("Growth", false, "CCC");
        var held = Profile("Held", true, "DDD", "AAA");
        wlManager.Setup(w => w.GetAllProfiles()).Returns(new List<WatchlistProfile> { core, growth, held });

        var dispatcher = new StockAnalyzer.Avalonia.Tests.Services.SynchronousDispatcherService();
        var messenger = new WeakReferenceMessenger();
        var python = new Mock<IPythonService>();
        var portfolioMgr = new Mock<IPortfolioManager>();
        var dialog = new Mock<IDialogService>();
        var tickerImport = new Mock<ITickerImportService>();

        var store = new TickerListViewModel(
            market.Object,
            python.Object,
            messenger,
            dispatcher,
            wlManager.Object,
            portfolioMgr.Object,
            dialog.Object,
            tickerImport.Object);

        setupStore?.Invoke(store);

        var wizard = new TrainingWizardViewModel(orchestrator: orchestrator.Object, marketDataProvider: market.Object, tickerStore: store);
        for (int i = 0; i < 200 && !wizard.HasSourceTree; i++) await Task.Delay(10);
        Assert.True(wizard.HasSourceTree);

        return (wizard, started, store);
    }

    [Fact]
    public async Task FilterNode_ResolvesFilteredSymbols_AndTrainsOnThemOnly()
    {
        var (wizard, started, store) = await CreateHarness(st =>
        {
            var filterSettings = new FilterSettings
            {
                Id = Guid.NewGuid(),
                Name = "Only AAA",
                Rules = new List<FilterRule>
                {
                    new() { Field = "Symbol", Operator = "Equals", Value = "AAA" }
                }
            };
            var filterNode = new FilterNode(filterSettings);
            var allTickersNode = st.Groups.First(g => g.Id == TickerListViewModel.AllTickersId);
            allTickersNode.Children!.Add(filterNode);
        });

        using (wizard)
        using (store)
        {
            var filterRow = FindRow(wizard, "Only AAA");
            FindRow(wizard, "All Tickers").IsSelected = false;
            filterRow.IsSelected = true;

            await wizard.StartTrainingCommand.ExecuteAsync(null);
            var result = Assert.Single(started).Symbols.OrderBy(s => s, StringComparer.Ordinal).ToArray();
            Assert.Equal(new[] { "AAA" }, result);
        }
    }

    [Fact]
    public async Task CategoryNode_ResolvesUnionOfChildWatchlists_AndTrainsOnThem()
    {
        var (wizard, started, store) = await CreateHarness();

        using (wizard)
        using (store)
        {
            FindRow(wizard, "All Tickers").IsSelected = false;
            var watchlistsRow = FindRow(wizard, LocalizationManager.Instance["TickerList_Category_Watchlists"]);
            watchlistsRow.IsSelected = true;

            await wizard.StartTrainingCommand.ExecuteAsync(null);
            var result = Assert.Single(started).Symbols.OrderBy(s => s, StringComparer.Ordinal).ToArray();
            Assert.Equal(new[] { "AAA", "BBB", "CCC" }, result);
        }
    }

    [Fact]
    public async Task CustomCategoryNode_And_ListFolderNode_ResolveUnderlyingTickers_AndTrainOnThem()
    {
        var (wizard, started, store) = await CreateHarness(st =>
        {
            var folderSettings = new TickerListFolderSettings
            {
                Id = Guid.NewGuid(),
                Name = "Folder1",
                ChildTickers = new List<string> { "BBB", "DDD" }
            };
            var parentSettings = new TickerParentCategorySettings
            {
                Id = Guid.NewGuid(),
                Name = "Custom Group",
                Folders = new List<TickerListFolderSettings> { folderSettings }
            };
            var customCategory = new CustomCategoryNode(parentSettings);
            var folderNode = new TickerListFolderNode(folderSettings);
            customCategory.Children!.Add(folderNode);
            st.Groups.Add(customCategory);
        });

        using (wizard)
        using (store)
        {
            // 1. Verify FolderNode
            FindRow(wizard, "All Tickers").IsSelected = false;
            var folderRow = FindRow(wizard, "Folder1");
            folderRow.IsSelected = true;

            await wizard.StartTrainingCommand.ExecuteAsync(null);
            var result1 = Assert.Single(started).Symbols.OrderBy(s => s, StringComparer.Ordinal).ToArray();
            Assert.Equal(new[] { "BBB", "DDD" }, result1);

            // 2. Verify CustomCategoryNode
            started.Clear();
            folderRow.IsSelected = false;
            var customGroupRow = FindRow(wizard, "Custom Group");
            customGroupRow.IsSelected = true;

            await wizard.StartTrainingCommand.ExecuteAsync(null);
            var result2 = Assert.Single(started).Symbols.OrderBy(s => s, StringComparer.Ordinal).ToArray();
            Assert.Equal(new[] { "BBB", "DDD" }, result2);
        }
    }
}
