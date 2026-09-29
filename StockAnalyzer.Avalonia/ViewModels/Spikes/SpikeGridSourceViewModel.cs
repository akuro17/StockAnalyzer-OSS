using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace StockAnalyzer.Avalonia.ViewModels.Spikes;

public class SpikeItem
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal Value { get; set; }
    public string Category { get; set; } = string.Empty;
}

public partial class SpikeGridSourceViewModel : ObservableObject
{
    private readonly List<SpikeItem> _allItems;
    
    [ObservableProperty]
    private ObservableCollection<SpikeItem> _items = new();

    public SpikeGridSourceViewModel(IEnumerable<SpikeItem>? initialItems = null)
    {
        _allItems = initialItems?.ToList() ?? new List<SpikeItem>();
        RefreshItems();
    }

    public void AddItem(SpikeItem item)
    {
        _allItems.Add(item);
        RefreshItems();
    }

    public void Sort(string sortKey, bool ascending)
    {
        List<SpikeItem> sorted;
        
        switch (sortKey)
        {
            case nameof(SpikeItem.Id):
                sorted = ascending ? _allItems.OrderBy(x => x.Id).ToList() : _allItems.OrderByDescending(x => x.Id).ToList();
                break;
            case nameof(SpikeItem.Name):
                sorted = ascending ? _allItems.OrderBy(x => x.Name).ToList() : _allItems.OrderByDescending(x => x.Name).ToList();
                break;
            case nameof(SpikeItem.Value):
                sorted = ascending ? _allItems.OrderBy(x => x.Value).ToList() : _allItems.OrderByDescending(x => x.Value).ToList();
                break;
            case nameof(SpikeItem.Category):
                sorted = ascending ? _allItems.OrderBy(x => x.Category).ToList() : _allItems.OrderByDescending(x => x.Category).ToList();
                break;
            default:
                sorted = _allItems.ToList();
                break;
        }

        UpdateItems(sorted);
    }

    public void Filter(string category)
    {
        var filtered = _allItems.Where(x => x.Category == category).ToList();
        UpdateItems(filtered);
    }
    
    private void UpdateItems(List<SpikeItem> newItems)
    {
        Items.Clear();
        foreach (var item in newItems)
        {
            Items.Add(item);
        }
    }

    private void RefreshItems()
    {
        UpdateItems(_allItems);
    }
}
