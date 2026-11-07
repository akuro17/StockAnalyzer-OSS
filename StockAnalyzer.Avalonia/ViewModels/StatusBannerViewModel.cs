using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;

namespace StockAnalyzer.Avalonia.ViewModels;

/// <summary>How strongly a banner item asks for attention.</summary>
public enum StatusBannerSeverity
{
    /// <summary>Something is limited or protected; the app keeps working.</summary>
    Warning,

    /// <summary>Something failed.</summary>
    Error
}

/// <summary>A button on a banner item.</summary>
public sealed record StatusBannerAction(string Text, ICommand Command);

/// <summary>
/// One message of the status banner. <paramref name="Id"/> identifies the message so that showing it again replaces it
/// instead of stacking duplicates.
/// </summary>
public sealed record StatusBannerItem(
    string Id,
    StatusBannerSeverity Severity,
    string Text,
    IReadOnlyList<StatusBannerAction> Actions,
    string? ToolTip = null)
{
    public bool IsError => Severity == StatusBannerSeverity.Error;
}

/// <summary>
/// The persistent banner row below the chart area. Several items can be shown at once (workspace restore problems today,
/// a read-only storage notice later). A child view model: it knows nothing about its owner, which pushes items in and removes them.
/// All members must be used on the UI thread.
/// </summary>
public sealed partial class StatusBannerViewModel : ObservableObject
{
    /// <summary>Items in the order they first appeared.</summary>
    public ObservableCollection<StatusBannerItem> Items { get; } = new();

    public bool HasItems => Items.Count > 0;

    public StatusBannerViewModel()
    {
        Items.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasItems));
    }

    /// <summary>Adds the item, or replaces the shown item with the same <see cref="StatusBannerItem.Id"/> in place.</summary>
    public void Show(StatusBannerItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var index = IndexOf(item.Id);
        if (index < 0)
        {
            Items.Add(item);
        }
        else
        {
            Items[index] = item;
        }
    }

    /// <summary>Removes the item with the given id. Returns false when none is shown.</summary>
    public bool Remove(string id)
    {
        var index = IndexOf(id);
        if (index < 0)
        {
            return false;
        }

        Items.RemoveAt(index);
        return true;
    }

    private int IndexOf(string id)
    {
        for (var i = 0; i < Items.Count; i++)
        {
            if (string.Equals(Items[i].Id, id, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }
}
