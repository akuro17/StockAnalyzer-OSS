using System.Collections.Generic;
using System.Collections.Specialized;
using StockAnalyzer.Avalonia.Common;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Common;

/// <summary>sa_implement (Notes メインタイムライン インフィニットスクロール Task 3,
/// Y:\Temp\sa_implementation_plan.md): covers <see cref="BulkObservableCollection{T}.AddRange"/>,
/// the append-without-Reset counterpart to the existing <see cref="BulkObservableCollection{T}.ReplaceRange"/>.
/// Infinite-scroll's requirement - appending a new page without disturbing a bound ItemsControl's
/// already-rendered items/scroll position - depends specifically on AddRange never raising a Reset,
/// so that behavior is asserted directly here rather than only exercised indirectly through a
/// ViewModel test.</summary>
public class BulkObservableCollectionTests
{
    [Fact]
    public void AddRange_AppendsItemsInOrder_AfterExistingItems()
    {
        var collection = new BulkObservableCollection<int> { 1, 2, 3 };

        collection.AddRange(new[] { 4, 5 });

        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, collection);
    }

    [Fact]
    public void AddRange_RaisesSingleAddNotification_NotReset()
    {
        var collection = new BulkObservableCollection<int> { 1, 2, 3 };
        var raised = new List<NotifyCollectionChangedEventArgs>();
        collection.CollectionChanged += (_, e) => raised.Add(e);

        collection.AddRange(new[] { 4, 5 });

        var change = Assert.Single(raised);
        Assert.Equal(NotifyCollectionChangedAction.Add, change.Action);
        Assert.Equal(3, change.NewStartingIndex);
        Assert.Equal(new object[] { 4, 5 }, change.NewItems);
    }

    [Fact]
    public void AddRange_DoesNotReplaceExistingItemInstances()
    {
        var first = new object();
        var collection = new BulkObservableCollection<object> { first };

        collection.AddRange(new object[] { new object() });

        Assert.Same(first, collection[0]);
        Assert.Equal(2, collection.Count);
    }

    [Fact]
    public void AddRange_EmptyCollection_RaisesNoNotification()
    {
        var collection = new BulkObservableCollection<int> { 1 };
        var raisedCount = 0;
        collection.CollectionChanged += (_, _) => raisedCount++;

        collection.AddRange(new int[0]);

        Assert.Equal(0, raisedCount);
        Assert.Single(collection);
    }
}
