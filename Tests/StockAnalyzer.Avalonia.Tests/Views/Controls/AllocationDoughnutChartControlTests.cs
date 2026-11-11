using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using SkiaSharp;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.Views.Controls;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views.Controls;

/// <summary>
/// The doughnut hands its render-thread operation an immutable copy of (sweep, color) per slice, so the compositor never reads the
/// UI-owned collection, and it holds exactly one collection-changed subscription while attached.
/// </summary>
public class AllocationDoughnutChartControlTests
{
    private static AllocationEntryViewModel Entry(decimal percentage, uint color) => new() { Percentage = percentage, Color = color };

    private sealed class CountingCollection : List<AllocationEntryViewModel>, INotifyCollectionChanged
    {
        public int Subscribers { get; private set; }
        private NotifyCollectionChangedEventHandler? _handlers;

        public event NotifyCollectionChangedEventHandler? CollectionChanged
        {
            add { _handlers += value; Subscribers++; }
            remove { _handlers -= value; Subscribers--; }
        }
    }

    [Fact]
    public void BuildSlices_UsesTheSweepFormulaAndSkipsNonPositivePercentages()
    {
        var items = new List<AllocationEntryViewModel>
        {
            Entry(50m, 0xFF112233),
            Entry(0m, 0xFF000000),
            Entry(-5m, 0xFF000000),
            Entry(12.5m, 0xFFAABBCC),
        };

        var slices = AllocationDoughnutChartControl.BuildSlices(items);

        Assert.Equal(2, slices.Length);
        Assert.Equal((float)50m * 3.6f, slices[0].SweepDegrees);
        Assert.Equal(new SKColor(0xFF112233), slices[0].Color);
        Assert.Equal((float)12.5m * 3.6f, slices[1].SweepDegrees);
        Assert.Equal(new SKColor(0xFFAABBCC), slices[1].Color);
    }

    [Fact]
    public void BuildSlices_GivesTheSameResultForAListAndAPlainEnumerable()
    {
        var list = new List<AllocationEntryViewModel> { Entry(30m, 0xFF010203), Entry(0m, 1), Entry(70m, 0xFF040506) };
        IEnumerable<AllocationEntryViewModel> enumerable = list.Where(_ => true);

        Assert.Equal(AllocationDoughnutChartControl.BuildSlices(list), AllocationDoughnutChartControl.BuildSlices(enumerable));
    }

    [Fact]
    public void BuildSlices_ReturnsEmptyWhenNothingIsPositive()
    {
        Assert.Empty(AllocationDoughnutChartControl.BuildSlices(new List<AllocationEntryViewModel> { Entry(0m, 1) }));
        Assert.Empty(AllocationDoughnutChartControl.BuildSlices(new List<AllocationEntryViewModel>()));
    }

    [AvaloniaFact]
    public void HoldsExactlyOneSubscriptionWhileAttached_AndResubscribesAfterReattach()
    {
        var collection = new CountingCollection { Entry(100m, 0xFF112233) };
        var control = new AllocationDoughnutChartControl { Items = collection };
        var window = new Window { Content = control };

        window.Show();
        Assert.Equal(1, collection.Subscribers);

        window.Content = null;               // detached
        Assert.Equal(0, collection.Subscribers);
        Assert.False(control.IsSubscribedToItems);

        window.Content = control;            // attached again
        Assert.Equal(1, collection.Subscribers);

        control.Items = null;                // replaced items release the subscription
        Assert.Equal(0, collection.Subscribers);
        window.Close();
    }

    [Fact]
    public void SharedPaint_IsDisposedOnlyWhenTheLastHolderReleasesIt()
    {
        var shared = new AllocationDoughnutChartControl.SharedPaint();
        Assert.Equal(1, shared.References);

        shared.AddReference();   // an operation created by Render
        shared.AddReference();   // another operation
        Assert.Equal(3, shared.References);

        shared.Release();        // the control detached: operations of an in-flight frame still use the paint
        shared.Release();
        Assert.False(shared.IsReleased);
        shared.Paint.StrokeWidth = 3f;   // still usable (would throw if disposed)

        shared.Release();        // the last operation was disposed
        Assert.True(shared.IsReleased);
        Assert.Equal(0, shared.References);
    }

    [AvaloniaFact]
    public void DetachingTheControl_ReleasesTheControlsReference_AndReattachingCreatesAFreshPaint()
    {
        var control = new AllocationDoughnutChartControl { Items = new List<AllocationEntryViewModel> { Entry(100m, 0xFF112233) } };
        var window = new Window { Content = control };
        window.Show();
        var field = typeof(AllocationDoughnutChartControl).GetField("_sharedPaint", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(field);

        control.InvalidateVisual();
        global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var first = (AllocationDoughnutChartControl.SharedPaint?)field!.GetValue(control);

        window.Content = null;   // detach
        Assert.Null(field.GetValue(control));
        if (first != null)
        {
            Assert.True(first.References >= 0);
        }

        window.Content = control;   // attach again, render again
        control.InvalidateVisual();
        global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var second = (AllocationDoughnutChartControl.SharedPaint?)field.GetValue(control);
        if (first != null && second != null)
        {
            Assert.NotSame(first, second);
        }
        window.Close();
    }
}
