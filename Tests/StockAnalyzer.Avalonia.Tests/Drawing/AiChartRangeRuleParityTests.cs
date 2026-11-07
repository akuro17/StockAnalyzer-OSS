using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Avalonia.Views.Chart;
using StockAnalyzer.Avalonia.Views.Chart.Renderers;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Rendering;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

/// <summary>
/// Plan 15, remediation M-2: the value ranges of the AI image panels (Core <see cref="AiChartPriceRange"/>) follow the application's own rules. The price range is compared with
/// <see cref="ChartDataSnapshot"/> (candles plus overlay values, padded by pixels of the panel) and the oscillator range with <see cref="PanelValueRangeCalculator"/>, on the same
/// bars and the same series, so a change of one rule without the other fails here. The chart side is given the chart's OWN padding constant
/// (<see cref="ChartDataCoordinator.StandardPricePaddingPx"/>, internal for this purpose) and the image side uses <see cref="AiChartImageStyle.PricePaddingPx"/>, so a change of either
/// constant is seen by the price cases, and both are pinned to the literal 10 below (owner decision E-1 = P-B).
/// </summary>
public sealed class AiChartRangeRuleParityTests
{
    private const int PanelHeight = 240;

    /// <summary>What a failure of a padding test means: the style AiRecognitionV1 is frozen, so the way out is a decision, not a number change.</summary>
    private const string PaddingChangedMessage =
        "The padding above and below the price range changed. AiRecognitionV1 is a frozen style that follows the chart's geometry as it was defined: do not change one value to make this test pass. "
        + "Either keep AiRecognitionV1 frozen and separate this test from the chart's constant, or give the image a new style name, and ask the owner first.";

    [Fact]
    public void TheChartPaddingAndTheImagePaddingAreTheSameTenPixels()
    {
        Assert.True(ChartDataCoordinator.StandardPricePaddingPx == 10m, $"Chart padding is {ChartDataCoordinator.StandardPricePaddingPx}. {PaddingChangedMessage}");
        Assert.True(AiChartImageStyle.PricePaddingPx == 10, $"Image padding is {AiChartImageStyle.PricePaddingPx}. {PaddingChangedMessage}");
        Assert.Equal(ChartDataCoordinator.StandardPricePaddingPx, (decimal)AiChartImageStyle.PricePaddingPx);
    }
    private static readonly DateTime FirstDay = new(2025, 1, 6);

    private static (List<CandleData> Core, List<CoreCandleData> App) Bars(params (decimal Low, decimal High)[] ranges)
    {
        var core = new List<CandleData>();
        var app = new List<CoreCandleData>();
        for (int i = 0; i < ranges.Length; i++)
        {
            (decimal low, decimal high) = ranges[i];
            decimal open = low + ((high - low) / 4m);
            decimal close = high - ((high - low) / 4m);
            core.Add(new CandleData(FirstDay.AddDays(i), open, high, low, close, 1000 + i));
            app.Add(new CoreCandleData(FirstDay.AddDays(i), open, high, low, close, 1000 + i));
        }

        return (core, app);
    }

    private static (decimal Min, decimal Max) ApplicationPriceRange(List<CoreCandleData> bars, decimal?[]? overlay)
    {
        var settings = new List<CoreIndicatorSettings>();
        var results = new Dictionary<string, IIndicatorResult>();
        if (overlay is not null)
        {
            var setting = new CoreIndicatorSettings { Id = "ov", TypeEnum = IndicatorType.SMA, IsOverlay = true, IsEnabled = true };
            settings.Add(setting);
            results[setting.Id] = IndicatorResult.Success(new Dictionary<string, IReadOnlyList<decimal?>> { { "Main", overlay } });
        }

        var snapshot = new ChartDataSnapshot(
            candles: bars,
            indicatorResults: results,
            indicatorSettings: settings,
            startIndex: 0,
            count: bars.Count,
            paddingTopPx: ChartDataCoordinator.StandardPricePaddingPx,
            paddingBottomPx: ChartDataCoordinator.StandardPricePaddingPx,
            chartHeightPx: PanelHeight);
        return (snapshot.MinPrice, snapshot.MaxPrice);
    }

    public static IEnumerable<object?[]> PriceCases()
    {
        decimal?[] none = null!;
        yield return new object?[] { "candles only", new (decimal, decimal)[] { (95m, 105m), (97m, 112m), (90m, 108m), (99m, 120m) }, none };
        yield return new object?[]
        {
            "an overlay above and below the candles widens both sides",
            new (decimal, decimal)[] { (95m, 105m), (97m, 112m), (90m, 108m), (99m, 120m) },
            new decimal?[] { 80m, 100m, 130m, 101m },
        };
        yield return new object?[]
        {
            "warm-up gaps of an overlay count for nothing",
            new (decimal, decimal)[] { (95m, 105m), (97m, 112m), (90m, 108m), (99m, 120m) },
            new decimal?[] { null, null, 100m, 101m },
        };
        yield return new object?[]
        {
            "an overlay inside the candles changes nothing",
            new (decimal, decimal)[] { (95m, 105m), (97m, 112m), (90m, 108m), (99m, 120m) },
            new decimal?[] { 100m, 100m, 100m, 100m },
        };
        yield return new object?[] { "flat candles", new (decimal, decimal)[] { (50m, 50m), (50m, 50m), (50m, 50m) }, none };
        yield return new object?[] { "a single bar", new (decimal, decimal)[] { (0.0003m, 0.0009m) }, none };
    }

    [Theory]
    [MemberData(nameof(PriceCases))]
    public void ThePricePanelRangeEqualsTheRangeTheChartComputesForTheSameBarsAndOverlay(string name, (decimal Low, decimal High)[] ranges, decimal?[]? overlay)
    {
        (List<CandleData> coreBars, List<CoreCandleData> appBars) = Bars(ranges);

        (decimal appMin, decimal appMax) = ApplicationPriceRange(appBars, overlay);
        (decimal imageMin, decimal imageMax) = AiChartPriceRange.Compute(coreBars, overlay is null ? null : new IReadOnlyList<decimal?>[] { overlay }, PanelHeight);

        Assert.True(appMin == imageMin && appMax == imageMax, $"{name}: chart {appMin}..{appMax}, image {imageMin}..{imageMax}. If the padding differs: {PaddingChangedMessage}");
    }

    private static (decimal Min, decimal Max) ApplicationOscillatorRange(decimal?[] values, decimal? fixedMin, decimal? fixedMax, bool zeroBaseline)
    {
        var setting = new CoreIndicatorSettings
        {
            Id = "osc",
            TypeEnum = IndicatorType.RSI,
            IsOverlay = false,
            IsEnabled = true,
            MinValue = fixedMin,
            MaxValue = fixedMax,
            IncludeZeroBaseline = zeroBaseline,
        };
        var results = new Dictionary<string, IIndicatorResult>
        {
            [setting.Id] = IndicatorResult.Success(new Dictionary<string, IReadOnlyList<decimal?>> { { "Main", values } }),
        };
        (_, List<CoreCandleData> bars) = Bars(values.Select(_ => (95m, 105m)).ToArray());
        var snapshot = new ChartDataSnapshot(candles: bars, indicatorResults: results, indicatorSettings: new[] { setting }, startIndex: 0, count: bars.Count);
        return PanelValueRangeCalculator.Calculate(snapshot, setting);
    }

    public static IEnumerable<object?[]> OscillatorCases()
    {
        yield return new object?[] { "spread values", new decimal?[] { 20m, 55m, 80m, 35m }, null, null, false };
        yield return new object?[] { "warm-up gaps", new decimal?[] { null, null, 40m, 60m }, null, null, false };
        yield return new object?[] { "a successful result without any value", new decimal?[] { null, null, null }, null, null, false };
        yield return new object?[] { "a successful result without any value, zero baseline asked", new decimal?[] { null, null, null }, null, null, true };
        yield return new object?[] { "all positive, zero baseline asked", new decimal?[] { 10m, 20m, 30m }, null, null, true };
        yield return new object?[] { "all negative, zero baseline asked", new decimal?[] { -30m, -20m, -10m }, null, null, true };
        yield return new object?[] { "around zero, zero baseline asked", new decimal?[] { -0.2m, 0m, 0.15m, 0.2m }, null, null, true };
        yield return new object?[] { "flat non-zero", new decimal?[] { 5m, 5m, 5m }, null, null, false };
        yield return new object?[] { "flat zero", new decimal?[] { 0m, 0m, 0m }, null, null, false };
        yield return new object?[] { "a fixed range wins over the values", new decimal?[] { 20m, 55m, 80m }, 0m, 100m, false };
    }

    [Theory]
    [MemberData(nameof(OscillatorCases))]
    public void TheOscillatorPanelRangeEqualsTheRangeTheChartComputesForTheSameSeries(string name, decimal?[] values, decimal? fixedMin, decimal? fixedMax, bool zeroBaseline)
    {
        (decimal appMin, decimal appMax) = ApplicationOscillatorRange(values, fixedMin, fixedMax, zeroBaseline);
        (decimal imageMin, decimal imageMax) = AiChartPriceRange.ComputeOscillator(new IReadOnlyList<decimal?>[] { values }, fixedMin, fixedMax, zeroBaseline);

        Assert.True(appMin == imageMin && appMax == imageMax, $"{name}: chart {appMin}..{appMax}, image {imageMin}..{imageMax}");
    }

    // The names are pinned by value: the two sets are what the user sees (the data window and the chart information line leave out the four directional series and show the
    // GranvilleLaw "Signals"; the panel scale, the price scale and the AI image treat all five as signals). PanelValueRangeCalculator.IsSignalSeries delegates to the Core set.
    [Theory]
    [InlineData("BullishSignals", true, true)]
    [InlineData("BearishSignals", true, true)]
    [InlineData("BuySignals", true, true)]
    [InlineData("SellSignals", true, true)]
    [InlineData("Signals", false, true)]
    [InlineData("Main", false, false)]
    [InlineData("Upper", false, false)]
    [InlineData("SignalLine", false, false)]
    [InlineData("signals", false, false)]
    [InlineData("", false, false)]
    public void TheSignalSeriesNamesAreTheTwoSetsTheUserSees(string seriesName, bool directional, bool signal)
    {
        Assert.Equal(directional, IndicatorSeriesNames.IsDirectionalSignalSeries(seriesName));
        Assert.Equal(signal, IndicatorSeriesNames.IsSignalSeries(seriesName));
        Assert.Equal(signal, PanelValueRangeCalculator.IsSignalSeries(seriesName));
    }
}
