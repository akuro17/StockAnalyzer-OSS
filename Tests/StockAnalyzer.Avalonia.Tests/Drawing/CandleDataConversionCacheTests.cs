using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

/// <summary>
/// CoreCandleData -> CandleData conversion is defined once (CandleDataConverter); the instance cache reuses the converted array
/// only while the same list (same Count, same first and last element) is passed, and never keeps the list alive.
/// </summary>
public class CandleDataConversionCacheTests
{
    private static readonly DateTime Start = new(2026, 1, 1);

    private static List<CoreCandleData> Candles(int count) =>
        Enumerable.Range(0, count)
            .Select(i => new CoreCandleData(Start.AddDays(i), 100m + i, 105m + i, 95m + i, 102.5m + i, 1000 + i))
            .ToList();

    [Fact]
    public void Converter_MapsEveryFieldAndKeepsTheOrder()
    {
        var candles = Candles(5);

        var array = CandleDataConverter.ToArray(candles);
        var lazy = CandleDataConverter.ToLazy(candles).ToArray();

        Assert.Equal(candles.Count, array.Length);
        Assert.Equal(array, lazy);
        for (int i = 0; i < candles.Count; i++)
        {
            Assert.Equal(new CandleData(candles[i].Timestamp, candles[i].Open, candles[i].High, candles[i].Low, candles[i].Close, candles[i].Volume), array[i]);
        }
    }

    [Fact]
    public void Get_ReusesTheArrayForTheSameUnchangedList()
    {
        var cache = new CandleDataConversionCache();
        var candles = Candles(10);

        var first = cache.Get(candles);
        var second = cache.Get(candles);

        Assert.Same(first, second);
        Assert.Equal(1, cache.ConversionCount);
        Assert.Equal(CandleDataConverter.ToArray(candles), first);
    }

    [Fact]
    public void Get_ReconvertsWhenCountFirstLastOrInstanceChange()
    {
        var cache = new CandleDataConversionCache();
        var candles = Candles(10);
        cache.Get(candles);

        candles.Add(new CoreCandleData(Start.AddDays(10), 1m, 2m, 0.5m, 1.5m, 1));         // Count changed (and last)
        cache.Get(candles);
        Assert.Equal(2, cache.ConversionCount);

        candles[^1] = candles[^1] with { Close = 1.7m };                                      // last element replaced
        cache.Get(candles);
        Assert.Equal(3, cache.ConversionCount);

        candles[0] = candles[0] with { Open = 99m };                                          // first element replaced
        var afterFirst = cache.Get(candles);
        Assert.Equal(4, cache.ConversionCount);
        Assert.Equal(99m, ((CandleData[])afterFirst)[0].Open);

        cache.Get(candles.ToList());                                                          // other list instance, same content
        Assert.Equal(5, cache.ConversionCount);
    }

    [Fact]
    public void Invalidate_ForcesReconversion()
    {
        var cache = new CandleDataConversionCache();
        var candles = Candles(4);
        cache.Get(candles);

        cache.Invalidate();
        cache.Get(candles);

        Assert.Equal(2, cache.ConversionCount);
    }

    [Fact]
    public void Get_ReturnsAnEmptySequenceForAnEmptyListAndDoesNotCacheIt()
    {
        var cache = new CandleDataConversionCache();

        Assert.Empty(cache.Get(new List<CoreCandleData>()));
        Assert.Equal(0, cache.ConversionCount);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference FillCacheWithShortLivedList(CandleDataConversionCache cache)
    {
        var list = Candles(6);
        cache.Get(list);
        return new WeakReference(list);
    }

    [Fact]
    public void Cache_DoesNotKeepTheSourceListAlive()
    {
        var cache = new CandleDataConversionCache();
        var weak = FillCacheWithShortLivedList(cache);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(weak.IsAlive);
    }

    [Fact]
    public void TryRecalculate_WithCache_ConvertsOnceForRepeatedHarmonicAndElliottCalls_AndGivesTheSameResults()
    {
        var candles = Candles(60);
        var harmonicCached = NewHarmonic();
        var harmonicPlain = NewHarmonic();
        var elliottCached = new AutoElliottWaveObject();
        var cache = new CandleDataConversionCache();

        Assert.True(DeferredComputationRecalculator.TryRecalculate(harmonicCached, candles, cache));
        Assert.True(DeferredComputationRecalculator.TryRecalculate(harmonicCached, candles, cache));
        Assert.True(DeferredComputationRecalculator.TryRecalculate(elliottCached, candles, cache));
        Assert.True(DeferredComputationRecalculator.TryRecalculate(harmonicPlain, candles));

        Assert.Equal(1, cache.ConversionCount);
        Assert.Equal(harmonicPlain.CachedResults.Count, harmonicCached.CachedResults.Count);
    }

    [Fact]
    public void TryRecalculate_WithCache_LeavesOtherObjectTypesToTheExistingPathAndDoesNotConvert()
    {
        var candles = Candles(30);
        var regression = new RegressionTrendObject(new ChartPoint(Start.AddDays(2), 100m), new ChartPoint(Start.AddDays(20), 120m));
        var cache = new CandleDataConversionCache();

        Assert.True(DeferredComputationRecalculator.TryRecalculate(regression, candles, cache));
        Assert.Equal(0, cache.ConversionCount);
        Assert.False(DeferredComputationRecalculator.TryRecalculate(new VerticalLineObject(new ChartPoint(Start, 1m)), candles, cache));
        Assert.True(DeferredComputationRecalculator.TryRecalculate(regression, candles, null));
    }

    private static HarmonicPatternObject NewHarmonic()
    {
        var obj = new HarmonicPatternObject();
        obj.Points.Add(new ChartPoint(Start.AddDays(5), 100m));
        obj.Points.Add(new ChartPoint(Start.AddDays(50), 120m));
        return obj;
    }
}
