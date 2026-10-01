using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Tests.Services;
using StockAnalyzer.Avalonia.Tests.TestHelpers;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Models.Templates;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

/// <summary>sa_implement (Column Customization selection scope, Phase 3/4): TickerListViewModel follows
/// ITickersSettingsManager.ColumnSelectionScope, both at construction and when it changes later.</summary>
[Collection("MessengerSharedState")]
public class TickerListViewModelColumnScopeTests
{
    private static (TickerListViewModel Vm, InMemoryColumnTemplateService Service, ColumnTemplate Template) Create(FakeTickersSettingsManager? manager)
    {
        var watchlists = new Mock<IWatchlistManager>();
        watchlists.Setup(w => w.GetAllProfiles()).Returns(new List<StockAnalyzer.Core.Models.Watchlist.WatchlistProfile>());
        var service = new InMemoryColumnTemplateService();
        var template = new ColumnTemplate { Id = Guid.NewGuid(), Name = "One", ColumnNames = new[] { "Symbol", "Volume" } };
        service.Templates.Add(template);

        var vm = new TickerListViewModel(
            marketDataProvider: new Mock<IMarketDataProvider>().Object,
            pythonService: null!,
            messenger: WeakReferenceMessenger.Default,
            dispatcherService: new SynchronousDispatcherService(),
            watchlistManager: watchlists.Object,
            portfolioManager: null!,
            dialogService: null!,
            tickerImportService: null!,
            chartSettingsManager: new MockChartSettingsManager(),
            logger: NullLogger<TickerListViewModel>.Instance,
            templateService: service,
            tickersSettingsManager: manager);
        return (vm, service, template);
    }

    private static async Task SelectTemplateAsync(TickerListViewModel vm, ColumnTemplate template)
    {
        // The sidebar list is not selected in this harness; give the selector a current list explicitly.
        vm.ColumnTemplateSelector.OnActiveListChanged(Guid.NewGuid());
        await vm.ColumnTemplateSelector.ReloadEntriesAsync();
        vm.ColumnTemplateSelector.SelectedEntry = vm.ColumnTemplateSelector.Entries.Single(e => e.Id == template.Id);
    }

    [Fact]
    public async Task WithoutSettingsManager_SelectionStaysPerList()
    {
        var (vm, _, template) = Create(null);

        await SelectTemplateAsync(vm, template);

        Assert.Contains(template.Id, vm.ColumnTemplateSelector.ExportSelectionsByList().Values);
        Assert.Equal(Guid.Empty, vm.ColumnTemplateSelector.ExportSharedSelection());
        vm.Dispose();
    }

    [Fact]
    public async Task SharedScopeAtConstruction_StoresTheSelectionInTheSharedStore()
    {
        var manager = new FakeTickersSettingsManager();
        manager.SetColumnSelectionScope(TickerColumnSelectionScope.Shared);
        var (vm, _, template) = Create(manager);

        await SelectTemplateAsync(vm, template);

        Assert.Equal(template.Id, vm.ColumnTemplateSelector.ExportSharedSelection());
        Assert.Empty(vm.ColumnTemplateSelector.ExportSelectionsByList());
        vm.Dispose();
    }

    [Fact]
    public async Task ScopeChangedLater_SwitchesTheActiveStore()
    {
        var manager = new FakeTickersSettingsManager();
        var (vm, _, template) = Create(manager);
        await SelectTemplateAsync(vm, template);
        Assert.Contains(template.Id, vm.ColumnTemplateSelector.ExportSelectionsByList().Values);

        manager.SetColumnSelectionScope(TickerColumnSelectionScope.Shared);

        // Shared store is empty: Active Columns is applied and the per-list store is kept as it was.
        Assert.Same(vm.ColumnTemplateSelector.ActiveColumnsEntry, vm.ColumnTemplateSelector.SelectedEntry);
        Assert.Contains(template.Id, vm.ColumnTemplateSelector.ExportSelectionsByList().Values);

        manager.SetColumnSelectionScope(TickerColumnSelectionScope.PerList);

        Assert.Equal(template.Id, vm.ColumnTemplateSelector.SelectedEntry?.Id);
        vm.Dispose();
    }

    [Fact]
    public async Task AfterDispose_ScopeChangesAreIgnored()
    {
        var manager = new FakeTickersSettingsManager();
        var (vm, _, template) = Create(manager);
        await SelectTemplateAsync(vm, template);
        vm.Dispose();

        manager.SetColumnSelectionScope(TickerColumnSelectionScope.Shared);

        Assert.Equal(template.Id, vm.ColumnTemplateSelector.SelectedEntry?.Id);
    }
}
