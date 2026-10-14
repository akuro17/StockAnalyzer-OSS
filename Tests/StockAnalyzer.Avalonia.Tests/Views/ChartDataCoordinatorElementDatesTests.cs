using System;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.Views.Chart;
using StockAnalyzer.Avalonia.Tests.TestHelpers;
using StockAnalyzer.Core.Models;
using StockAnalyzer.ZeroAllocation;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views;

/// <summary>
/// The Data tab shows the reversal and update dates of Kagi and Point and Figure charts, so the snapshot has to carry the creation and last-update dates of
/// every drawn element, in the order and count of <see cref="ChartDataSnapshot.AllCandles"/>, from whichever producer drew the chart (converter or adapter).
/// </summary>
public class ChartDataCoordinatorElementDatesTests
{
    private static readonly Rect ControlBounds = new(0, 0, 1200, 700);

    private static ChartViewModel NewViewModel(ChartType chartType) => new()
    {
        Candles = SwingingCandles.Create(),
        EffectiveRenkoBrickSize = 1m,
        EffectivePnfBoxSize = 1m,
        EffectiveKagiReversalAmount = 2m,
        KagiReversalAmount = 2m,
        ChartType = chartType,
    };

    private static ChartDataSnapshot Snapshot(ChartViewModel vm)
    {
        new ChartDataCoordinator().UpdateSnapshot(vm, ControlBounds, suppressMessages: true);
        return vm.CurrentSnapshot!;
    }

    private static ZeroAllocCandleData[] ZeroAlloc(ChartViewModel vm) =>
        vm.Candles.Select(c => new ZeroAllocCandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, (long)c.Volume)).ToArray();

    [Theory]
    [InlineData(ChartType.Kagi)]
    [InlineData(ChartType.PointAndFigure)]
    public void Converter_Snapshot_CarriesOneDatePairPerElement(ChartType chartType)
    {
        var snapshot = Snapshot(NewViewModel(chartType));

        Assert.NotNull(snapshot.AllCandles);
        Assert.True(snapshot.AllCandles!.Count > 40);
        Assert.NotNull(snapshot.ElementDates);
        Assert.Equal(snapshot.AllCandles.Count, snapshot.ElementDates!.Count);
        for (int i = 0; i < snapshot.AllCandles.Count; i++)
        {
            Assert.True(snapshot.ElementDates[i].Start <= snapshot.ElementDates[i].End, $"{chartType}[{i}]: Start is after End");
            // The converters stamp an element with the date of its last update.
            Assert.Equal(snapshot.AllCandles[i].Timestamp, snapshot.ElementDates[i].End);
        }
    }

    [Fact]
    public void Converter_Snapshot_StartsOfKagiElementsDoNotDecrease()
    {
        var dates = Snapshot(NewViewModel(ChartType.Kagi)).ElementDates!;

        for (int i = 1; i < dates.Count; i++)
        {
            Assert.True(dates[i].Start >= dates[i - 1].Start, $"Kagi[{i}]: chronological order broken");
        }
    }

    [Theory]
    [InlineData(ChartType.Renko)]
    [InlineData(ChartType.ThreeLineBreak)]
    [InlineData(ChartType.Candlestick)]
    public void OtherCharts_Snapshot_HasNoElementDates(ChartType chartType)
    {
        Assert.Null(Snapshot(NewViewModel(chartType)).ElementDates);
    }

    [Fact]
    public void KagiAdapter_Snapshot_CarriesTheDatesOfTheAdapter()
    {
        var vm = NewViewModel(ChartType.Kagi);
        var adapter = KagiCalculator.Calculate(ZeroAlloc(vm), new KagiParameters(2m));
        vm.KagiDataAdapter = adapter;

        var snapshot = Snapshot(vm);

        Assert.True(adapter.Count > 40);
        Assert.Equal(adapter.Count, snapshot.AllCandles!.Count);
        Assert.Equal(adapter.ElementDates.ToArray(), snapshot.ElementDates!.ToArray());
    }

    [Fact]
    public void PnfAdapter_Snapshot_CarriesTheDatesOfTheAdapter()
    {
        var vm = NewViewModel(ChartType.PointAndFigure);
        var adapter = PointAndFigureCalculator.Calculate(ZeroAlloc(vm), new PointAndFigureParameters(1m, 3));
        vm.PnfDataAdapter = adapter;

        var snapshot = Snapshot(vm);

        Assert.True(adapter.Count > 40);
        Assert.Equal(adapter.Count, snapshot.AllCandles!.Count);
        Assert.Equal(adapter.ElementDates.ToArray(), snapshot.ElementDates!.ToArray());
    }

    [Fact]
    public void AdapterWithoutDates_Snapshot_HasNoElementDates_NotWrongOnes()
    {
        var vm = NewViewModel(ChartType.Kagi);
        var calculated = KagiCalculator.Calculate(ZeroAlloc(vm), new KagiParameters(2m));
        // The same segments, but built by a producer that supplies no dates.
        vm.KagiDataAdapter = new ChartSegmentAdapter(
            ZeroAllocChartDataType.Kagi,
            calculated.Timestamps.ToArray(), calculated.Opens.ToArray(), calculated.Highs.ToArray(), calculated.Lows.ToArray(),
            calculated.Closes.ToArray(), calculated.Volumes.ToArray(), calculated.OriginalRanges.ToArray());

        var snapshot = Snapshot(vm);

        Assert.Equal(calculated.Count, snapshot.AllCandles!.Count);
        Assert.Null(snapshot.ElementDates);
    }

    [Fact]
    public void Snapshot_KeepsAnImmutableCopy()
    {
        var vm = NewViewModel(ChartType.Kagi);
        var coordinator = new ChartDataCoordinator();
        coordinator.UpdateSnapshot(vm, ControlBounds, suppressMessages: true);
        var first = vm.CurrentSnapshot;
        var before = first.ElementDates!.ToArray();

        vm.Candles = SwingingCandles.Create(120); // the shared buffers are rebuilt with fewer elements
        coordinator.UpdateSnapshot(vm, ControlBounds, suppressMessages: true);

        Assert.Equal(before, first.ElementDates!.ToArray());
        Assert.NotEqual(before.Length, vm.CurrentSnapshot.ElementDates!.Count);
    }

    private static ElementDates[] BackingArray(ChartDataSnapshot snapshot) =>
        ImmutableCollectionsMarshal.AsArray((ImmutableArray<ElementDates>)snapshot.ElementDates!)!;

    [Theory]
    [InlineData(ChartType.Kagi)]
    [InlineData(ChartType.PointAndFigure)]
    public void UnchangedElements_ReuseTheImmutableArray_ChangedElementsGetANewOne(ChartType chartType)
    {
        var vm = NewViewModel(chartType);
        var coordinator = new ChartDataCoordinator();
        coordinator.UpdateSnapshot(vm, ControlBounds, suppressMessages: true);
        var first = BackingArray(vm.CurrentSnapshot!);

        coordinator.UpdateSnapshot(vm, ControlBounds, suppressMessages: true); // zoom, scroll and hover rebuild the snapshot with the same elements
        var second = BackingArray(vm.CurrentSnapshot!);
        vm.Candles = SwingingCandles.Create(150);
        coordinator.UpdateSnapshot(vm, ControlBounds, suppressMessages: true);
        var third = BackingArray(vm.CurrentSnapshot!);

        Assert.Same(first, second);
        Assert.NotSame(first, third);
        Assert.Equal(vm.CurrentSnapshot!.AllCandles!.Count, third.Length);
    }

    [Theory]
    [InlineData(ChartType.Kagi)]
    [InlineData(ChartType.PointAndFigure)]
    public void ClearAllBuffers_ReleasesTheImmutableArrayTheCoordinatorKeeps(ChartType chartType)
    {
        var vm = NewViewModel(chartType);
        var coordinator = new ChartDataCoordinator();
        coordinator.UpdateSnapshot(vm, ControlBounds, suppressMessages: true);
        var beforeRelease = BackingArray(vm.CurrentSnapshot!);

        coordinator.ClearAllBuffers();
        coordinator.UpdateSnapshot(vm, ControlBounds, suppressMessages: true);   // the same elements again
        var afterRelease = BackingArray(vm.CurrentSnapshot!);

        Assert.Equal(beforeRelease, afterRelease);   // same dates ...
        Assert.NotSame(beforeRelease, afterRelease); // ... in a new array: the coordinator no longer holds the old one
    }
}
