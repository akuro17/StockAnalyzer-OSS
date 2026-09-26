using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models.Templates;
using StockAnalyzer.Core.Models.Watchlist;
using StockAnalyzer.Avalonia.Common;

namespace StockAnalyzer.Avalonia.ViewModels.Dialogs;

/// <summary>
/// ViewModel managing states and operations for the Column Chooser dialog.
/// </summary>
public partial class ColumnChooserViewModel : ColumnPickerViewModelBase<ColumnItemViewModel>, IDisposable
{
    private readonly ITemplateService _templateService;
    private readonly IMessenger _messenger;
    private readonly ILogger<ColumnChooserViewModel> _logger;
    private readonly NotifyCollectionChangedEventHandler _collectionChangedHandler;
    private readonly System.ComponentModel.PropertyChangedEventHandler _itemPropertyChangedHandler;
    private bool _isDisposed;
    private bool _isTemplateBusy;

    [ObservableProperty]
    private ColumnItemViewModel? _selectedColumn;

    [ObservableProperty]
    private ColumnTemplate? _selectedTemplate;

    [ObservableProperty]
    private string _newTemplateName = string.Empty;

    public ObservableCollection<ColumnItemViewModel> AllItems { get; } = new();
    public ObservableCollection<ColumnTemplate> Templates { get; } = new();
    public ObservableCollection<string> SelectedTemplateColumnNames { get; } = new();

    public ColumnChooserViewModel(
        IEnumerable<WatchlistColumnMetadata> allColumns, 
        IEnumerable<string> activeColumnNames, 
        IMessenger? messenger = null,
        ITemplateService? templateService = null,
        ILogger<ColumnChooserViewModel>? logger = null)
    {
        SelectedCategory = ColumnCategory.Active;
        _messenger = messenger ?? WeakReferenceMessenger.Default;
        _templateService = templateService ?? new StockAnalyzer.Core.Services.TemplateService();
        _logger = logger ?? NullLogger<ColumnChooserViewModel>.Instance;
        _collectionChangedHandler = (s, e) => UpdateFilteredItems();
        _itemPropertyChangedHandler = OnItemPropertyChanged;

        var activeList = activeColumnNames.ToList();
        var activeSet = new HashSet<string>(activeList, StringComparer.OrdinalIgnoreCase);
        
        // 1. Add active columns in their current customized order
        foreach (var name in activeList)
        {
            var col = allColumns.FirstOrDefault(c => string.Equals(c.MemberName, name, StringComparison.OrdinalIgnoreCase));
            if (col != null)
            {
                var item = ColumnItemViewModel.Create(col, true);
                BindItemPropertyChange(item);
                AllItems.Add(item);
            }
        }

        // 2. Add remaining inactive columns in their default order
        foreach (var col in allColumns)
        {
            if (!activeSet.Contains(col.MemberName))
            {
                var item = ColumnItemViewModel.Create(col, false);
                BindItemPropertyChange(item);
                AllItems.Add(item);
            }
        }

        AllItems.CollectionChanged += _collectionChangedHandler;

        UpdateFilteredItems();

        // Load templates asynchronously
        _ = LoadTemplatesAsync();
    }

    private void BindItemPropertyChange(ColumnItemViewModel item)
    {
        item.PropertyChanged += _itemPropertyChangedHandler;
    }

    private void OnItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ColumnItemViewModel.IsActive))
        {
            if (SelectedCategory == ColumnCategory.Active)
            {
                UpdateFilteredItems();
            }
        }
    }

    protected override void OnSelectedCategoryChangedCore(ColumnCategory value)
    {
        base.OnSelectedCategoryChangedCore(value);
        OnPropertyChanged(nameof(IsTemplatesCategory));
        OnPropertyChanged(nameof(IsNotTemplatesCategory));
    }

    public bool IsTemplatesCategory => SelectedCategory == ColumnCategory.Templates;
    public bool IsNotTemplatesCategory => SelectedCategory != ColumnCategory.Templates;

    protected override void UpdateFilteredItems()
    {
        FilteredItems.Clear();
        var query = SearchQuery?.Trim() ?? string.Empty;

        IEnumerable<ColumnItemViewModel> items;
        if (SelectedCategory == ColumnCategory.Active)
        {
            items = AllItems.Where(item => item.IsActive);
        }
        else if (SelectedCategory == ColumnCategory.All)
        {
            items = AllItems
                .OrderBy(item => item.IsSelect ? 0 : (item.IsSymbol ? 1 : 2))
                .ThenBy(item => item.EnglishName, StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            items = AllItems
                .Where(item => item.Category == SelectedCategory)
                .OrderBy(item => item.IsSelect ? 0 : (item.IsSymbol ? 1 : 2))
                .ThenBy(item => item.EnglishName, StringComparer.OrdinalIgnoreCase);
        }

        if (!string.IsNullOrEmpty(query))
        {
            items = items.Where(item =>
                item.EnglishName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                item.MemberName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                (item.Description != null && item.Description.Contains(query, StringComparison.OrdinalIgnoreCase)));
        }

        foreach (var item in items)
        {
            FilteredItems.Add(item);
        }

        if (SelectedColumn == null || !FilteredItems.Contains(SelectedColumn))
        {
            SelectedColumn = FilteredItems.FirstOrDefault();
        }
    }

    /// <summary>
    /// Commands to set IsActive to true for all columns in the currently selected category (except Symbol columns).
    /// </summary>
    [RelayCommand]
    private void SelectAll()
    {
        // If in Active or All category, select all in AllItems (except symbol)
        var targetItems = (SelectedCategory == ColumnCategory.Active || SelectedCategory == ColumnCategory.All)
            ? AllItems 
            : AllItems.Where(item => item.Category == SelectedCategory);

        foreach (var item in targetItems)
        {
            if (!item.IsSymbol)
            {
                item.IsActive = true;
            }
        }
    }

    /// <summary>
    /// Commands to set IsActive to false for all columns in the currently selected category (except Symbol columns).
    /// </summary>
    [RelayCommand]
    private void ClearAll()
    {
        var targetItems = (SelectedCategory == ColumnCategory.Active || SelectedCategory == ColumnCategory.All)
            ? AllItems 
            : AllItems.Where(item => item.Category == SelectedCategory);

        foreach (var item in targetItems)
        {
            if (!item.IsSymbol)
            {
                item.IsActive = false;
            }
        }
    }

    /// <summary>
    /// Returns the names of all columns currently selected as active.
    /// </summary>
    public List<string> GetActiveColumnNames()
    {
        return AllItems
            .Where(item => item.IsActive)
            .Select(item => item.MemberName)
            .ToList();
    }

    /// <summary>
    /// Optional callback for custom Apply behavior (isolated context).
    /// </summary>
    public Action<List<string>>? OnApplyAction { get; set; }

    /// <summary>
    /// Apply changes immediately.
    /// </summary>
    [RelayCommand]
    private void Apply()
    {
        var activeCols = GetActiveColumnNames();
        if (OnApplyAction != null)
        {
            OnApplyAction(activeCols);
        }
        else
        {
            _messenger.Send(new ColumnChooserAppliedMessage(activeCols));
        }
    }

    partial void OnSelectedTemplateChanged(ColumnTemplate? value)
    {
        SelectedTemplateColumnNames.Clear();
        if (value != null)
        {
            var displayNames = value.ColumnNames
                .Select(memberName => AllItems.FirstOrDefault(item => string.Equals(item.MemberName, memberName, StringComparison.OrdinalIgnoreCase))?.EnglishName ?? memberName)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (var name in displayNames)
            {
                SelectedTemplateColumnNames.Add(name);
            }
        }
    }

    [RelayCommand]
    private async Task SaveTemplateAsync()
    {
        if (_isTemplateBusy) return;
        if (string.IsNullOrWhiteSpace(NewTemplateName)) return;

        var activeColumnNames = GetActiveColumnNames();
        if (!activeColumnNames.Any()) return;

        _isTemplateBusy = true;
        try
        {
            var trimmedName = NewTemplateName.Trim();
            var existing = Templates.FirstOrDefault(t => string.Equals(t.Name, trimmedName, StringComparison.OrdinalIgnoreCase));

            var template = existing ?? new ColumnTemplate
            {
                Id = Guid.NewGuid(),
                Name = trimmedName,
                SchemaVersion = 1,
                CreatedAt = DateTime.UtcNow
            };

            template.SetColumnNames(activeColumnNames);
            template.UpdatedAt = DateTime.UtcNow;

            var validation = await _templateService.ValidateAsync(template);
            if (!validation.IsValid)
            {
                _logger.LogWarning("Cannot save invalid column template '{Name}': {Errors}", trimmedName, string.Join(", ", validation.Errors));
                return;
            }

            if (existing == null)
            {
                Templates.Add(template);
            }
            NewTemplateName = string.Empty;

            await _templateService.SaveAsync(template);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save column template '{Name}'", NewTemplateName);
        }
        finally
        {
            _isTemplateBusy = false;
        }
    }

    [RelayCommand]
    private async Task LoadTemplateAsync(ColumnTemplate? template)
    {
        if (template == null || _isTemplateBusy) return;

        _isTemplateBusy = true;
        try
        {
            // 1. Validate template BEFORE mutating UI state
            var validation = await _templateService.ValidateAsync(template);
            if (!validation.IsValid)
            {
                _logger.LogWarning("Cannot apply invalid column template '{Name}': {Errors}", template.Name, string.Join(", ", validation.Errors));
                return;
            }

            var templateSet = new HashSet<string>(template.ColumnNames, StringComparer.OrdinalIgnoreCase);

            foreach (var item in AllItems)
            {
                item.IsActive = item.IsSymbol || templateSet.Contains(item.MemberName);
            }

            var orderedList = new List<ColumnItemViewModel>();
            var remainingItems = AllItems.ToList();

            foreach (var name in template.ColumnNames)
            {
                var item = remainingItems.FirstOrDefault(i => string.Equals(i.MemberName, name, StringComparison.OrdinalIgnoreCase));
                if (item != null)
                {
                    orderedList.Add(item);
                    remainingItems.Remove(item);
                }
            }

            var otherActive = remainingItems.Where(i => i.IsActive).ToList();
            foreach (var item in otherActive)
            {
                orderedList.Add(item);
                remainingItems.Remove(item);
            }

            orderedList.AddRange(remainingItems);

            for (int i = 0; i < orderedList.Count; i++)
            {
                int oldIndex = AllItems.IndexOf(orderedList[i]);
                if (oldIndex != i && oldIndex >= 0)
                {
                    AllItems.Move(oldIndex, i);
                }
            }

            UpdateFilteredItems();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load column template '{Name}'", template.Name);
        }
        finally
        {
            _isTemplateBusy = false;
        }
    }

    [RelayCommand]
    private async Task AppendTemplateAsync(ColumnTemplate? template)
    {
        if (template == null || _isTemplateBusy) return;

        _isTemplateBusy = true;
        try
        {
            // 1. Validate template BEFORE mutating UI state
            var validation = await _templateService.ValidateAsync(template);
            if (!validation.IsValid)
            {
                _logger.LogWarning("Cannot append invalid column template '{Name}': {Errors}", template.Name, string.Join(", ", validation.Errors));
                return;
            }

            var templateSet = new HashSet<string>(template.ColumnNames, StringComparer.OrdinalIgnoreCase);

            // Activate matching columns without deactivating existing active columns
            foreach (var item in AllItems)
            {
                if (templateSet.Contains(item.MemberName))
                {
                    item.IsActive = true;
                }
            }

            // Maintain existing active columns first, then append newly activated template columns
            var orderedList = new List<ColumnItemViewModel>();
            var remainingItems = AllItems.ToList();

            // 1. Keep already ordered active items
            var existingActive = remainingItems.Where(i => i.IsActive && !templateSet.Contains(i.MemberName)).ToList();
            foreach (var item in existingActive)
            {
                orderedList.Add(item);
                remainingItems.Remove(item);
            }

            // 2. Add template active items in template order
            foreach (var name in template.ColumnNames)
            {
                var item = remainingItems.FirstOrDefault(i => string.Equals(i.MemberName, name, StringComparison.OrdinalIgnoreCase));
                if (item != null)
                {
                    orderedList.Add(item);
                    remainingItems.Remove(item);
                }
            }

            // 3. Add any other active items
            var otherActive = remainingItems.Where(i => i.IsActive).ToList();
            foreach (var item in otherActive)
            {
                orderedList.Add(item);
                remainingItems.Remove(item);
            }

            // 4. Add remaining inactive items
            orderedList.AddRange(remainingItems);

            for (int i = 0; i < orderedList.Count; i++)
            {
                int oldIndex = AllItems.IndexOf(orderedList[i]);
                if (oldIndex != i && oldIndex >= 0)
                {
                    AllItems.Move(oldIndex, i);
                }
            }

            UpdateFilteredItems();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to append column template '{Name}'", template.Name);
        }
        finally
        {
            _isTemplateBusy = false;
        }
    }

    [RelayCommand]
    private async Task DeleteTemplateAsync(ColumnTemplate? template)
    {
        if (template == null || _isTemplateBusy) return;

        _isTemplateBusy = true;
        try
        {
            await _templateService.DeleteAsync(TemplateType.Column, template.Id);
            Templates.Remove(template);
            if (SelectedTemplate == template)
            {
                SelectedTemplate = null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete column template '{Name}' ({Id})", template.Name, template.Id);
        }
        finally
        {
            _isTemplateBusy = false;
        }
    }

    private async Task LoadTemplatesAsync()
    {
        if (_isTemplateBusy) return;
        _isTemplateBusy = true;
        try
        {
            var list = await _templateService.GetAllAsync<ColumnTemplate>(TemplateType.Column);
            Templates.Clear();
            foreach (var t in list)
            {
                Templates.Add(t);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load column templates.");
        }
        finally
        {
            _isTemplateBusy = false;
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        AllItems.CollectionChanged -= _collectionChangedHandler;
        foreach (var item in AllItems)
        {
            item.PropertyChanged -= _itemPropertyChangedHandler;
        }
        GC.SuppressFinalize(this);
    }
}
