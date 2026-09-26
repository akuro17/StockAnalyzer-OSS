using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using StockAnalyzer.Avalonia.Tests.Services;
using StockAnalyzer.Avalonia.Tests.TestHelpers;
using StockAnalyzer.Avalonia.ViewModels.TickerList;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models.Templates;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

public class TickerColumnTemplateSelectorViewModelTests
{
    private const string ActiveName = "Active Columns";
    private const string InvalidTemplateName = "Invalid";

    private sealed class Harness
    {
        public InMemoryColumnTemplateService Service { get; } = new() { InvalidTemplateName = InvalidTemplateName };
        public List<string> Displayed { get; } = new() { "Symbol", "Name", "Close" };
        public int ApplyCount { get; private set; }
        public TickerColumnTemplateSelectorViewModel Sut { get; }

        public Harness()
        {
            Sut = new TickerColumnTemplateSelectorViewModel(
                Service,
                ActiveName,
                () => Displayed.ToList(),
                names =>
                {
                    ApplyCount++;
                    Displayed.Clear();
                    Displayed.AddRange(names);
                },
                new SynchronousDispatcherService());
        }

        public ColumnTemplate AddTemplate(string name, params string[] columns)
        {
            var t = new ColumnTemplate { Id = Guid.NewGuid(), Name = name, ColumnNames = columns };
            Service.Templates.Add(t);
            return t;
        }

        public async Task<ColumnTemplate> SelectAsync(Guid id)
        {
            await Sut.ReloadEntriesAsync();
            var entry = Sut.Entries.Single(e => e.Id == id);
            Sut.SelectedEntry = entry;
            return entry;
        }
    }

    [Fact]
    public void Constructor_SelectsActiveColumnsSentinelAsFirstEntry()
    {
        var h = new Harness();

        Assert.Same(h.Sut.ActiveColumnsEntry, h.Sut.Entries.First());
        Assert.Equal(Guid.Empty, h.Sut.ActiveColumnsEntry.Id);
        Assert.Equal(ActiveName, h.Sut.ActiveColumnsEntry.Name);
        Assert.Same(h.Sut.ActiveColumnsEntry, h.Sut.SelectedEntry);
        Assert.Same(h.Sut.ActiveColumnsEntry, h.Sut.SelectedEntry);
    }

    [Fact]
    public async Task ReloadEntries_ListsActiveFirstThenTemplatesByName()
    {
        var h = new Harness();
        h.AddTemplate("Zeta", "Symbol");
        h.AddTemplate("Alpha", "Symbol");

        await h.Sut.ReloadEntriesAsync();

        Assert.Equal(new[] { ActiveName, "Alpha", "Zeta" }, h.Sut.Entries.Select(e => e.Name).ToArray());
        Assert.Same(h.Sut.ActiveColumnsEntry, h.Sut.SelectedEntry);
    }

    [Fact]
    public async Task SelectingTemplate_AppliesItsColumnsInOrder_AndReturningToActiveRestoresWorkingSetExactly()
    {
        var h = new Harness();
        var t = h.AddTemplate("Fundamentals", "Symbol", "ReturnOnEquity", "Name");
        var workingSet = h.Displayed.ToList();

        var entry = await h.SelectAsync(t.Id);

        Assert.Equal(new[] { "Symbol", "ReturnOnEquity", "Name" }, h.Displayed);
        Assert.Equal(t.Id, h.Sut.SelectedEntry?.Id);
        Assert.Equal(workingSet, h.Sut.GetWorkingSetColumnNames());

        h.Sut.SelectedEntry = h.Sut.ActiveColumnsEntry;

        Assert.Equal(workingSet, h.Displayed);
        Assert.Same(h.Sut.ActiveColumnsEntry, h.Sut.SelectedEntry);
        Assert.NotNull(entry);
    }

    [Fact]
    public async Task SelectingTemplate_DoesNotSaveAnything()
    {
        var h = new Harness();
        var t = h.AddTemplate("Fundamentals", "Symbol", "Name");

        await h.SelectAsync(t.Id);
        h.Sut.SelectedEntry = h.Sut.ActiveColumnsEntry;

        Assert.Equal(0, h.Service.SaveCount);
    }

    [Fact]
    public async Task EditingWhileTemplateSelected_SavesOnlyThatTemplate_AndKeepsWorkingSet()
    {
        var h = new Harness();
        var t1 = h.AddTemplate("One", "Symbol", "Name");
        var t2 = h.AddTemplate("Two", "Symbol", "Close");
        var workingSet = h.Displayed.ToList();

        await h.SelectAsync(t1.Id);
        var edited = new[] { "Symbol", "Name", "Volume" };
        h.Displayed.Clear();
        h.Displayed.AddRange(edited);
        h.Sut.OnColumnsEditedByUser(edited);

        Assert.Equal(1, h.Service.SaveCount);
        Assert.Equal(t1.Id, h.Service.Saved.Single().Id);
        Assert.Equal(edited, h.Service.Templates.Single(x => x.Id == t1.Id).ColumnNames);
        Assert.Equal(new[] { "Symbol", "Close" }, h.Service.Templates.Single(x => x.Id == t2.Id).ColumnNames);
        Assert.Equal(workingSet, h.Sut.GetWorkingSetColumnNames());

        h.Sut.SelectedEntry = h.Sut.ActiveColumnsEntry;
        Assert.Equal(workingSet, h.Displayed);
    }

    [Fact]
    public async Task Reload_WaitsForPendingEditSave_SoTheReloadedTemplateHasTheEditedColumns()
    {
        var h = new Harness();
        var t = h.AddTemplate("One", "Symbol", "Name");
        await h.SelectAsync(t.Id);
        var gate = new TaskCompletionSource();
        h.Service.SaveGate = gate.Task;
        var edited = new[] { "Symbol", "Name", "Volume" };

        h.Sut.OnColumnsEditedByUser(edited);
        var reload = h.Sut.ReloadEntriesAsync();

        Assert.False(reload.IsCompleted);

        gate.SetResult();
        await reload;

        Assert.Equal(edited, h.Sut.Entries.Single(e => e.Id == t.Id).ColumnNames);
    }

    [Fact]
    public async Task RepeatedIdenticalEdit_SavesOnce()
    {
        var h = new Harness();
        var t = h.AddTemplate("One", "Symbol", "Name");
        await h.SelectAsync(t.Id);
        var edited = new[] { "Symbol", "Volume" };

        h.Sut.OnColumnsEditedByUser(edited);
        h.Sut.OnColumnsEditedByUser(edited);

        Assert.Equal(1, h.Service.SaveCount);
    }

    [Fact]
    public void EditingWhileActiveColumnsSelected_SavesNothing()
    {
        var h = new Harness();

        h.Sut.OnColumnsEditedByUser(new[] { "Symbol", "Volume" });

        Assert.Equal(0, h.Service.SaveCount);
    }

    [Fact]
    public async Task InvalidTemplate_IsNotApplied_AndSelectionReverts()
    {
        var h = new Harness();
        var bad = h.AddTemplate(InvalidTemplateName, "Symbol", "Volume");
        var before = h.Displayed.ToList();

        await h.SelectAsync(bad.Id);

        Assert.Equal(before, h.Displayed);
        Assert.Equal(0, h.ApplyCount);
        Assert.Same(h.Sut.ActiveColumnsEntry, h.Sut.SelectedEntry);
    }

    [Fact]
    public async Task Reload_KeepsSelectionByIdOnNewInstance_WithoutReapplying()
    {
        var h = new Harness();
        var t = h.AddTemplate("One", "Symbol", "Name");
        var first = await h.SelectAsync(t.Id);
        var applyCountBefore = h.ApplyCount;

        await h.Sut.ReloadEntriesAsync();

        Assert.NotSame(first, h.Sut.SelectedEntry);
        Assert.Equal(t.Id, h.Sut.SelectedEntry?.Id);
        Assert.Equal(applyCountBefore, h.ApplyCount);
    }

    [Fact]
    public async Task Reload_WhenSelectedTemplateWasDeleted_FallsBackToActiveAndRestoresWorkingSet()
    {
        var h = new Harness();
        var t = h.AddTemplate("One", "Symbol", "Name");
        var workingSet = h.Displayed.ToList();
        await h.SelectAsync(t.Id);
        h.Service.Templates.Clear();

        await h.Sut.ReloadEntriesAsync();

        Assert.Same(h.Sut.ActiveColumnsEntry, h.Sut.SelectedEntry);
        Assert.Equal(workingSet, h.Displayed);
    }

    [Fact]
    public async Task SwitchingTemplateToTemplate_KeepsWorkingSetFromActive()
    {
        var h = new Harness();
        var t1 = h.AddTemplate("One", "Symbol", "Name");
        var t2 = h.AddTemplate("Two", "Symbol", "Close");
        var workingSet = h.Displayed.ToList();

        await h.SelectAsync(t1.Id);
        await h.SelectAsync(t2.Id);
        Assert.Equal(new[] { "Symbol", "Close" }, h.Displayed);

        h.Sut.SelectedEntry = h.Sut.ActiveColumnsEntry;
        Assert.Equal(workingSet, h.Displayed);
    }

    private static readonly Guid ListA = Guid.NewGuid();
    private static readonly Guid ListB = Guid.NewGuid();

    [Fact]
    public async Task SelectionIsKeptPerList_SwitchingListsRestoresEachListsEntry()
    {
        var h = new Harness();
        var t = h.AddTemplate("One", "Symbol", "Name", "Volume");
        var workingSet = h.Displayed.ToList();
        await h.Sut.ReloadEntriesAsync();

        h.Sut.OnActiveListChanged(ListA);
        h.Sut.SelectedEntry = h.Sut.Entries.Single(e => e.Id == t.Id);
        Assert.Equal(new[] { "Symbol", "Name", "Volume" }, h.Displayed);

        h.Sut.OnActiveListChanged(ListB);
        Assert.Same(h.Sut.ActiveColumnsEntry, h.Sut.SelectedEntry);
        Assert.Equal(workingSet, h.Displayed);

        h.Sut.OnActiveListChanged(ListA);
        Assert.Equal(t.Id, h.Sut.SelectedEntry?.Id);
        Assert.Equal(new[] { "Symbol", "Name", "Volume" }, h.Displayed);
    }

    [Fact]
    public async Task UnregisteredList_UsesActiveColumns()
    {
        var h = new Harness();
        var t = h.AddTemplate("One", "Symbol", "Name");
        await h.Sut.ReloadEntriesAsync();
        h.Sut.OnActiveListChanged(ListA);
        h.Sut.SelectedEntry = h.Sut.Entries.Single(e => e.Id == t.Id);

        h.Sut.OnActiveListChanged(ListB);

        Assert.Same(h.Sut.ActiveColumnsEntry, h.Sut.SelectedEntry);
    }

    [Fact]
    public async Task ListSwitch_DoesNotSaveTemplates()
    {
        var h = new Harness();
        var t = h.AddTemplate("One", "Symbol", "Name");
        await h.Sut.ReloadEntriesAsync();
        h.Sut.OnActiveListChanged(ListA);
        h.Sut.SelectedEntry = h.Sut.Entries.Single(e => e.Id == t.Id);

        h.Sut.OnActiveListChanged(ListB);
        h.Sut.OnActiveListChanged(ListA);

        Assert.Equal(0, h.Service.SaveCount);
    }

    [Fact]
    public async Task EditingWhileTemplateSelected_OnlyAffectsThatTemplate_AcrossLists()
    {
        var h = new Harness();
        var t1 = h.AddTemplate("One", "Symbol", "Name");
        var t2 = h.AddTemplate("Two", "Symbol", "Close");
        await h.Sut.ReloadEntriesAsync();
        h.Sut.OnActiveListChanged(ListA);
        h.Sut.SelectedEntry = h.Sut.Entries.Single(e => e.Id == t1.Id);
        h.Sut.OnActiveListChanged(ListB);
        h.Sut.SelectedEntry = h.Sut.Entries.Single(e => e.Id == t2.Id);

        var edited = new[] { "Symbol", "Close", "Volume" };
        h.Sut.OnColumnsEditedByUser(edited);

        Assert.Equal(edited, h.Service.Templates.Single(x => x.Id == t2.Id).ColumnNames);
        Assert.Equal(new[] { "Symbol", "Name" }, h.Service.Templates.Single(x => x.Id == t1.Id).ColumnNames);
    }

    [Fact]
    public async Task ChoosingActiveColumns_RemovesTheListsEntry()
    {
        var h = new Harness();
        var t = h.AddTemplate("One", "Symbol", "Name");
        await h.Sut.ReloadEntriesAsync();
        h.Sut.OnActiveListChanged(ListA);
        h.Sut.SelectedEntry = h.Sut.Entries.Single(e => e.Id == t.Id);
        h.Sut.SelectedEntry = h.Sut.ActiveColumnsEntry;

        Assert.DoesNotContain(ListA, h.Sut.ExportSelectionsByList().Keys);
    }

    [Fact]
    public async Task ExportImport_RoundTripsSelectionsAndAppliesCurrentList()
    {
        var h = new Harness();
        var t = h.AddTemplate("One", "Symbol", "Name", "Volume");
        await h.Sut.ReloadEntriesAsync();
        h.Sut.OnActiveListChanged(ListA);
        h.Sut.SelectedEntry = h.Sut.Entries.Single(e => e.Id == t.Id);
        var exported = h.Sut.ExportSelectionsByList();
        Assert.Equal(t.Id, exported[ListA]);

        var h2 = new Harness();
        h2.Service.Templates.AddRange(h.Service.Templates);
        h2.Sut.OnActiveListChanged(ListA);
        h2.Sut.ImportSelectionsByList(exported);

        Assert.Equal(t.Id, h2.Sut.SelectedEntry?.Id);
        Assert.Equal(new[] { "Symbol", "Name", "Volume" }, h2.Displayed);
    }

    [Fact]
    public void Import_WithUnknownTemplate_FallsBackToActiveColumnsAndDropsEntry()
    {
        var h = new Harness();
        var before = h.Displayed.ToList();
        h.Sut.OnActiveListChanged(ListA);

        h.Sut.ImportSelectionsByList(new Dictionary<Guid, Guid> { [ListA] = Guid.NewGuid() });

        Assert.Same(h.Sut.ActiveColumnsEntry, h.Sut.SelectedEntry);
        Assert.Equal(before, h.Displayed);
        Assert.DoesNotContain(ListA, h.Sut.ExportSelectionsByList().Keys);
    }

    [Fact]
    public void Import_WithNullOrEmpty_KeepsActiveColumns()
    {
        var h = new Harness();
        h.Sut.OnActiveListChanged(ListA);

        h.Sut.ImportSelectionsByList(null);
        h.Sut.ImportSelectionsByList(new Dictionary<Guid, Guid>());

        Assert.Same(h.Sut.ActiveColumnsEntry, h.Sut.SelectedEntry);
    }

    [Fact]
    public async Task SelectionChanged_IsRaisedForUserSelection_ButNotForReload()
    {
        var h = new Harness();
        var t = h.AddTemplate("One", "Symbol", "Name");
        var raised = 0;
        h.Sut.SelectionChanged += (_, _) => raised++;

        await h.Sut.ReloadEntriesAsync();
        Assert.Equal(0, raised);

        await h.SelectAsync(t.Id);
        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task EditAfterTemplateWasDeleted_DoesNotResurrectIt_AndFallsBackToActiveColumns()
    {
        var h = new Harness();
        var t = h.AddTemplate("One", "Symbol", "Name");
        var workingSet = h.Displayed.ToList();
        await h.SelectAsync(t.Id);
        h.Service.Templates.Clear(); // deleted elsewhere while it is still selected here

        h.Sut.OnColumnsEditedByUser(new[] { "Symbol", "Name", "Volume" });

        Assert.Equal(0, h.Service.SaveCount);
        Assert.Empty(h.Service.Templates);
        Assert.Same(h.Sut.ActiveColumnsEntry, h.Sut.SelectedEntry);
        Assert.Equal(workingSet, h.Displayed);
    }

    [Fact]
    public async Task EditThatFailsValidation_IsNotSaved_AndTheInMemoryTemplateIsRolledBack()
    {
        var h = new Harness();
        var t = h.AddTemplate("One", "Symbol", "Name");
        var entry = await h.SelectAsync(t.Id);
        h.Service.InvalidTemplateName = "One";

        h.Sut.OnColumnsEditedByUser(new[] { "Symbol", "Name", "Volume" });

        Assert.Equal(0, h.Service.SaveCount);
        Assert.Equal(new[] { "Symbol", "Name" }, entry.ColumnNames);
    }

    [Fact]
    public async Task AfterDispose_ReloadAndSelectionDoNothing()
    {
        var h = new Harness();
        var t = h.AddTemplate("One", "Symbol", "Name");
        await h.Sut.ReloadEntriesAsync();
        var entry = h.Sut.Entries.Single(e => e.Id == t.Id);
        var raised = 0;
        h.Sut.SelectionChanged += (_, _) => raised++;
        h.AddTemplate("Two", "Symbol");

        h.Sut.Dispose();
        await h.Sut.ReloadEntriesAsync();
        h.Sut.SelectedEntry = entry;

        Assert.Equal(2, h.Sut.Entries.Count);
        Assert.Equal(0, h.ApplyCount);
        Assert.Equal(0, raised);
    }

    [Fact]
    public async Task ApplyWithUnchangedColumns_AfterTemplateWasDeleted_FallsBackToActiveColumns()
    {
        var h = new Harness();
        var t = h.AddTemplate("One", "Symbol", "Name");
        var workingSet = h.Displayed.ToList();
        await h.SelectAsync(t.Id);
        h.Service.Templates.Clear(); // deleted in the Column Customization dialog

        h.Sut.OnColumnsEditedByUser(h.Displayed.ToList()); // Apply without changing any column

        Assert.Same(h.Sut.ActiveColumnsEntry, h.Sut.SelectedEntry);
        Assert.Equal(workingSet, h.Displayed);
        Assert.Equal(0, h.Service.SaveCount);
    }

    [Fact]
    public async Task InvalidTemplateSelectedForAList_IsNotKeptInThatListsStoredSelection()
    {
        var h = new Harness();
        var bad = h.AddTemplate(InvalidTemplateName, "Symbol", "Volume");
        h.Sut.OnActiveListChanged(ListA);

        await h.SelectAsync(bad.Id);

        Assert.Same(h.Sut.ActiveColumnsEntry, h.Sut.SelectedEntry);
        Assert.DoesNotContain(ListA, h.Sut.ExportSelectionsByList().Keys);
    }

    [Fact]
    public void Import_WithUnknownTemplateWhileActiveColumnsSelected_RequestsASaveForTheRemovedEntry()
    {
        var h = new Harness();
        h.Sut.OnActiveListChanged(ListA);
        var raised = 0;
        h.Sut.SelectionChanged += (_, _) => raised++;

        h.Sut.ImportSelectionsByList(new Dictionary<Guid, Guid> { [ListA] = Guid.NewGuid() });

        Assert.Equal(1, raised);
    }
}
