using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace StockAnalyzer.Avalonia.ViewModels.Dialogs;

/// <summary>
/// Abstract base ViewModel providing search query, category selection, and filtered items collection.
/// </summary>
/// <typeparam name="TItem">Type of item displayed in the filtered list.</typeparam>
public abstract partial class ColumnPickerViewModelBase<TItem> : ViewModelBase
{
    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private ColumnCategory _selectedCategory = ColumnCategory.All;

    public ObservableCollection<TItem> FilteredItems { get; } = new();

    partial void OnSearchQueryChanged(string value) => OnSearchQueryChangedCore(value);
    partial void OnSelectedCategoryChanged(ColumnCategory value) => OnSelectedCategoryChangedCore(value);

    protected virtual void OnSearchQueryChangedCore(string value)
    {
        UpdateFilteredItems();
    }

    protected virtual void OnSelectedCategoryChangedCore(ColumnCategory value)
    {
        UpdateFilteredItems();
    }

    /// <summary>
    /// Re-evaluates items and populates FilteredItems according to SearchQuery and SelectedCategory.
    /// </summary>
    protected abstract void UpdateFilteredItems();
}
