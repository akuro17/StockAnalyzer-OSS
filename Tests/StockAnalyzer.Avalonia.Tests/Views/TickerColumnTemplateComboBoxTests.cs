using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Tests.Services;
using StockAnalyzer.Avalonia.Tests.TestHelpers;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Templates;
using StockAnalyzer.Core.Models.Watchlist;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views;

// Shares the static WeakReferenceMessenger.Default with other TickerListViewModel-hosting tests.
[Collection("MessengerSharedState")]
public class TickerColumnTemplateComboBoxTests
{
    private static void Pump()
    {
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void DeletedTemplate_AfterDropDownReload_ComboBoxShowsActiveColumns()
    {
        var service = new InMemoryColumnTemplateService();
        var template = new ColumnTemplate { Id = Guid.NewGuid(), Name = "Deleted Soon", ColumnNames = new[] { "Symbol", "Name", "Volume" } };
        service.Templates.Add(template);
        var watchlistManager = new Mock<IWatchlistManager>();
        watchlistManager.Setup(w => w.GetAllProfiles()).Returns(new List<WatchlistProfile>());

        var vm = new TickerListViewModel(
            marketDataProvider: null!,
            pythonService: null!,
            messenger: WeakReferenceMessenger.Default,
            dispatcherService: new SynchronousDispatcherService(),
            watchlistManager: watchlistManager.Object,
            portfolioManager: null!,
            dialogService: null!,
            tickerImportService: null!,
            chartSettingsManager: new MockChartSettingsManager(),
            logger: NullLogger<TickerListViewModel>.Instance,
            templateService: service);

        var view = new StockAnalyzer.Avalonia.Views.TickerListView { DataContext = vm };
        var window = new Window { Content = view, Width = 900, Height = 600 };

        try
        {
            window.Show();
            Pump();

            var selector = vm.ColumnTemplateSelector;
            var combo = view.FindControl<ComboBox>("ColumnTemplateComboBox");
            Assert.NotNull(combo);

            selector.SelectedEntry = selector.Entries.Single(e => e.Id == template.Id);
            Pump();
            Assert.Equal(template.Id, ((ColumnTemplate?)combo!.SelectedItem)?.Id);

            // The template is deleted elsewhere; opening the dropdown re-fetches the list.
            service.Templates.Clear();
            combo.IsDropDownOpen = true;
            Pump();

            Assert.Same(selector.ActiveColumnsEntry, selector.SelectedEntry);
            Assert.Same(selector.ActiveColumnsEntry, combo.SelectedItem);
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }
    }

    [AvaloniaFact]
    public void ListWhoseTemplateWasDeleted_ShowsActiveColumnsInComboBoxWithoutOpeningDropDown()
    {
        var service = new InMemoryColumnTemplateService();
        var template = new ColumnTemplate { Id = Guid.NewGuid(), Name = "Deleted Soon", ColumnNames = new[] { "Symbol", "Name", "Volume" } };
        service.Templates.Add(template);
        var watchlistManager = new Mock<IWatchlistManager>();
        watchlistManager.Setup(w => w.GetAllProfiles()).Returns(new List<WatchlistProfile>());
        var vm = new TickerListViewModel(null!, null!, WeakReferenceMessenger.Default, new SynchronousDispatcherService(), watchlistManager.Object, null!, null!, null!,
            new MockChartSettingsManager(), NullLogger<TickerListViewModel>.Instance, service);
        var view = new StockAnalyzer.Avalonia.Views.TickerListView { DataContext = vm };
        var window = new Window { Content = view, Width = 900, Height = 600 };

        try
        {
            window.Show();
            Pump();
            var selector = vm.ColumnTemplateSelector;
            var combo = view.FindControl<ComboBox>("ColumnTemplateComboBox")!;
            var listA = Guid.NewGuid();
            var listB = Guid.NewGuid();

            selector.OnActiveListChanged(listA);
            selector.SelectedEntry = selector.Entries.Single(e => e.Id == template.Id);
            Pump();
            selector.OnActiveListChanged(listB);
            Pump();

            // The template is deleted elsewhere while the dropdown list is stale (never re-opened).
            service.Templates.Clear();
            selector.OnActiveListChanged(listA);
            Pump();

            Assert.Same(selector.ActiveColumnsEntry, selector.SelectedEntry);
            Assert.Same(selector.ActiveColumnsEntry, combo.SelectedItem);
            Assert.DoesNotContain(listA, selector.ExportSelectionsByList().Keys);
            Assert.DoesNotContain(selector.Entries, e => e.Id == template.Id);
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }
    }

    [AvaloniaFact]
    public void ClosingColumnCustomizationDialogAfterDeletingTheSelectedTemplate_ComboBoxShowsActiveColumns()
    {
        var service = new InMemoryColumnTemplateService();
        var template = new ColumnTemplate { Id = Guid.NewGuid(), Name = "Deleted In Dialog", ColumnNames = new[] { "Symbol", "Name", "Volume" } };
        service.Templates.Add(template);
        var watchlistManager = new Mock<IWatchlistManager>();
        watchlistManager.Setup(w => w.GetAllProfiles()).Returns(new List<WatchlistProfile>());
        var dialogService = new Mock<IDialogService>();
        // The user deletes the template inside the dialog and closes it without changing any column.
        dialogService
            .Setup(d => d.ShowColumnChooserDialogAsync(It.IsAny<IEnumerable<WatchlistColumnMetadata>>(), It.IsAny<IEnumerable<string>>(), It.IsAny<Action<List<string>>?>()))
            .Returns<IEnumerable<WatchlistColumnMetadata>, IEnumerable<string>, Action<List<string>>?>((all, active, onApply) =>
            {
                service.Templates.Clear();
                return Task.FromResult<List<string>?>(active.ToList());
            });
        var vm = new TickerListViewModel(null!, null!, WeakReferenceMessenger.Default, new SynchronousDispatcherService(), watchlistManager.Object, null!, dialogService.Object, null!,
            new MockChartSettingsManager(), NullLogger<TickerListViewModel>.Instance, service);
        var view = new StockAnalyzer.Avalonia.Views.TickerListView { DataContext = vm };
        var window = new Window { Content = view, Width = 900, Height = 600 };

        try
        {
            window.Show();
            Pump();
            var selector = vm.ColumnTemplateSelector;
            var combo = view.FindControl<ComboBox>("ColumnTemplateComboBox")!;
            selector.SelectedEntry = selector.Entries.Single(e => e.Id == template.Id);
            Pump();

            vm.ShowColumnChooserCommand.Execute(null);
            Pump();

            Assert.Same(selector.ActiveColumnsEntry, selector.SelectedEntry);
            Assert.Same(selector.ActiveColumnsEntry, combo.SelectedItem);
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }
    }
}
