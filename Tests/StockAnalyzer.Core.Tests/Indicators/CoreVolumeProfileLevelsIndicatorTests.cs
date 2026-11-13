using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Chart;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.Core.Tests.Analysis;
using StockAnalyzer.Core.Tests.Common;
using Xunit;
using Xunit.Abstractions;

namespace StockAnalyzer.Core.Tests.Indicators;

/// <summary>WebAiExpansion plan 03 T7 (D-04=A): causal POC / VAH / VAL series, one profile of the last Period bars per bar.</summary>
public class CoreVolumeProfileLevelsIndicatorTests
{
    private readonly ITestOutputHelper _output;

    public CoreVolumeProfileLevelsIndicatorTests(ITestOutputHelper output) => _output = output;

    private static CoreCandleData Bar(int day, decimal low, decimal high, long volume) =>
        new(new DateTime(2024, 1, 1).AddDays(day), low, high, low, high, volume);

    private static CoreVolumeProfileLevelsIndicator Make(int period, int rowCount, VolumeDistributionMode mode = VolumeDistributionMode.Proportional, decimal valueArea = 0.70m) =>
        new() { Period = period, RowCount = rowCount, Mode = mode, ValueAreaPercentage = valueArea };

    [Fact]
    public void NoCandles_ReturnsASuccessfulEmptyResult()
    {
        var result = new CoreVolumeProfileLevelsIndicator().Calculate(new List<CoreCandleData>());

        Assert.True(result.IsSuccessful);
        Assert.Empty(result.MainValues);
    }

    [Theory]
    [InlineData(1, 200)]
    [InlineData(199, 200)]
    [InlineData(2, 5)]
    public void FewerBarsThanThePeriod_AreAllNullAndKeepTheLength(int bars, int period)
    {
        var candles = VolumeAnalysisSpanTests.RandomCandles(bars);
        var result = Make(period, 20).Calculate(candles);

        // bars = 2 with period 5, bars = 1 / 199 with period 200: no full window
        Assert.True(result.IsSuccessful);
        foreach (string name in new[] { "Main", "VAH", "VAL" })
        {
            Assert.Equal(bars, result.GetSeries(name).Count);
            Assert.All(result.GetSeries(name), v => Assert.Null(v));
        }
    }

    [Fact]
    public void HandComputedLevels()
    {
        // Window of 4 bars, 5 rows over 0..10 (row width 2): rows [0,2] [2,4] [4,6] [6,8] [8,10].
        //   A: 0..1 vol 600 -> row 0;  B: 4.5..5.5 vol 1000 -> row 2;  D: 6.5..7.5 vol 700 -> row 3;  C: 9..10 vol 200 -> row 4
        //   volumes [600, 0, 1000, 700, 200], total 2500; POC = row 2 -> price (4 + 6) / 2 = 5
        //   value area 50 %: target 1250; start 1000 at row 2; upper neighbour 700 >= lower neighbour 0 -> row 3 (1700 >= 1250) -> VAH = 8, VAL = 4
        var candles = new List<CoreCandleData> { Bar(0, 0m, 1m, 600), Bar(1, 4.5m, 5.5m, 1000), Bar(2, 6.5m, 7.5m, 700), Bar(3, 9m, 10m, 200) };

        var result = Make(4, 5, valueArea: 0.50m).Calculate(candles);

        Assert.All(result.MainValues.Take(3), v => Assert.Null(v));
        Assert.Equal(5m, result.MainValues[3]);
        Assert.Equal(8m, result.GetSeries("VAH")[3]);
        Assert.Equal(4m, result.GetSeries("VAL")[3]);
    }

    [Theory]
    [InlineData(20, 10, VolumeDistributionMode.Proportional)]
    [InlineData(50, 24, VolumeDistributionMode.Full)]
    [InlineData(200, 50, VolumeDistributionMode.Proportional)]
    public void EveryBarMatchesTheListBasedProfileOfTheLastPeriodBars(int period, int rowCount, VolumeDistributionMode mode)
    {
        var candles = VolumeAnalysisSpanTests.RandomCandles(300);
        var result = Make(period, rowCount, mode).Calculate(candles);

        for (int i = 0; i < candles.Count; i++)
        {
            if (i < period - 1)
            {
                Assert.Null(result.MainValues[i]);
                continue;
            }

            var bins = VolumeAnalysis.CalculateProfile(candles.Skip(i - period + 1).Take(period).ToList(), rowCount, mode);
            if (bins.Count == 0)
            {
                Assert.Null(result.MainValues[i]);
                Assert.Null(result.GetSeries("VAH")[i]);
                Assert.Null(result.GetSeries("VAL")[i]);
                continue;
            }

            var area = VolumeAnalysis.CalculateValueArea(bins);
            Assert.Equal(VolumeAnalysis.FindPointOfControl(bins)!.Price, result.MainValues[i]);
            Assert.Equal(area.VAH, result.GetSeries("VAH")[i]);
            Assert.Equal(area.VAL, result.GetSeries("VAL")[i]);
        }
    }

    [Fact]
    public void ABarDoesNotDependOnLaterBars()
    {
        var candles = VolumeAnalysisSpanTests.RandomCandles(300);
        var full = Make(50, 24).Calculate(candles);
        var cut = Make(50, 24).Calculate(candles.Take(180).ToList());

        foreach (string name in new[] { "Main", "VAH", "VAL" })
            for (int i = 0; i < 180; i++) Assert.Equal(full.GetSeries(name)[i], cut.GetSeries(name)[i]);
    }

    [Fact]
    public void AWindowOfZeroVolume_GivesWhatTheListBasedFunctionsGive()
    {
        var candles = Enumerable.Range(0, 12).Select(i => Bar(i, 100m + i, 110m + i, 0)).ToList();
        var result = Make(6, 10).Calculate(candles);

        var bins = VolumeAnalysis.CalculateProfile(candles.Skip(6).Take(6).ToList(), 10);
        var area = VolumeAnalysis.CalculateValueArea(bins);
        Assert.Equal(VolumeAnalysis.FindPointOfControl(bins)!.Price, result.MainValues[11]);
        Assert.Equal(area.VAH, result.GetSeries("VAH")[11]);
        Assert.Equal(area.VAL, result.GetSeries("VAL")[11]);
    }

    [Fact]
    public void AWindowOfOnePrice_IsNull()
    {
        var candles = Enumerable.Range(0, 10).Select(i => Bar(i, 100m, 100m, 1000)).ToList();

        var result = Make(5, 10).Calculate(candles);

        Assert.All(result.MainValues, v => Assert.Null(v));
        Assert.All(result.GetSeries("VAH"), v => Assert.Null(v));
    }

    [Fact]
    public void AllLevelsStayInsideTheWindowPriceRange_AndVahIsNotBelowThePocNorValBelowIt()
    {
        var candles = VolumeAnalysisSpanTests.RandomCandles(300);
        var result = Make(50, 24).Calculate(candles);

        for (int i = 49; i < candles.Count; i++)
        {
            if (result.MainValues[i] is null) continue;
            var window = candles.Skip(i - 49).Take(50).ToList();
            decimal low = window.Min(c => c.Low), high = window.Max(c => c.High);
            decimal poc = result.MainValues[i]!.Value, vah = result.GetSeries("VAH")[i]!.Value, val = result.GetSeries("VAL")[i]!.Value;
            Assert.InRange(poc, low, high);
            Assert.True(val <= poc && poc <= vah, $"bar {i}: VAL {val} POC {poc} VAH {vah}");
        }
    }

    [Fact]
    public void ALargeRowCount_UsesTheRentedArrayAndGivesTheSameLevels()
    {
        var candles = VolumeAnalysisSpanTests.RandomCandles(80);
        var indicator = Make(40, 5) ;
        indicator.RowCount = IndicatorDefaultConstants.VolumeProfileLevelsMaxStackRows + 44;

        var result = indicator.Calculate(candles);

        var bins = VolumeAnalysis.CalculateProfile(candles.Skip(40).Take(40).ToList(), indicator.RowCount);
        Assert.Equal(VolumeAnalysis.FindPointOfControl(bins)!.Price, result.MainValues[79]);
        Assert.Equal(VolumeAnalysis.CalculateValueArea(bins).VAH, result.GetSeries("VAH")[79]);
    }

    [Fact]
    public void ANonContiguousCandleList_GivesTheSameResultAsAList()
    {
        var candles = VolumeAnalysisSpanTests.RandomCandles(120);
        var fromList = Make(30, 12).Calculate(candles);
        var fromEnumerableWrapper = Make(30, 12).Calculate(new ReadOnlyCollectionWrapper(candles));

        for (int i = 0; i < candles.Count; i++) Assert.Equal(fromList.MainValues[i], fromEnumerableWrapper.MainValues[i]);
    }

    private sealed class ReadOnlyCollectionWrapper : IReadOnlyList<CoreCandleData>
    {
        private readonly List<CoreCandleData> _inner;
        public ReadOnlyCollectionWrapper(List<CoreCandleData> inner) => _inner = inner;
        public CoreCandleData this[int index] => _inner[index];
        public int Count => _inner.Count;
        public IEnumerator<CoreCandleData> GetEnumerator() => _inner.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Fact]
    public void FiveThousandBarsWithTheDefaults_AllocateNothingInTheBarLoop_AndRunFast()
    {
        var candles = VolumeAnalysisSpanTests.RandomCandles(5000);
        var indicator = new CoreVolumeProfileLevelsIndicator();
        Assert.Equal((IndicatorDefaultConstants.VolumeProfilePeriod, IndicatorDefaultConstants.VolumeProfileRowCount), (indicator.Period, indicator.RowCount));

        // A fresh instance allocates its three result lists (capacity set once) and a fixed overhead for the result object; this is the only allocation of the first call.
        long listBytes = 3L * (candles.Count * System.Runtime.CompilerServices.Unsafe.SizeOf<decimal?>() + 64);
        long firstCallBytes = MeasureBytes(() => new CoreVolumeProfileLevelsIndicator().Calculate(candles));
        _output.WriteLine($"fresh instance: {firstCallBytes} bytes, three lists {listBytes} bytes");
        Assert.True(firstCallBytes <= listBytes + SteadyStateOverheadBytes, $"allocated {firstCallBytes} bytes, expected at most {listBytes + SteadyStateOverheadBytes} (three lists + overhead)");

        // The instance reuses its lists (Clear keeps the capacity), so a second call allocates only the result object. Anything the bar loop allocated
        // (4800 bars) would show here: even 10 bytes per bar is 48 KB, above the bound.
        indicator.Calculate(candles);
        long steadyBytes = MeasureBytes(() => indicator.Calculate(candles));
        _output.WriteLine($"second call: {steadyBytes} bytes");
        Assert.True(steadyBytes <= SteadyStateOverheadBytes, $"the second call allocated {steadyBytes} bytes, expected at most {SteadyStateOverheadBytes}: something in the bar loop allocates");

        var watch = Stopwatch.StartNew();
        indicator.Calculate(candles);
        watch.Stop();
        _output.WriteLine($"Calculate(5000 bars, Period 200, RowCount 50): {watch.ElapsedMilliseconds} ms");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"{watch.ElapsedMilliseconds} ms");
    }

    /// <summary>The fixed allocation of one steady-state Calculate (result object and dictionary): measured 632 bytes; 16 KB leaves room for runtime noise but not for per-bar allocation.</summary>
    private const long SteadyStateOverheadBytes = 16 * 1024;

    /// <summary>Allocation of one call: the collection-free window probe, and the smallest-of-samples probe when every window sample overlapped a collection (as CoreRollingTailMathTests does).</summary>
    private static long MeasureBytes(Action body)
    {
        long bytes = AllocationProbe.InCollectionFreeWindow(body);
        return bytes >= 0 ? bytes : AllocationProbe.Minimum(body);
    }

    [Fact]
    public void IsCausalForTheBacktest_ButTheVolumeProfileItselfStaysListed()
    {
        Assert.DoesNotContain(IndicatorType.VolumeProfileLevels, BacktestIndicatorEligibility.NonCausalIndicatorTypes);
        Assert.DoesNotContain(IndicatorType.VolumeProfileLevels, BacktestIndicatorEligibility.SynchronousCalculationUnsupportedTypes);
        Assert.Contains(IndicatorType.VolumeProfile, BacktestIndicatorEligibility.NonCausalIndicatorTypes);
        Assert.Contains(IndicatorType.TimeAtPrice, BacktestIndicatorEligibility.NonCausalIndicatorTypes);
    }

    [Fact]
    public void IsRegistered_WithPocVahValOutputsAndNoCustomData()
    {
        Assert.True(IndicatorFactory.Default.IsRegistered(IndicatorType.VolumeProfileLevels));
        Assert.IsType<CoreVolumeProfileLevelsIndicator>(IndicatorFactory.Default.Create(IndicatorType.VolumeProfileLevels));

        var names = IndicatorOutputSeriesResolver.GetDisplayNames(IndicatorType.VolumeProfileLevels);
        Assert.Contains("POC", names);
        Assert.Contains("VAH", names);
        Assert.Contains("VAL", names);
        Assert.Equal("Main", IndicatorOutputSeriesResolver.Normalize(IndicatorType.VolumeProfileLevels, "POC"));

        var result = Make(20, 10).Calculate(VolumeAnalysisSpanTests.RandomCandles(60));
        Assert.Null(result.CustomData);
        Assert.Equal(new[] { "Main", "VAH", "VAL" }, result.SeriesNamesList.OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void DefaultSettings_AreAnOverlayChartIndicator()
    {
        var settings = DefaultCoreIndicatorSettings.GetDefault().Single(s => s.TypeEnum == IndicatorType.VolumeProfileLevels);

        var parameter = Assert.IsType<CoreVolumeProfileLevelsParameter>(settings.ParameterObject);
        Assert.Equal(IndicatorDefaultConstants.VolumeProfilePeriod, parameter.Period);
        Assert.Equal(IndicatorDefaultConstants.VolumeProfileRowCount, parameter.RowCount);
        Assert.Equal((decimal)ChartConstants.DefaultValueAreaPercent, parameter.ValueAreaPercentage);
        Assert.Equal(VolumeDistributionMode.Proportional, parameter.Mode);
        Assert.True(settings.IsOverlay);
        Assert.Equal(CoreIndicatorCategory.Chart, settings.Category);
        Assert.Equal(IndicatorDefaultConstants.SmaColor, settings.Color);
        Assert.Equal(IndicatorDefaultConstants.BollingerBandsColor, settings.SeriesColors.Single(c => c.Name == "Bands").Color);
    }

    [Fact]
    public void ConfigureAppliesTheParameterObject()
    {
        var indicator = new CoreVolumeProfileLevelsIndicator();

        indicator.Configure(new CoreVolumeProfileLevelsParameter { Period = 60, RowCount = 30, Mode = VolumeDistributionMode.Full, ValueAreaPercentage = 0.8m });

        Assert.Equal((60, 30, VolumeDistributionMode.Full, 0.8m), (indicator.Period, indicator.RowCount, indicator.Mode, indicator.ValueAreaPercentage));
    }

    [Fact]
    public void DefaultParameterValidates_AndOutOfRangeValuesThrow()
    {
        new CoreVolumeProfileLevelsParameter().Validate();

        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreVolumeProfileLevelsParameter { Period = IndicatorDefaultConstants.VolumeProfileLevelsMinPeriod - 1 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreVolumeProfileLevelsParameter { Period = IndicatorDefaultConstants.DefaultPeriodMax + 1 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreVolumeProfileLevelsParameter { RowCount = IndicatorDefaultConstants.VolumeProfileMinRowCount - 1 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreVolumeProfileLevelsParameter { RowCount = IndicatorDefaultConstants.VolumeProfileMaxRowCount + 1 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreVolumeProfileLevelsParameter { ValueAreaPercentage = 0.09m }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreVolumeProfileLevelsParameter { ValueAreaPercentage = 1.0m }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreVolumeProfileLevelsParameter { Mode = (VolumeDistributionMode)99 }.Validate());
    }
}
