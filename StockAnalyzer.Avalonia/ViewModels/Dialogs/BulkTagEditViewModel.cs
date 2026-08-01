using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace StockAnalyzer.Avalonia.ViewModels.Dialogs;

public partial class BulkTagEditViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _bulkTagInput = string.Empty;

    public ObservableCollection<string> ExistingTags { get; }

    public BulkTagEditViewModel(IEnumerable<string> existingTags)
    {
        ExistingTags = new ObservableCollection<string>(existingTags ?? Enumerable.Empty<string>());
    }

    [RelayCommand]
    private void SelectTag(string? tag)
    {
        if (string.IsNullOrEmpty(tag)) return;
        
        if (string.IsNullOrWhiteSpace(BulkTagInput))
        {
            BulkTagInput = tag;
        }
        else
        {
            var existing = BulkTagInput.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim())
                .ToList();
                
            if (!existing.Contains(tag, StringComparer.OrdinalIgnoreCase))
            {
                existing.Add(tag);
                BulkTagInput = string.Join(", ", existing);
            }
        }
    }
}
