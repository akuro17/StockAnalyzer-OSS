using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using System.IO;
using System.Text.Json;
using StockAnalyzer.Core.Models.Watchlist;
using StockAnalyzer.Avalonia.Common;

namespace StockAnalyzer.Avalonia.ViewModels.Dialogs;

/// <summary>
/// ViewModel managing states and operations for the Column Chooser dialog.
/// </summary>
public partial class ColumnChooserViewModel : ViewModelBase
{
    private static readonly string TemplatesFilePath = StockAnalyzer.Core.Common.PathDiscovery.ResolveConfigPath("column_templates.json");
    private readonly IMessenger _messenger;

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private ColumnCategory _selectedCategory = ColumnCategory.Active;

    [ObservableProperty]
    private ColumnItemViewModel? _selectedColumn;

    [ObservableProperty]
    private ColumnTemplate? _selectedTemplate;

    [ObservableProperty]
    private string _newTemplateName = string.Empty;

    public ObservableCollection<ColumnItemViewModel> AllItems { get; } = new();
    public ObservableCollection<ColumnItemViewModel> FilteredItems { get; } = new();
    public ObservableCollection<ColumnTemplate> Templates { get; } = new();
    public ObservableCollection<string> SelectedTemplateColumnNames { get; } = new();

    public ColumnChooserViewModel(IEnumerable<WatchlistColumnMetadata> allColumns, IEnumerable<string> activeColumnNames, IMessenger? messenger = null)
    {
        _messenger = messenger ?? WeakReferenceMessenger.Default;
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

        AllItems.CollectionChanged += (s, e) => UpdateFilteredItems();

        UpdateFilteredItems();

        // Load templates
        var saved = LoadTemplates();
        foreach (var t in saved)
        {
            Templates.Add(t);
        }
    }

    private void BindItemPropertyChange(ColumnItemViewModel item)
    {
        item.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(ColumnItemViewModel.IsActive))
            {
                if (SelectedCategory == ColumnCategory.Active)
                {
                    UpdateFilteredItems();
                }
            }
        };
    }

    partial void OnSearchQueryChanged(string value) => UpdateFilteredItems();
    partial void OnSelectedCategoryChanged(ColumnCategory value)
    {
        UpdateFilteredItems();
        OnPropertyChanged(nameof(IsTemplatesCategory));
        OnPropertyChanged(nameof(IsNotTemplatesCategory));
    }

    public bool IsTemplatesCategory => SelectedCategory == ColumnCategory.Templates;
    public bool IsNotTemplatesCategory => SelectedCategory != ColumnCategory.Templates;

    private void UpdateFilteredItems()
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
    private void SaveTemplate()
    {
        if (string.IsNullOrWhiteSpace(NewTemplateName)) return;

        var activeColumnNames = GetActiveColumnNames();
        if (!activeColumnNames.Any()) return;

        var existing = Templates.FirstOrDefault(t => string.Equals(t.Name, NewTemplateName.Trim(), StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            existing.ColumnNames = activeColumnNames;
        }
        else
        {
            var template = new ColumnTemplate
            {
                Name = NewTemplateName.Trim(),
                ColumnNames = activeColumnNames
            };
            Templates.Add(template);
        }

        SaveTemplates(Templates.ToList());
        NewTemplateName = string.Empty;
    }

    [RelayCommand]
    private void LoadTemplate(ColumnTemplate template)
    {
        if (template == null) return;

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

    [RelayCommand]
    private void DeleteTemplate(ColumnTemplate template)
    {
        if (template == null) return;
        Templates.Remove(template);
        SaveTemplates(Templates.ToList());
        if (SelectedTemplate == template)
        {
            SelectedTemplate = null;
        }
    }

    private List<ColumnTemplate> LoadTemplates()
    {
        try
        {
            if (File.Exists(TemplatesFilePath))
            {
                var json = File.ReadAllText(TemplatesFilePath);
                return JsonSerializer.Deserialize<List<ColumnTemplate>>(json) ?? new List<ColumnTemplate>();
            }
        }
        catch (Exception)
        {
        }
        return new List<ColumnTemplate>();
    }

    private void SaveTemplates(List<ColumnTemplate> templates)
    {
        try
        {
            var directory = Path.GetDirectoryName(TemplatesFilePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
            var json = JsonSerializer.Serialize(templates, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(TemplatesFilePath, json);
        }
        catch (Exception)
        {
        }
    }
}

public class ColumnTemplate
{
    public string Name { get; set; } = string.Empty;
    public List<string> ColumnNames { get; set; } = new();
}
