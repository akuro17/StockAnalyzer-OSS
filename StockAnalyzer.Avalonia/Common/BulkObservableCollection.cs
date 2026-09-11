using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;

namespace StockAnalyzer.Avalonia.Common;

/// <summary>
/// An ObservableCollection that supports bulk updates with suppressed notifications.
/// </summary>
/// <typeparam name="T">The type of elements in the collection.</typeparam>
public class BulkObservableCollection<T> : ObservableCollection<T>
{
    private bool _suppressNotification;
    private int _suspendCount;

    /// <summary>
    /// Suspends CollectionChanged notifications until EndBulkUpdate is called.
    /// </summary>
    public void BeginBulkUpdate()
    {
        _suspendCount++;
        _suppressNotification = true;
    }

    /// <summary>
    /// Resumes CollectionChanged notifications and triggers a Reset if notifications were suspended.
    /// </summary>
    public void EndBulkUpdate()
    {
        if (_suspendCount > 0)
        {
            _suspendCount--;
            if (_suspendCount == 0)
            {
                _suppressNotification = false;
                OnCountPropertyChanged();
                OnIndexerPropertyChanged();
                OnCollectionReset();
            }
        }
    }

    /// <summary>
    /// Replaces the current items in the collection with the specified collection.
    /// Triggers a single Reset notification at the end.
    /// </summary>
    /// <param name="collection">The collection of items to replace with.</param>
    public void ReplaceRange(IEnumerable<T> collection)
    {
        if (collection == null) throw new ArgumentNullException(nameof(collection));

        BeginBulkUpdate();
        try
        {
            Items.Clear();
            foreach (var item in collection)
            {
                Items.Add(item);
            }
        }
        finally
        {
            EndBulkUpdate();
        }
    }

    /// <summary>
    /// Appends items to the end of the collection and raises a single Add notification (not Reset)
    /// carrying every appended item and its starting index. Unlike <see cref="ReplaceRange"/> (which
    /// clears and rebuilds everything), existing items and their bound container instances are left
    /// untouched - callers that append to an already-visible, scroll-position-sensitive list (e.g. an
    /// infinite-scroll timeline) need this to avoid the full visual rebuild and scroll-offset reset a
    /// Reset notification would otherwise force onto a bound ItemsControl.
    /// </summary>
    /// <param name="collection">The items to append.</param>
    public void AddRange(IEnumerable<T> collection)
    {
        if (collection == null) throw new ArgumentNullException(nameof(collection));

        var startIndex = Items.Count;
        var newItems = collection.ToList();
        if (newItems.Count == 0)
        {
            return;
        }

        foreach (var item in newItems)
        {
            Items.Add(item);
        }

        OnCountPropertyChanged();
        OnIndexerPropertyChanged();
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, newItems, startIndex));
    }

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (!_suppressNotification)
        {
            base.OnCollectionChanged(e);
        }
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        if (!_suppressNotification)
        {
            base.OnPropertyChanged(e);
        }
    }

    private void OnCountPropertyChanged() => OnPropertyChanged(new PropertyChangedEventArgs("Count"));
    private void OnIndexerPropertyChanged() => OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
    private void OnCollectionReset() => OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
}
