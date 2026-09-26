using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models.Templates;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Avalonia.ViewModels.TickerList;

/// <summary>
/// Backs the Tickers tab's "Column Customization" dropdown
/// (Y:\Temp\sa_implementation_plan_TickerColumnTemplateDropdown.md).
/// The first entry is the sentinel <see cref="ActiveColumnsEntry"/> (the user's own working column set);
/// the remaining entries are the saved <see cref="ColumnTemplate"/>s. Column edits made while an entry is
/// selected are stored only in that entry's slot: the working set for Active Columns, the template
/// itself for a template.
/// </summary>
/// <remarks>
/// Takes its data source and column-apply callbacks as constructor delegates rather than holding a
/// reference to the owning <c>TickerListViewModel</c> (SA_ARCHITECTURE_RULES: Composed Child ViewModel
/// Decoupling). <see cref="_applyColumns"/> is only invoked for selection-driven changes, so applying
/// columns from here never feeds back into <see cref="OnColumnsEditedByUser"/>.
/// </remarks>
public sealed partial class TickerColumnTemplateSelectorViewModel : ObservableObject, IDisposable
{
    private readonly ITemplateService? _templateService;
    private readonly Func<IReadOnlyList<string>> _getDisplayedColumnNames;
    private readonly Action<IReadOnlyList<string>> _applyColumns;
    private readonly IDispatcherService _dispatcher;
    private readonly ILogger _logger;

    // Snapshot of the Active Columns working set, taken when leaving the Active Columns entry.
    private List<string> _workingSet = new();
    private ColumnTemplate _previousEntry;
    private bool _isSyncingSelection;
    private int _selectionVersion;

    // Ticker list Id -> selected ColumnTemplate Id. A list without an entry uses Active Columns.
    private readonly Dictionary<Guid, Guid> _selectionByList = new();
    private Guid _currentListId = Guid.Empty;
    // The list the currently selected entry was chosen for (differs from _currentListId only while a
    // list switch is still re-fetching the templates).
    private Guid _selectedEntryListId = Guid.Empty;
    private bool _isDisposed;
    private bool _entriesLoaded;
    private Task _pendingSave = Task.CompletedTask;

    /// <summary>Sentinel first entry (<c>Id == Guid.Empty</c>): the current working column set.</summary>
    public ColumnTemplate ActiveColumnsEntry { get; }

    public BulkObservableCollection<ColumnTemplate> Entries { get; } = new();

    [ObservableProperty]
    private ColumnTemplate? _selectedEntry;

    /// <summary>Two-way bound to the ComboBox's IsDropDownOpen: opening it re-fetches the saved
    /// templates so one saved in the Column Customization dialog appears without a restart.</summary>
    [ObservableProperty]
    private bool _isDropDownOpen;

    /// <summary>Raised when the user (or a restore) changes the selected entry, so the layout can be saved.</summary>
    public event EventHandler? SelectionChanged;

    public TickerColumnTemplateSelectorViewModel(
        ITemplateService? templateService,
        string activeColumnsEntryName,
        Func<IReadOnlyList<string>> getDisplayedColumnNames,
        Action<IReadOnlyList<string>> applyColumns,
        IDispatcherService dispatcher,
        ILogger? logger = null)
    {
        _templateService = templateService;
        _getDisplayedColumnNames = getDisplayedColumnNames ?? throw new ArgumentNullException(nameof(getDisplayedColumnNames));
        _applyColumns = applyColumns ?? throw new ArgumentNullException(nameof(applyColumns));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _logger = logger ?? NullLogger.Instance;

        ActiveColumnsEntry = new ColumnTemplate { Id = Guid.Empty, Name = activeColumnsEntryName };
        _previousEntry = ActiveColumnsEntry;
        Entries.Add(ActiveColumnsEntry);
        SelectedEntry = ActiveColumnsEntry;
    }

    private bool IsActiveEntry(ColumnTemplate? entry) => entry == null || entry.Id == Guid.Empty;

    /// <summary>The Active Columns working set: the displayed columns while Active Columns is
    /// selected, otherwise the snapshot taken when the template was selected.</summary>
    public IReadOnlyList<string> GetWorkingSetColumnNames()
    {
        if (IsActiveEntry(SelectedEntry) || _workingSet.Count == 0)
        {
            return _getDisplayedColumnNames();
        }
        return _workingSet;
    }

    partial void OnIsDropDownOpenChanged(bool value)
    {
        if (value)
        {
            _ = ReloadEntriesAsync();
        }
    }

    partial void OnSelectedEntryChanged(ColumnTemplate? value)
    {
        if (_isSyncingSelection || _isDisposed) return;

        var previous = _previousEntry;
        var target = value ?? ActiveColumnsEntry;
        _previousEntry = target;
        var version = ++_selectionVersion;
        _selectedEntryListId = _currentListId;
        RecordSelection(_currentListId, target);

        if (IsActiveEntry(target))
        {
            if (!IsActiveEntry(previous) && _workingSet.Count > 0)
            {
                _applyColumns(_workingSet.ToList());
            }
        }
        else
        {
            if (IsActiveEntry(previous))
            {
                _workingSet = _getDisplayedColumnNames().ToList();
            }
            _ = ApplyTemplateAsync(target, previous, version);
        }

        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task ApplyTemplateAsync(ColumnTemplate template, ColumnTemplate previous, int version)
    {
        try
        {
            if (_templateService != null)
            {
                var validation = await _templateService.ValidateAsync(template).ConfigureAwait(false);
                if (!validation.IsValid)
                {
                    _logger.LogWarning("Cannot apply invalid column template '{Name}': {Errors}", template.Name, string.Join(", ", validation.Errors));
                    _dispatcher.Post(() => RevertSelection(previous, version));
                    return;
                }
            }

            _dispatcher.Post(() =>
            {
                if (_isDisposed || version != _selectionVersion) return;
                if (template.ColumnNames.Count == 0)
                {
                    _logger.LogWarning("Column template '{Name}' has no columns; keeping the displayed columns.", template.Name);
                    return;
                }
                _applyColumns(template.ColumnNames);
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply column template '{Name}'.", template.Name);
        }
    }

    private void RevertSelection(ColumnTemplate previous, int version)
    {
        if (_isDisposed || version != _selectionVersion) return;
        _isSyncingSelection = true;
        try
        {
            SelectedEntry = previous;
            _previousEntry = previous;
        }
        finally
        {
            _isSyncingSelection = false;
        }
        // The stored selection must match what is displayed again, or the invalid template would be
        // persisted and retried on every switch to this list.
        RecordSelection(_selectedEntryListId, previous);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Called after the user edited the displayed columns (Column Customization dialog). While a
    /// template is selected the edit is written back to that template; while Active Columns is selected
    /// the displayed columns already are the working set, so nothing more is stored.
    /// </summary>
    public void OnColumnsEditedByUser(IReadOnlyList<string> displayedColumnNames)
    {
        var template = SelectedEntry;
        if (IsActiveEntry(template) || _templateService == null) return;
        if (displayedColumnNames.Count == 0) return;
        if (template!.ColumnNames.SequenceEqual(displayedColumnNames, StringComparer.Ordinal))
        {
            // Nothing to write, but the template may have been deleted in the dialog meanwhile: re-fetch so
            // the dropdown falls back to Active Columns instead of keeping a template that no longer exists.
            _ = ReloadEntriesAsync();
            return;
        }

        _pendingSave = SaveEditedTemplateAsync(_pendingSave, template, displayedColumnNames.ToList());
    }

    // The template is mutated synchronously (before the first await) so the in-memory instance and the
    // idempotence check in OnColumnsEditedByUser stay consistent; the disk write runs after any earlier
    // pending save, and ReloadEntriesAsync waits for it (SA_ARCHITECTURE_RULES: save-then-load ordering).
    // The edit is only persisted when the template still exists (a template deleted elsewhere must not
    // be resurrected) and validates; otherwise the in-memory change is rolled back.
    // Never faults: every failure is caught and logged here.
    private async Task SaveEditedTemplateAsync(Task previousSave, ColumnTemplate template, List<string> columnNames)
    {
        var previousNames = template.ColumnNames.ToList();
        var previousUpdatedAt = template.UpdatedAt;
        template.SetColumnNames(columnNames);
        template.UpdatedAt = DateTime.UtcNow;

        var persisted = false;
        try
        {
            await previousSave.ConfigureAwait(false);

            var stored = await _templateService!.GetAsync<ColumnTemplate>(TemplateType.Column, template.Id).ConfigureAwait(false);
            if (stored == null)
            {
                _logger.LogWarning("Column template '{Name}' no longer exists; the column edit is not saved.", template.Name);
                _dispatcher.Post(() =>
                {
                    if (!_isDisposed) _ = ReloadEntriesAsync();
                });
                return;
            }

            var validation = await _templateService.ValidateAsync(template).ConfigureAwait(false);
            if (!validation.IsValid)
            {
                _logger.LogWarning("Cannot save invalid column template '{Name}': {Errors}", template.Name, string.Join(", ", validation.Errors));
                return;
            }

            await _templateService.SaveAsync(template).ConfigureAwait(false);
            persisted = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save column template '{Name}' after a column edit.", template.Name);
        }
        finally
        {
            if (!persisted)
            {
                _dispatcher.Post(() =>
                {
                    // Only undo our own change; a newer edit queued behind this one must be kept.
                    if (template.ColumnNames.SequenceEqual(columnNames, StringComparer.Ordinal))
                    {
                        template.SetColumnNames(previousNames);
                        template.UpdatedAt = previousUpdatedAt;
                    }
                });
            }
        }
    }

    /// <summary>Re-fetches the saved templates, keeps the selection by Id, and falls back to Active
    /// Columns (re-applying the working set) if the selected template no longer exists.</summary>
    public async Task ReloadEntriesAsync()
    {
        if (_templateService == null || _isDisposed) return;
        try
        {
            // Never read the store while an edit save is still in flight: the read could return the
            // pre-edit columns and replace the edited instance in Entries.
            await _pendingSave.ConfigureAwait(false);
            var templates = await _templateService.GetAllAsync<ColumnTemplate>(TemplateType.Column).ConfigureAwait(false);
            await _dispatcher.PostAsync(() =>
            {
                if (!_isDisposed) ApplyLoadedEntries(templates);
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load column templates for the Tickers tab dropdown.");
        }
    }

    private void ApplyLoadedEntries(IReadOnlyList<ColumnTemplate> templates)
    {
        _entriesLoaded = true;
        var previousId = SelectedEntry?.Id ?? Guid.Empty;
        var merge = TemplateEntryList.Merge(
            ActiveColumnsEntry, templates.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase), previousId, t => t.Id);
        var replacement = merge.Replacement;
        var deleted = merge.SelectedWasDeleted;

        _isSyncingSelection = true;
        try
        {
            Entries.ReplaceRange(merge.Combined);
            if (!ReferenceEquals(SelectedEntry, replacement))
            {
                SelectedEntry = replacement;
            }
            _previousEntry = replacement;
        }
        finally
        {
            _isSyncingSelection = false;
        }

        if (deleted)
        {
            // The selected template was deleted elsewhere: return to the Active Columns working set.
            _selectionVersion++;
            // Remove the mapping of the list the deleted template was selected for (not necessarily the
            // list that is current now, while a list switch is re-fetching).
            RecordSelection(_selectedEntryListId, ActiveColumnsEntry);
            _selectedEntryListId = _currentListId;
            if (_workingSet.Count > 0)
            {
                _applyColumns(_workingSet.ToList());
            }
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void RecordSelection(Guid listId, ColumnTemplate target)
    {
        if (listId == Guid.Empty) return;

        if (IsActiveEntry(target))
        {
            _selectionByList.Remove(listId);
        }
        else
        {
            _selectionByList[listId] = target.Id;
        }
    }

    /// <summary>
    /// Called when the selected ticker list (sidebar node) changes: selects the entry remembered for
    /// that list, or Active Columns if none. The Active Columns working set itself is shared by all lists.
    /// </summary>
    public void OnActiveListChanged(Guid listId)
    {
        if (listId == Guid.Empty || listId == _currentListId) return;

        _currentListId = listId;
        ApplyCurrentListSelection();
    }

    private void ApplyCurrentListSelection()
    {
        var listId = _currentListId;
        if (listId == Guid.Empty) return;

        if (!_selectionByList.TryGetValue(listId, out var templateId) || templateId == Guid.Empty)
        {
            SelectEntry(ActiveColumnsEntry);
            return;
        }

        // Entries is only refreshed when the dropdown opens, so a template deleted elsewhere (e.g. in the
        // Column Customization dialog) can still be listed here. Re-fetch before selecting it, so the
        // dropdown never shows (or applies) a template that no longer exists.
        _ = ApplyListSelectionAfterReloadAsync(listId, templateId);
    }

    private async Task ApplyListSelectionAfterReloadAsync(Guid listId, Guid templateId)
    {
        try
        {
            await ReloadEntriesAsync().ConfigureAwait(false);
            await _dispatcher.PostAsync(() =>
            {
                if (_isDisposed || _currentListId != listId) return Task.CompletedTask;

                var removedStaleEntry = false;
                var entry = Entries.FirstOrDefault(e => e.Id == templateId);
                if (entry == null)
                {
                    _logger.LogWarning("Column template {Id} for ticker list {ListId} no longer exists; using Active Columns.", templateId, listId);
                    _selectionByList.Remove(listId);
                    entry = ActiveColumnsEntry;
                    removedStaleEntry = true;
                }
                var wasActive = ReferenceEquals(SelectedEntry, ActiveColumnsEntry);
                SelectEntry(entry);
                if (removedStaleEntry && wasActive)
                {
                    // No selection change was raised, but the stored per-list selection changed: request a save.
                    SelectionChanged?.Invoke(this, EventArgs.Empty);
                }
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply the column template selection of ticker list {ListId}.", listId);
        }
    }

    private void SelectEntry(ColumnTemplate entry)
    {
        if (!ReferenceEquals(SelectedEntry, entry))
        {
            SelectedEntry = entry;
        }
    }

    /// <summary>The per-list selections to persist. Entries whose template no longer exists are omitted
    /// once the template list has been loaded.</summary>
    public Dictionary<Guid, Guid> ExportSelectionsByList()
    {
        return _selectionByList
            .Where(kv => !_entriesLoaded || Entries.Any(e => e.Id == kv.Value))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    /// <summary>Restores persisted per-list selections (workspace load) and applies the one of the
    /// currently selected list. Unknown templates fall back to Active Columns.</summary>
    public void ImportSelectionsByList(IReadOnlyDictionary<Guid, Guid>? selectionsByList)
    {
        _selectionByList.Clear();
        if (selectionsByList != null)
        {
            foreach (var (listId, templateId) in selectionsByList)
            {
                if (listId != Guid.Empty && templateId != Guid.Empty)
                {
                    _selectionByList[listId] = templateId;
                }
            }
        }

        ApplyCurrentListSelection();
    }

    /// <summary>Stops in-flight asynchronous continuations from touching this ViewModel and releases
    /// its event subscribers.</summary>
    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        SelectionChanged = null;
    }
}
