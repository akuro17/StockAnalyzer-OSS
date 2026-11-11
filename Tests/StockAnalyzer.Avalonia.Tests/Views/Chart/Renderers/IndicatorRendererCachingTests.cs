using System;
using System.Collections.Generic;
using System.Linq;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Views.Chart.Renderers;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.MarketStructure;
using Xunit;
using Xunit.Abstractions;

namespace StockAnalyzer.Avalonia.Tests.Views.Chart.Renderers;

/// <summary>
/// The Market Structure Shift, line and step-line renderers keep their paints / path / dash effect / typeface between calls.
/// These tests prove that (a) a call does not depend on what the previous call drew (the cached objects carry no state over),
/// (b) the per-call width and colors are still applied, and (c) the allocation per call is measured.
/// </summary>
public class IndicatorRendererCachingTests
{
    private const int Width = 400;
    private const int Height = 200;
    private const int CandleCount = 20;
    private static readonly DateTime Start = new(2026, 1, 1);
    private static readonly TimeSpan Interval = TimeSpan.FromDays(1);
    private readonly ITestOutputHelper _output;

    public IndicatorRendererCachingTests(ITestOutputHelper output) => _output = output;

    private static List<CoreCandleData> Candles() =>
        Enumerable.Range(0, CandleCount)
            .Select(i => new CoreCandleData(Start.AddDays(i), 100 + i, 105 + i, 95 + i, 102 + i, 1000))
            .ToList();

    private static GenericCoordinateTransform Transform()
    {
        var transform = new GenericCoordinateTransform(ChartAxisMode.Time, Width, Height);
        transform.UpdateRange(Start, Start.AddDays(CandleCount), 90m, 130m);
        return transform;
    }

    private static CoreIndicatorSettings Settings(double thickness, IndicatorColor color) =>
        new() { Thickness = thickness, Color = color };

    private static byte[] Draw(Action<SKCanvas> draw)
    {
        using var bitmap = new SKBitmap(Width, Height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Black);
        draw(canvas);
        return bitmap.Bytes;
    }

    private static List<MarketStructureShift> Shifts() => new()
    {
        new MarketStructureShift(MarketStructureType.BullishBOS, 10, Start.AddDays(10), 112m, 110m, 4, Start.AddDays(4), 98m, 2, Start.AddDays(2)),
        new MarketStructureShift(MarketStructureType.BearishCHoCH, 15, Start.AddDays(15), 100m, 120m, 8, Start.AddDays(8), 104m, 12, Start.AddDays(12)),
    };

    private static void DrawMarketStructure(MarketStructureShiftRenderer renderer, SKCanvas canvas, CoreIndicatorSettings setting,
        GenericCoordinateTransform transform, List<CoreCandleData> candles, List<MarketStructureShift> shifts) =>
        renderer.Render(canvas, new global::Avalonia.Rect(0, 0, Width, Height), shifts, setting, 90m, 130m, transform, candles, Interval);

    [Fact]
    public void MarketStructureShift_RepeatedCallsDrawIdenticalPixels_AndWidthAndColorFollowTheSettings()
    {
        var transform = Transform();
        var candles = Candles();
        var shifts = Shifts();
        var thin = Settings(1.0, new IndicatorColor(255, 255, 0, 0));
        var thick = Settings(4.0, new IndicatorColor(255, 0, 0, 255));
        using var renderer = new MarketStructureShiftRenderer();

        byte[] first = Draw(c => DrawMarketStructure(renderer, c, thin, transform, candles, shifts));
        byte[] thickOnce = Draw(c => DrawMarketStructure(renderer, c, thick, transform, candles, shifts));
        byte[] again = Draw(c => DrawMarketStructure(renderer, c, thin, transform, candles, shifts));

        Assert.Contains(first, b => b != 0);
        Assert.Equal(first, again);
        Assert.NotEqual(first, thickOnce);

        // A fresh renderer draws exactly what the long-lived one draws.
        using var fresh = new MarketStructureShiftRenderer();
        Assert.Equal(first, Draw(c => DrawMarketStructure(fresh, c, thin, transform, candles, shifts)));
    }

    [Fact]
    public void MarketStructureShift_BosAndChochColorOverridesAreApplied()
    {
        var transform = Transform();
        var candles = Candles();
        var shifts = Shifts();
        using var renderer = new MarketStructureShiftRenderer();
        var plain = Settings(2.0, new IndicatorColor(255, 255, 0, 0));
        var overridden = Settings(2.0, new IndicatorColor(255, 255, 0, 0));
        overridden.SeriesColors.Add(new SeriesColorConfig { Name = "BOS", Color = new IndicatorColor(255, 0, 255, 0) });
        overridden.SeriesColors.Add(new SeriesColorConfig { Name = "CHoCH", Color = new IndicatorColor(255, 255, 255, 0) });

        byte[] a = Draw(c => DrawMarketStructure(renderer, c, plain, transform, candles, shifts));
        byte[] b = Draw(c => DrawMarketStructure(renderer, c, overridden, transform, candles, shifts));
        byte[] a2 = Draw(c => DrawMarketStructure(renderer, c, plain, transform, candles, shifts));

        Assert.NotEqual(a, b);
        Assert.Equal(a, a2);
    }

    private static List<decimal?> Values() => Enumerable.Range(0, CandleCount).Select(i => (decimal?)(100 + i % 7 * 3)).ToList();

    [Fact]
    public void LineIndicator_PathIsResetBetweenCalls()
    {
        var transform = Transform();
        var candles = Candles();
        var values = Values();
        var short1 = values.Take(8).ToList();
        var setting = Settings(2.0, new IndicatorColor(255, 255, 0, 0));
        using var renderer = new LineIndicatorRenderer();
        var area = new global::Avalonia.Rect(0, 0, Width, Height);

        byte[] Run(IReadOnlyList<decimal?> v) => Draw(c => renderer.Render(c, area, v, setting, 90m, 130m, CandleCount, 0, 0, candles, Interval, transform));

        byte[] full1 = Run(values);
        byte[] partial = Run(short1);   // a shorter line after a longer one: nothing of the longer path may remain
        byte[] full2 = Run(values);

        Assert.Equal(full1, full2);
        Assert.NotEqual(full1, partial);
        using var fresh = new LineIndicatorRenderer();
        Assert.Equal(partial, Draw(c => fresh.Render(c, area, short1, setting, 90m, 130m, CandleCount, 0, 0, candles, Interval, transform)));
    }

    [Fact]
    public void StepLine_PathIsResetBetweenCalls()
    {
        var transform = Transform();
        var candles = Candles();
        var values = Values();
        var short1 = values.Take(8).ToList();
        var setting = Settings(2.0, new IndicatorColor(255, 255, 0, 0));
        using var renderer = new StepLineRenderer();
        var area = new global::Avalonia.Rect(0, 0, Width, Height);

        byte[] Run(IReadOnlyList<decimal?> v) => Draw(c => renderer.Render(c, area, v, setting, 90m, 130m, CandleCount, 0, 0, candles, Interval, transform));

        byte[] full1 = Run(values);
        byte[] partial = Run(short1);
        byte[] full2 = Run(values);

        Assert.Equal(full1, full2);
        Assert.NotEqual(full1, partial);
        using var fresh = new StepLineRenderer();
        Assert.Equal(partial, Draw(c => fresh.Render(c, area, short1, setting, 90m, 130m, CandleCount, 0, 0, candles, Interval, transform)));
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    [InlineData(3, 2)]
    [InlineData(4, 3)]
    [InlineData(-1, 4)]
    [InlineData(-2, 5)]
    [InlineData(-3, 6)]
    [InlineData(-4, 7)]
    public void GranvilleSignal_MapsToItsSlot(int signal, int expectedSlot)
    {
        Assert.True(GranvilleLawRenderer.TrySlotOf(signal, out int slot));
        Assert.Equal(expectedSlot, slot);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(-5)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public void GranvilleSignal_OutsideOneToFour_DrawsNothing(int signal)
    {
        Assert.False(GranvilleLawRenderer.TrySlotOf(signal, out _));
    }
}
