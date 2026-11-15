#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Statistics;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Backtest.Engine;
using Xunit;

namespace StockAnalyzer.Core.Tests.Indicators;

public sealed class CoreMarketBreadthIndicatorTests
{
    private static List<CoreCandleData> BuildSeries(DateTime start, int count, decimal startPrice, decimal step, long baseVolume)
    {
        var list = new List<CoreCandleData>(count);
        DateTime d = start;
        while (list.Count < count)
        {
            if (d.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday)
            {
                int i = list.Count;
                decimal close = startPrice + step * i + ((i % 3) - 1) * 0.4m;
                decimal open = close - 0.2m;
                decimal high = Math.Max(open, close) + 1.0m;
                decimal low = Math.Min(open, close) - 1.0m;
                list.Add(new CoreCandleData(d, open, high, low, close, baseVolume + i * 1_000));
            }
            d = d.AddDays(1);
        }

        return list;
    }

    private static (List<CoreCandleData> ChartBars, MarketBreadthResult Breadth) BuildBreadthFixture(TimeFrame timeFrame = TimeFrame.D1)
    {
        var start = new DateTime(2025, 1, 6);
        var rawDaily = new Dictionary<string, IReadOnlyList<CandleData>>(StringComparer.OrdinalIgnoreCase)
        {
            ["SYM1"] = BuildSeries(start, 45, 100m, 0.9m, 200_000).Select(c => new CandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume)).ToList(),
            ["SYM2"] = BuildSeries(start, 45, 150m, 0.4m, 150_000).Select(c => new CandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume)).ToList(),
            ["SYM3"] = BuildSeries(start, 45, 200m, -0.5m, 180_000).Select(c => new CandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume)).ToList()
        };

        IReadOnlyDictionary<string, IReadOnlyList<CandleData>> universe = TimeFrameAggregator.IsPeriodFrame(timeFrame)
            ? rawDaily.ToDictionary(
                kv => kv.Key,
                kv => (IReadOnlyList<CandleData>)TimeFrameAggregator.AggregateToPeriodStart(kv.Value, timeFrame),
                StringComparer.OrdinalIgnoreCase)
            : rawDaily;

        MarketBreadthResult result = MarketBreadthCalculator.Calculate(
            universe,
            new MarketBreadthParameters
            {
                AdRatioBars = 5,
                MaBars = 5,
                HighLowBars = 5,
                TimeFrame = timeFrame
            });

        List<CoreCandleData> chartBars = universe["SYM1"]
            .Select(c => new CoreCandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume))
            .ToList();

        return (chartBars, result);
    }

    [Fact]
    public void Calculate_WithoutInjection_ReturnsAllNulls()
    {
        var (chartBars, _) = BuildBreadthFixture();
        var indicator = new CoreMarketBreadthIndicator();

        IIndicatorResult result = indicator.Calculate(chartBars);

        Assert.True(result.IsSuccessful);
        Assert.Equal(chartBars.Count, result.MainValues.Count);
        Assert.All(result.MainValues, v => Assert.Null(v));
    }

    [Theory]
    [InlineData(MarketBreadthMetricMode.AdRatio)]
    [InlineData(MarketBreadthMetricMode.PctAboveMa)]
    [InlineData(MarketBreadthMetricMode.AdLine)]
    [InlineData(MarketBreadthMetricMode.NetNewHighs)]
    [InlineData(MarketBreadthMetricMode.Trin)]
    [InlineData(MarketBreadthMetricMode.Cvi)]
    public void Calculate_AllMetricModes_MatchDirectCalculatorWithSpecUnits(MarketBreadthMetricMode mode)
    {
        var (chartBars, breadth) = BuildBreadthFixture();

        // Prepend one date outside universe to confirm unmatched dates map to null.
        var extendedChart = new List<CoreCandleData>
        {
            new(new DateTime(2024, 12, 31), 98m, 99m, 97m, 98.5m, 10_000)
        };
        extendedChart.AddRange(chartBars);

        var param = new CoreMarketBreadthParameter
        {
            UniverseGroupId = Guid.NewGuid().ToString(),
            MetricMode = mode,
            AdRatioBars = 5,
            MaBars = 5,
            HighLowBars = 5
        };

        var indicator = Assert.IsType<CoreMarketBreadthIndicator>(IndicatorFactory.Default.Create(IndicatorType.MarketBreadth, param));
        indicator.SetUniverseBreadth(breadth, TimeFrame.D1);

        IIndicatorResult result = indicator.Calculate(extendedChart);

        Assert.True(result.IsSuccessful);
        Assert.Equal(extendedChart.Count, result.MainValues.Count);
        Assert.Null(result.MainValues[0]);

        for (int i = 0; i < breadth.DateCount; i++)
        {
            decimal? expected = mode switch
            {
                MarketBreadthMetricMode.AdRatio => breadth.Series.AdRatio[i] is double r ? IndicatorCalculationHelper.ToDecimalOrNull(r * 100.0) : null,
                MarketBreadthMetricMode.PctAboveMa => breadth.Series.PctAboveMa[i] is double p ? IndicatorCalculationHelper.ToDecimalOrNull(p * 100.0) : null,
                MarketBreadthMetricMode.AdLine => breadth.Series.AdLine[i],
                MarketBreadthMetricMode.NetNewHighs => breadth.Series.NetNewHighs[i],
                MarketBreadthMetricMode.Trin => breadth.Series.Trin[i] is double t ? IndicatorCalculationHelper.ToDecimalOrNull(t) : null,
                MarketBreadthMetricMode.Cvi => breadth.Series.Cvi[i],
                _ => throw new ArgumentOutOfRangeException()
            };

            Assert.Equal(expected, result.MainValues[i + 1]);
        }

        // Verify PctAboveMa percentage bounds [0, 100].
        if (mode == MarketBreadthMetricMode.PctAboveMa)
        {
            Assert.All(result.MainValues.Where(v => v.HasValue), v => Assert.InRange(v!.Value, 0m, 100m));
        }
    }

    [Fact]
    public void ParameterValidationAndRegistrations_EnforceContracts()
    {
        var defaults = new CoreMarketBreadthParameter();
        defaults.Validate();
        Assert.Equal(MarketBreadthCalculator.DefaultAdRatioBars, defaults.AdRatioBars);
        Assert.Equal(MarketBreadthCalculator.DefaultMaBars, defaults.MaBars);
        Assert.Equal(MarketBreadthCalculator.DefaultHighLowBars, defaults.HighLowBars);
        Assert.Equal(MarketBreadthMetricMode.AdRatio, defaults.MetricMode);

        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreMarketBreadthParameter { UniverseGroupId = "invalid" }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreMarketBreadthParameter { MetricMode = (MarketBreadthMetricMode)999 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreMarketBreadthParameter { AdRatioBars = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreMarketBreadthParameter { AdRatioBars = 1001 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreMarketBreadthParameter { MaBars = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreMarketBreadthParameter { MaBars = 1001 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreMarketBreadthParameter { HighLowBars = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoreMarketBreadthParameter { HighLowBars = 1001 }.Validate());

        Assert.Equal(
            BacktestIndicatorViolationReason.SynchronousCalculationUnsupported,
            BacktestIndicatorEligibility.Check(IndicatorType.MarketBreadth, null));

        CoreIndicatorSettings settings = DefaultCoreIndicatorSettings.GetDefault().Single(s => s.TypeEnum == IndicatorType.MarketBreadth);
        Assert.False(settings.IsOverlay);
        Assert.Null(settings.MinValue);
        Assert.Null(settings.MaxValue);
        Assert.Single(settings.SeriesColors);
    }

    [Theory]
    [InlineData(MarketBreadthMetricMode.AdRatio, 0, "Advance/Decline Ratio (%)")]
    [InlineData(MarketBreadthMetricMode.PctAboveMa, 1, "Percentage Above Moving Average (%)")]
    [InlineData(MarketBreadthMetricMode.AdLine, 2, "Advance-Decline Line")]
    [InlineData(MarketBreadthMetricMode.NetNewHighs, 3, "Net New Highs")]
    [InlineData(MarketBreadthMetricMode.Trin, 4, "Arms Index (Trading Index)")]
    [InlineData(MarketBreadthMetricMode.Cvi, 5, "Cumulative Volume Index")]
    public void MetricModeMetadata_PreservesIntegerValuesAndUsesFormalNames(
        MarketBreadthMetricMode mode,
        int expectedIntValue,
        string expectedFormalDescription)
    {
        Assert.Equal(expectedIntValue, (int)mode);
        Assert.Equal(expectedFormalDescription, mode.GetDescription());

        var param = new CoreMarketBreadthParameter { MetricMode = mode };
        Assert.Equal($"Market Breadth ({expectedFormalDescription})", param.GetDisplayName("Market Breadth"));

        var indicator = Assert.IsType<CoreMarketBreadthIndicator>(IndicatorFactory.Default.Create(IndicatorType.MarketBreadth, param));
        Assert.Equal($"{IndicatorType.MarketBreadth.GetDescription()} ({expectedFormalDescription})", indicator.Name);
    }

    [Fact]
    public void BreadthParameters_ExposeExpectedDisplayAttributes()
    {
        Assert.NotNull(typeof(MarketBreadthMetricMode).GetCustomAttributes(typeof(EnumDisplaySortAttribute), inherit: false).SingleOrDefault());

        var breadthUniverseAttr = (System.ComponentModel.DisplayNameAttribute?)Attribute.GetCustomAttribute(
            typeof(CoreMarketBreadthParameter).GetProperty(nameof(CoreMarketBreadthParameter.UniverseGroupId))!,
            typeof(System.ComponentModel.DisplayNameAttribute));
        var mcclellanUniverseAttr = (System.ComponentModel.DisplayNameAttribute?)Attribute.GetCustomAttribute(
            typeof(CoreMcClellanOscillatorParameter).GetProperty(nameof(CoreMcClellanOscillatorParameter.UniverseGroupId))!,
            typeof(System.ComponentModel.DisplayNameAttribute));

        Assert.Equal("List / Filter", breadthUniverseAttr?.DisplayName);
        Assert.Equal("List / Filter", mcclellanUniverseAttr?.DisplayName);
    }

    [Fact]
    public void PopulateFromDoubles_PreservesScaleFormula_AndHandlesNonFiniteAndOverflow()
    {
        var target = new List<decimal?>();
        int[] mapped = new[] { -1, 0, 1, 2, 3, 4 };
        double?[] source = new double?[]
        {
            0.25,
            double.NaN,
            double.PositiveInfinity,
            1e28,
            null
        };

        MarketBreadthDateAligner.PopulateFromDoubles(
            target,
            mapped.Length,
            mapped,
            source,
            IndicatorDefaultConstants.PercentScale);

        Assert.Equal(6, target.Count);
        Assert.Null(target[0]);
        Assert.Equal(25m, target[1]);
        Assert.Null(target[2]);
        Assert.Null(target[3]);
        Assert.Null(target[4]);
        Assert.Null(target[5]);
    }
}
