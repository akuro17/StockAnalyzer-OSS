using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using StockAnalyzer.Avalonia.Tests.Services;
using StockAnalyzer.Avalonia.Tests.TestHelpers;
using StockAnalyzer.Avalonia.ViewModels.TickerList;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Models.Templates;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

/// <summary>sa_implement (Column Customization selection scope,
/// Y:\Temp\sa_implementation_plan_TickerColumnSelectionScope.md Phase 4). A separate class on purpose: the
/// pre-existing selector tests stay untouched and prove the PerList default is unchanged. The harness is a copy
/// of that class's private harness.</summary>
public class TickerColumnSelectionScopeTests
{
    private const string ActiveName = "Active Columns";
    private static readonly Guid ListA = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid ListB = Guid.Parse("00000000-0000-0000-0000-00000000000b");

    private sealed class Harness
    {
        public InMemoryColumnTemplateService Service { get; } = new();
        public List<string> Displayed { get; } = new() { "Symbol", "Name", "Close" };
        public int SelectionChangedCount { get; private set; }
        public TickerColumnTemplateSelectorViewModel Sut { get; }

        public Harness()
        {
            Sut = new TickerColumnTemplateSelectorViewModel(
                Service,
                ActiveName,
                () => Displayed.ToList(),
                names =>
                {
                    Displayed.Clear();
                    Displayed.AddRange(names);
                },
                new SynchronousDispatcherService());
            Sut.SelectionChanged += (_, _) => SelectionChangedCount++;
        }

        public ColumnTemplate AddTemplate(string name, params string[] columns)
        {
            var t = new ColumnTemplate { Id = Guid.NewGuid(), Name = name, ColumnNames = columns };
            Service.Templates.Add(t);
            return t;
        }

        public ColumnTemplate Entry(Guid id) => Sut.Entries.Single(e => e.Id == id);
    }

    [Fact]
    public async Task Shared_SelectionOnOneListAppliesToEveryList()
    {
        var h = new Harness();
        var t1 = h.AddTemplate("One", "Symbol", "Volume");
        await h.Sut.ReloadEntriesAsync();
        h.Sut.SetScope(TickerColumnSelectionScope.Shared);
        h.Sut.OnActiveListChanged(ListA);

        h.Sut.SelectedEntry = h.Entry(t1.Id);
        h.Sut.OnActiveListChanged(ListB);

        Assert.Equal(t1.Id, h.Sut.SelectedEntry?.Id);
        Assert.Equal(new[] { "Symbol", "Volume" }, h.Displayed);

        h.Sut.SelectedEntry = h.Sut.ActiveColumnsEntry;
        h.Sut.OnActiveListChanged(ListA);

        Assert.Same(h.Sut.ActiveColumnsEntry, h.Sut.SelectedEntry);
        Assert.Equal(new[] { "Symbol", "Name", "Close" }, h.Displayed);
    }

    [Fact]
    public async Task PerListDefault_KeepsSeparateSelectionsPerList()
    {
        var h = new Harness();
        var t1 = h.AddTemplate("One", "Symbol", "Volume");
        await h.Sut.ReloadEntriesAsync();
        h.Sut.OnActiveListChanged(ListA);
        h.Sut.SelectedEntry = h.Entry(t1.Id);

        h.Sut.OnActiveListChanged(ListB);

        Assert.Same(h.Sut.ActiveColumnsEntry, h.Sut.SelectedEntry);
        h.Sut.OnActiveListChanged(ListA);
        Assert.Equal(t1.Id, h.Sut.SelectedEntry?.Id);
    }

    [Fact]
    public async Task ScopeRoundTrip_NeverTouchesTheOtherStore()
    {
        var h = new Harness();
        var t1 = h.AddTemplate("One", "Symbol", "Volume");
        var t2 = h.AddTemplate("Two", "Symbol", "Close");
        var t3 = h.AddTemplate("Three", "Name");
        await h.Sut.ReloadEntriesAsync();
        h.Sut.OnActiveListChanged(ListA);
        h.Sut.SelectedEntry = h.Entry(t1.Id);
        h.Sut.OnActiveListChanged(ListB);
        h.Sut.SelectedEntry = h.Entry(t2.Id);
        h.Sut.OnActiveListChanged(ListA);
        var perListBefore = h.Sut.ExportSelectionsByList();

        h.Sut.SetScope(TickerColumnSelectionScope.Shared);

        // The shared store is empty, so Active Columns is applied; nothing is copied from the per-list store.
        Assert.Same(h.Sut.ActiveColumnsEntry, h.Sut.SelectedEntry);
        Assert.Equal(Guid.Empty, h.Sut.ExportSharedSelection());
        h.Sut.SelectedEntry = h.Entry(t3.Id);
        Assert.Equal(t3.Id, h.Sut.ExportSharedSelection());
        Assert.Equal(perListBefore, h.Sut.ExportSelectionsByList());

        h.Sut.SetScope(TickerColumnSelectionScope.PerList);

        Assert.Equal(t1.Id, h.Sut.SelectedEntry?.Id);
        h.Sut.OnActiveListChanged(ListB);
        Assert.Equal(t2.Id, h.Sut.SelectedEntry?.Id);
        Assert.Equal(t3.Id, h.Sut.ExportSharedSelection());
    }

    [Fact]
    public async Task SharedImportExport_RoundTripsAndImportOnlyStores()
    {
        var h = new Harness();
        var t1 = h.AddTemplate("One", "Symbol", "Volume");
        await h.Sut.ReloadEntriesAsync();
        h.Sut.SetScope(TickerColumnSelectionScope.Shared);
        var raisedBefore = h.SelectionChangedCount;

        h.Sut.ImportSharedSelection(t1.Id);

        Assert.Equal(raisedBefore, h.SelectionChangedCount);
        Assert.Same(h.Sut.ActiveColumnsEntry, h.Sut.SelectedEntry);
        Assert.Equal(t1.Id, h.Sut.ExportSharedSelection());

        // The following per-list import is the single apply step of a workspace restore.
        h.Sut.OnActiveListChanged(ListA);
        h.Sut.ImportSelectionsByList(null);

        Assert.Equal(t1.Id, h.Sut.SelectedEntry?.Id);
        Assert.Equal(new[] { "Symbol", "Volume" }, h.Displayed);
    }

    [Fact]
    public async Task SharedExport_OmitsATemplateThatNoLongerExists()
    {
        var h = new Harness();
        await h.Sut.ReloadEntriesAsync();

        h.Sut.ImportSharedSelection(Guid.NewGuid());

        Assert.Equal(Guid.Empty, h.Sut.ExportSharedSelection());
    }

    [Fact]
    public void Shared_ImportOfAnUnknownTemplate_FallsBackToActiveColumnsWhenApplied()
    {
        var h = new Harness();
        var before = h.Displayed.ToList();
        h.Sut.SetScope(TickerColumnSelectionScope.Shared);
        h.Sut.ImportSharedSelection(Guid.NewGuid());

        h.Sut.OnActiveListChanged(ListA);
        h.Sut.ImportSelectionsByList(null);

        Assert.Same(h.Sut.ActiveColumnsEntry, h.Sut.SelectedEntry);
        Assert.Equal(before, h.Displayed);
        Assert.Equal(Guid.Empty, h.Sut.ExportSharedSelection());
    }

    [Fact]
    public void SetScope_UndefinedOrSameValue_IsIgnored()
    {
        var h = new Harness();
        h.Sut.OnActiveListChanged(ListA);
        var raised = h.SelectionChangedCount;

        h.Sut.SetScope((TickerColumnSelectionScope)99);
        h.Sut.SetScope(TickerColumnSelectionScope.PerList);

        Assert.Equal(raised, h.SelectionChangedCount);
        Assert.Same(h.Sut.ActiveColumnsEntry, h.Sut.SelectedEntry);
    }

    [Fact]
    public async Task Shared_ListSwitch_RaisesNoSelectionChangedAndKeepsColumns()
    {
        var h = new Harness();
        var t1 = h.AddTemplate("One", "Symbol", "Volume");
        await h.Sut.ReloadEntriesAsync();
        h.Sut.SetScope(TickerColumnSelectionScope.Shared);
        h.Sut.SelectedEntry = h.Entry(t1.Id);
        var raised = h.SelectionChangedCount;
        var displayed = h.Displayed.ToList();

        h.Sut.OnActiveListChanged(ListA);
        h.Sut.OnActiveListChanged(ListB);

        Assert.Equal(raised, h.SelectionChangedCount);
        Assert.Equal(displayed, h.Displayed);
    }

    [Fact]
    public async Task Shared_DeletedTemplate_FallsBackToActiveColumnsAndClearsTheSharedStore()
    {
        var h = new Harness();
        var t1 = h.AddTemplate("One", "Symbol", "Volume");
        await h.Sut.ReloadEntriesAsync();
        h.Sut.SetScope(TickerColumnSelectionScope.Shared);
        h.Sut.SelectedEntry = h.Entry(t1.Id);
        Assert.Equal(t1.Id, h.Sut.ExportSharedSelection());

        h.Service.Templates.Clear();
        await h.Sut.ReloadEntriesAsync();

        Assert.Same(h.Sut.ActiveColumnsEntry, h.Sut.SelectedEntry);
        Assert.Equal(Guid.Empty, h.Sut.ExportSharedSelection());
    }

    [Fact]
    public async Task WorkspaceSettings_SharedSelection_RoundTripsThroughSaveAndLoad_AndOldFilesDefaultToEmpty()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sa_ws_sharedcol_" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            var id = Guid.NewGuid();
            using var service = new StockAnalyzer.Core.Services.WorkspaceSerializationService();
            var path = System.IO.Path.Combine(dir, "roundtrip.json");
            await service.SaveAsync(new WorkspaceSettings { TickerListSharedColumnTemplateId = id }, path);
            Assert.Equal(id, (await service.LoadAsync(path))!.TickerListSharedColumnTemplateId);

            var legacy = System.IO.Path.Combine(dir, "legacy.json");
            await System.IO.File.WriteAllTextAsync(legacy, "{\"TickerListVisibleColumns\":[\"Symbol\"]}");
            Assert.Equal(Guid.Empty, (await service.LoadAsync(legacy))!.TickerListSharedColumnTemplateId);
            Assert.Equal(Guid.Empty, new WorkspaceSettings().TickerListSharedColumnTemplateId);
        }
        finally
        {
            try { System.IO.Directory.Delete(dir, true); } catch (System.IO.IOException) { }
        }
    }
}