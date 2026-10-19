using System.Collections.Generic;
using System.Linq;
using Avalonia;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.Views.Chart;
using StockAnalyzer.Avalonia.Tests.TestHelpers;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views;

/// <summary>
/// Verifies viewport conversion caching for Renko, Point and Figure, and Kagi charts.
/// Consecutive snapshot updates with identical conversion parameters must reuse internal
/// conversion buffers, while changes to parameters or data must trigger reconversion.
/// </summary>
public class ChartDataCoordinatorSpecialChartCacheTests
{
    private static readonly Rect ControlBounds = new(0, 0, 1200, 700);

    private static ChartViewModel CreateViewModel(ChartType chartType) => new()
    {
        Symbol = "TEST",
        Candles = SwingingCandles.Create(),
        EffectiveRenkoBrickSize = 1m,
        RenkoShowMultiWavePatterns = true,
        EffectivePnfBoxSize = 1m,
        PnfReversal = 3,
        PnfShowMultiWavePatterns = true,
        KagiReversalMode = ChartSizingMode.Fixed,
        EffectiveKagiReversalAmount = 2m,
        KagiReversalAmount = 2m,
        KagiShowMultiWavePatterns = true,
        ChartType = chartType,
    };

    [Theory]
    [InlineData(ChartType.Renko)]
    [InlineData(ChartType.PointAndFigure)]
    [InlineData(ChartType.Kagi)]
    public void PanOrZoom_ReusesConversionCache(ChartType chartType)
    {
        var coordinator = new ChartDataCoordinator();
        var vm = CreateViewModel(chartType);

        // First pass: converts data and populates cache
        coordinator.UpdateSnapshot(vm, ControlBounds, suppressMessages: true);
        var snapshot1 = vm.CurrentSnapshot;
        Assert.NotNull(snapshot1);
        Assert.NotNull(snapshot1.AllCandles);
        int originalCount = snapshot1.AllCandles.Count;
        Assert.True(originalCount > 0);

        // Second pass: simulated pan/zoom without changing source candles or parameters
        coordinator.UpdateSnapshot(vm, ControlBounds, suppressMessages: true);
        var snapshot2 = vm.CurrentSnapshot;
        Assert.NotNull(snapshot2);
        Assert.NotNull(snapshot2.AllCandles);

        // Must produce identical converted candle counts
        Assert.Equal(originalCount, snapshot2.AllCandles.Count);

        // MultiWaveSignals must be the exact cached instance
        Assert.NotNull(snapshot1.MultiWaveSignals);
        Assert.Same(snapshot1.MultiWaveSignals, snapshot2.MultiWaveSignals);

        // For P&F and Kagi, ElementDates must match in content
        if (chartType is ChartType.PointAndFigure or ChartType.Kagi)
        {
            Assert.NotNull(snapshot1.ElementDates);
            Assert.Equal(snapshot1.ElementDates, snapshot2.ElementDates);
        }

        // For Kagi, ConvertedKagiCandles buffer reference must be reused
        if (chartType == ChartType.Kagi)
        {
            var kagiCandles1 = vm.ConvertedKagiCandles;
            Assert.NotNull(kagiCandles1);
            Assert.Same(kagiCandles1, vm.ConvertedKagiCandles);
        }
    }

    [Fact]
    public void Renko_ParameterChange_TriggersReconversion()
    {
        var coordinator = new ChartDataCoordinator();
        var vm = CreateViewModel(ChartType.Renko);

        coordinator.UpdateSnapshot(vm, ControlBounds, suppressMessages: true);
        int count1 = vm.CurrentSnapshot!.AllCandles!.Count;
        var mwSignals1 = vm.CurrentSnapshot.MultiWaveSignals;

        // Change brick size
        vm.EffectiveRenkoBrickSize = 2m;
        coordinator.UpdateSnapshot(vm, ControlBounds, suppressMessages: true);
        int count2 = vm.CurrentSnapshot!.AllCandles!.Count;
        var mwSignals2 = vm.CurrentSnapshot.MultiWaveSignals;

        // Larger brick size must produce fewer bricks and new signals
        Assert.NotEqual(count1, count2);
        Assert.True(count2 < count1);
        Assert.NotSame(mwSignals1, mwSignals2);
    }

    [Fact]
    public void PointAndFigure_ParameterChange_TriggersReconversion()
    {
        var coordinator = new ChartDataCoordinator();
        var vm = CreateViewModel(ChartType.PointAndFigure);

        coordinator.UpdateSnapshot(vm, ControlBounds, suppressMessages: true);
        int count1 = vm.CurrentSnapshot!.AllCandles!.Count;
        var mwSignals1 = vm.CurrentSnapshot.MultiWaveSignals;

        // Change box size
        vm.EffectivePnfBoxSize = 2m;
        coordinator.UpdateSnapshot(vm, ControlBounds, suppressMessages: true);
        int count2 = vm.CurrentSnapshot!.AllCandles!.Count;
        var mwSignals2 = vm.CurrentSnapshot.MultiWaveSignals;

        Assert.NotEqual(count1, count2);
        Assert.True(count2 < count1);
        Assert.NotSame(mwSignals1, mwSignals2);
    }

    [Fact]
    public void Kagi_ParameterChange_TriggersReconversion()
    {
        var coordinator = new ChartDataCoordinator();
        var vm = CreateViewModel(ChartType.Kagi);

        coordinator.UpdateSnapshot(vm, ControlBounds, suppressMessages: true);
        int count1 = vm.CurrentSnapshot!.AllCandles!.Count;
        var mwSignals1 = vm.CurrentSnapshot.MultiWaveSignals;

        // Change reversal amount to 8m (larger than default swing reversal)
        vm.KagiReversalAmount = 8m;
        vm.EffectiveKagiReversalAmount = 8m;
        coordinator.UpdateSnapshot(vm, ControlBounds, suppressMessages: true);
        int count2 = vm.CurrentSnapshot!.AllCandles!.Count;
        var mwSignals2 = vm.CurrentSnapshot.MultiWaveSignals;

        Assert.NotEqual(count1, count2);
        Assert.True(count2 < count1);
        Assert.NotSame(mwSignals1, mwSignals2);
    }

    [Fact]
    public void ClearAllBuffers_InvalidatesCache()
    {
        var coordinator = new ChartDataCoordinator();
        var vm = CreateViewModel(ChartType.Renko);

        coordinator.UpdateSnapshot(vm, ControlBounds, suppressMessages: true);
        Assert.NotNull(vm.CurrentSnapshot!.AllCandles);
        var mw1 = vm.CurrentSnapshot.MultiWaveSignals;

        coordinator.ClearAllBuffers();

        // Next snapshot repopulates the buffer with a fresh reconversion
        coordinator.UpdateSnapshot(vm, ControlBounds, suppressMessages: true);
        Assert.NotNull(vm.CurrentSnapshot!.AllCandles);
        Assert.True(vm.CurrentSnapshot.AllCandles.Count > 0);
        var mw2 = vm.CurrentSnapshot.MultiWaveSignals;
        Assert.NotSame(mw1, mw2);
    }
}
