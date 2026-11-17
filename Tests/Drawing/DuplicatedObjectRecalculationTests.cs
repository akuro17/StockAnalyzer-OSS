using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Serialization;
using StockAnalyzer.Avalonia.Services.Drawing;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Core.Drawing.Values;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Drawing;
using StockAnalyzer.Core.Services;
using Xunit;
using Point = Avalonia.Point;

namespace StockAnalyzer.Tests.Drawing;

/// <summary>
/// A duplicated / pasted / copied drawing object is a new instance whose candle-dependent derived state is empty.
/// These tests pin that the copy shows the same candle-dependent values as a freshly drawn object, not the empty state.
/// </summary>
public class DuplicatedObjectRecalculationTests
{
    private static readonly DateTime FirstBar = new(2025, 1, 6, 12, 0, 0);
    private const int BarCountInSeries = 120;
    private const long VolumePerBar = 1_000L;

    private sealed class InMemoryClipboardService : IClipboardService
    {
        public string? Content { get; set; }

        public Task SetTextAsync(string text)
        {
            Content = text;
            return Task.CompletedTask;
        }

        public Task<string?> GetTextAsync() => Task.FromResult(Content);
    }

    /// <summary>Same transform as the clipboard tests: 10 px per day from 2025-01-01, price = 800 - y.</summary>
    private sealed class DayTransform : ICoordinateTransform
    {
        public double CanvasWidth => 1000;
        public double CanvasHeight => 800;
        public Rect ScreenRect => new Rect(0, 0, 1000, 800);
        public double ViewportX => 0;
        public double ViewportWidth => 1000;
        public double ScaleX => 1.0;
        public PriceScaleType PriceScale => PriceScaleType.Linear;
        public TransformMetadata Metadata => new TransformMetadata(false, true, ChartType.Line);
        public IReadOnlyList<DateTime>? TimeMap => null;

        public Point ChartToScreen(ChartPoint chartPoint)
            => new((chartPoint.Time - new DateTime(2025, 1, 1)).TotalDays * 10.0, 800.0 - (double)chartPoint.Price);

        public ChartPoint ScreenToChart(Point screenPoint)
            => new(new DateTime(2025, 1, 1).AddDays(screenPoint.X / 10.0), (decimal)(800.0 - screenPoint.Y));

        public Point NumericToScreen(double x, double y) => new(x, y);
        public (double x, double y) ScreenToNumeric(Point screenPoint) => (screenPoint.X, screenPoint.Y);
        public void UpdateRange(DateTime minTime, DateTime maxTime, decimal minPrice, decimal maxPrice, double? newCanvasWidth = null, double? newCanvasHeight = null) { }
        public void SetTimeMap(IReadOnlyList<DateTime> timeMap) { }
        public double GetXFromIndex(double index) => index;
        public double GetYFromPrice(decimal price) => 800.0 - (double)price;
    }

    /// <summary>One bar per calendar day (weekends included, so the series always has bars inside any range under test).</summary>
    private static CoreCandleData[] CreateDailyCandles()
    {
        var candles = new CoreCandleData[BarCountInSeries];
        for (int i = 0; i < candles.Length; i++)
        {
            decimal close = 100m + i;
            candles[i] = new CoreCandleData(FirstBar.AddDays(i), close - 1m, close + 2m, close - 2m, close, VolumePerBar);
        }

        return candles;
    }

    /// <summary>A symbol no other test uses: the drawing session store is keyed by symbol and shared by parallel test classes.</summary>
    private static string UniqueSymbol() => "DUPREC" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    private static ChartViewModel CreateChartWithCandles(CoreCandleData[] candles)
    {
        var vm = new ChartViewModel { Symbol = UniqueSymbol() };
        vm.UpdateDrawingCoordinator();
        vm.Candles = candles;
        return vm;
    }

    private static DatePriceRangeObject AddRecalculatedRange(ChartViewModel vm, CoreCandleData[] candles)
    {
        var range = new DatePriceRangeObject(
            new ChartPoint(new DateTime(2025, 1, 10, 9, 0, 0), 100m),
            new ChartPoint(new DateTime(2025, 1, 24, 9, 0, 0), 130m));
        vm.ObjectManager.AddObject(range);
        range.Recalculate(candles);
        return range;
    }

    /// <summary>The values a correctly recalculated object must show for its own points (the pure Core calculation is the oracle).</summary>
    private static void AssertShowsRecalculatedValues(DatePriceRangeObject obj, CoreCandleData[] candles)
    {
        var expected = DatePriceRangeValues.Calculate(
            obj.Points[0].ToAnchor(),
            obj.Points[1].ToAnchor(),
            candles,
            new IndicatorColor(obj.Color.A, obj.Color.R, obj.Color.G, obj.Color.B));

        // Guards against a vacuous pass: the range under test really contains bars.
        Assert.True(expected[2].NumericValue > 0m, "the range under test must contain bars");

        var actual = obj.GetCalculatedValues(DateTime.UtcNow);
        Assert.Equal(expected[2].NumericValue, actual[2].NumericValue); // BarCount
        Assert.Equal(expected[4].NumericValue, actual[4].NumericValue); // TotalVolume
        Assert.Equal(expected[5].NumericValue, actual[5].NumericValue); // AverageVolume
    }

    [Fact]
    public void DuplicateSelected_DatePriceRange_ShowsRecalculatedBarCountAndVolume()
    {
        var candles = CreateDailyCandles();
        var vm = CreateChartWithCandles(candles);
        var original = AddRecalculatedRange(vm, candles);
        vm.ObjectManager.SelectObject(original.Id);

        DrawingClipboardService.DuplicateSelected(vm);

        var clone = Assert.IsType<DatePriceRangeObject>(vm.ObjectManager.Objects.Single(o => o.Id != original.Id));
        AssertShowsRecalculatedValues(clone, candles);
    }

    [Fact]
    public async Task TryPasteAsync_DatePriceRange_ShowsRecalculatedBarCountAndVolume()
    {
        var candles = CreateDailyCandles();
        var vm = CreateChartWithCandles(candles);
        var original = AddRecalculatedRange(vm, candles);
        vm.ObjectManager.SelectObject(original.Id);
        var clipboard = new InMemoryClipboardService();
        var transform = new DayTransform();

        await DrawingClipboardService.CopyAsync(
            vm.ObjectManager.SelectedObjects.ToList(),
            ChartDrawingContextType.Standard,
            vm.Symbol,
            vm.SelectedTimeFrame,
            vm.ObjectManager,
            clipboard);

        // Paste 30 days later than the original, still inside the candle series.
        bool success = await DrawingClipboardService.TryPasteAsync(vm, new Point(500, 300), transform, clipboard);

        Assert.True(success);
        var pasted = Assert.IsType<DatePriceRangeObject>(vm.ObjectManager.Objects.Single(o => o.Id != original.Id));
        AssertShowsRecalculatedValues(pasted, candles);
    }

    [Fact]
    public void DrawingObjectItemViewModelCopy_DatePriceRange_ShowsRecalculatedBarCountAndVolume()
    {
        var candles = CreateDailyCandles();
        var vm = CreateChartWithCandles(candles);
        var original = AddRecalculatedRange(vm, candles);

        var item = new DrawingObjectItemViewModel(
            original,
            vm.ObjectManager,
            coordinatorProvider: () => vm.DrawingEditCoordinator,
            calculationContextProvider: () => vm.CreateDrawingCalculationContext());
        item.CopyCommand.Execute(null);

        var clone = Assert.IsType<DatePriceRangeObject>(vm.ObjectManager.Objects.Single(o => o.Id != original.Id));
        AssertShowsRecalculatedValues(clone, candles);
    }

    [Fact]
    public void DuplicateSelected_AnchoredVwap_CurveBelongsToTheShiftedAnchor()
    {
        var candles = CreateDailyCandles();
        var vm = CreateChartWithCandles(candles);
        var original = new AnchoredVwapObject(new ChartPoint(new DateTime(2025, 1, 10, 12, 0, 0), 110m));
        vm.ObjectManager.AddObject(original);
        original.Recalculate(candles);
        Assert.NotEmpty(original.GetCalculatedValues(DateTime.UtcNow));
        vm.ObjectManager.SelectObject(original.Id);

        DrawingClipboardService.DuplicateSelected(vm);

        var clone = Assert.IsType<AnchoredVwapObject>(vm.ObjectManager.Objects.Single(o => o.Id != original.Id));
        Assert.NotEmpty(clone.GetCalculatedValues(DateTime.UtcNow));
        // The clone is shifted: its curve must start at its own anchor, and must not be the original's list.
        Assert.NotSame(original.VwapCurve, clone.VwapCurve);
        Assert.NotEmpty(clone.VwapCurve);
        Assert.True(clone.VwapCurve[0].Time >= clone.Points[0].Time);
    }

    [Fact]
    public void DuplicateSelected_WithoutCandles_DoesNotThrowAndLeavesTheCopyEmpty()
    {
        var vm = new ChartViewModel { Symbol = UniqueSymbol() };
        vm.UpdateDrawingCoordinator();
        var range = new DatePriceRangeObject(
            new ChartPoint(new DateTime(2025, 1, 10, 9, 0, 0), 100m),
            new ChartPoint(new DateTime(2025, 1, 24, 9, 0, 0), 130m));
        vm.ObjectManager.AddObject(range);
        vm.ObjectManager.SelectObject(range.Id);

        Assert.Single(vm.ObjectManager.SelectedObjects);
        DrawingClipboardService.DuplicateSelected(vm);
        Assert.Equal(2, vm.ObjectManager.Objects.Count);

        var clone = Assert.IsType<DatePriceRangeObject>(vm.ObjectManager.Objects.Single(o => o.Id != range.Id));
        Assert.Equal(0m, clone.GetCalculatedValues(DateTime.UtcNow)[2].NumericValue);
    }
}
