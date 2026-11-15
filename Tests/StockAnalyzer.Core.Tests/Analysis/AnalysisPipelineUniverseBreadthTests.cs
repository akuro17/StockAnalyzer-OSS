#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Analysis;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Statistics;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Services.Analysis;
using Xunit;

namespace StockAnalyzer.Core.Tests.Analysis;

public sealed class AnalysisPipelineUniverseBreadthTests
{
    private sealed class RecordingBreadthProvider : IMarketBreadthSeriesProvider
    {
        private readonly IReadOnlyDictionary<string, IReadOnlyList<CandleData>> _universeBySymbol;
        private readonly Guid _expectedGroupId;

        public int CallCount { get; private set; }
        public List<(Guid GroupId, TimeFrame TimeFrame, MarketBreadthParameters Parameters)> Calls { get; } = new();

        public RecordingBreadthProvider(Guid expectedGroupId, IReadOnlyDictionary<string, IReadOnlyList<CandleData>> universeBySymbol)
        {
            _expectedGroupId = expectedGroupId;
            _universeBySymbol = universeBySymbol;
        }

        public Task<MarketBreadthResult?> GetAsync(
            Guid groupId,
            TimeFrame timeFrame,
            MarketBreadthParameters parameters,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            CallCount++;
            Calls.Add((groupId, timeFrame, parameters));

            if (groupId != _expectedGroupId)
            {
                return Task.FromResult<MarketBreadthResult?>(null);
            }

            var effectiveParams = new MarketBreadthParameters
            {
                AdRatioBars = parameters.AdRatioBars,
                McClellanFast = parameters.McClellanFast,
                McClellanSlow = parameters.McClellanSlow,
                MaBars = parameters.MaBars,
                HighLowBars = parameters.HighLowBars,
                TimeFrame = timeFrame
            };
            return Task.FromResult<MarketBreadthResult?>(MarketBreadthCalculator.Calculate(_universeBySymbol, effectiveParams));
        }

        public void ClearCache()
        {
        }
    }

    private static List<CoreCandleData> BuildCoreCandles(DateTime startDate, int count, TimeFrame timeFrame, Func<int, decimal> closeSelector)
    {
        var list = new List<CoreCandleData>(count);
        for (int i = 0; i < count; i++)
        {
            DateTime ts = timeFrame switch
            {
                TimeFrame.W1 => startDate.AddDays(i * 7),
                TimeFrame.MN1 => startDate.AddMonths(i),
                _ => NextWeekday(startDate, i)
            };
            decimal close = closeSelector(i);
            list.Add(new CoreCandleData(ts, close, close + 1m, close - 1m, close, 1000L + i * 10L));
        }

        return list;
    }

    private static List<CandleData> ToCandleData(IReadOnlyList<CoreCandleData> core)
        => core.Select(c => new CandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume)).ToList();

    private static DateTime NextWeekday(DateTime start, int offset)
    {
        DateTime d = start;
        int added = 0;
        while (added < offset)
        {
            d = d.AddDays(1);
            if (d.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday)
            {
                added++;
            }
        }

        return d;
    }

    [Fact]
    public async Task CalculateIndicatorsAsync_McClellanOscillator_MatchesDirectCalculatorOutput()
    {
        Guid groupId = Guid.NewGuid();
        var start = new DateTime(2024, 1, 1);
        var chartCandles = BuildCoreCandles(start, 60, TimeFrame.D1, i => 100m + i);
        var u1 = ToCandleData(BuildCoreCandles(start, 60, TimeFrame.D1, i => 50m + i));
        var u2 = ToCandleData(BuildCoreCandles(start, 60, TimeFrame.D1, i => i % 2 == 0 ? 40m + i : 42m + i));
        var u3 = ToCandleData(BuildCoreCandles(start, 60, TimeFrame.D1, i => 30m + (i % 5)));
        var universe = new Dictionary<string, IReadOnlyList<CandleData>>
        {
            ["SYM1"] = u1,
            ["SYM2"] = u2,
            ["SYM3"] = u3
        };

        var provider = new RecordingBreadthProvider(groupId, universe);
        var pipeline = new AnalysisPipelineService(marketBreadthProvider: provider);

        var param = new CoreMcClellanOscillatorParameter
        {
            UniverseGroupId = groupId.ToString("D"),
            FastPeriod = 5,
            SlowPeriod = 10
        };
        var setting = new CoreIndicatorSettings
        {
            Id = "mc1",
            TypeEnum = IndicatorType.McClellanOscillator,
            ParameterObject = param
        };

        Dictionary<string, IIndicatorResult> results = await pipeline.CalculateIndicatorsAsync(chartCandles, new[] { setting });
        Assert.True(results.TryGetValue("mc1", out IIndicatorResult? indResult));
        Assert.True(indResult!.IsSuccessful);
        Assert.Equal(1, provider.CallCount);
        Assert.Equal(TimeFrame.D1, provider.Calls[0].TimeFrame);

        MarketBreadthResult direct = MarketBreadthCalculator.Calculate(universe, new MarketBreadthParameters
        {
            McClellanFast = 5,
            McClellanSlow = 10,
            TimeFrame = TimeFrame.D1
        });

        Assert.Equal(chartCandles.Count, indResult.MainValues.Count);
        IReadOnlyList<decimal?> summationSeries = indResult.GetSeries(CoreMcClellanOscillatorIndicator.SummationIndexSeriesName);
        IReadOnlyList<decimal?> adLineSeries = indResult.GetSeries(CoreMcClellanOscillatorIndicator.AdLineSeriesName);

        for (int i = 0; i < chartCandles.Count; i++)
        {
            decimal? expectedMc = direct.Series.McClellan[i].HasValue
                ? IndicatorCalculationHelper.ToDecimalOrNull(direct.Series.McClellan[i]!.Value)
                : null;
            decimal? expectedSum = direct.Series.SummationIndex[i].HasValue
                ? IndicatorCalculationHelper.ToDecimalOrNull(direct.Series.SummationIndex[i]!.Value)
                : null;
            decimal? expectedAd = direct.Series.AdLine[i].HasValue ? direct.Series.AdLine[i]!.Value : null;

            Assert.Equal(expectedMc, indResult.MainValues[i]);
            Assert.Equal(expectedSum, summationSeries[i]);
            Assert.Equal(expectedAd, adLineSeries[i]);
        }
    }

    [Fact]
    public async Task CalculateIndicatorsAsync_WithoutGroupId_ReturnsAllNullWithoutProviderCallOrException()
    {
        var provider = new RecordingBreadthProvider(Guid.NewGuid(), new Dictionary<string, IReadOnlyList<CandleData>>());
        var pipeline = new AnalysisPipelineService(marketBreadthProvider: provider);
        var chartCandles = BuildCoreCandles(new DateTime(2024, 1, 1), 30, TimeFrame.D1, i => 100m + i);

        var mcSetting = new CoreIndicatorSettings
        {
            Id = "mc_empty",
            TypeEnum = IndicatorType.McClellanOscillator,
            ParameterObject = new CoreMcClellanOscillatorParameter { UniverseGroupId = string.Empty }
        };
        var mbSetting = new CoreIndicatorSettings
        {
            Id = "mb_empty",
            TypeEnum = IndicatorType.MarketBreadth,
            ParameterObject = new CoreMarketBreadthParameter { UniverseGroupId = string.Empty }
        };
        var mbInvalid = new CoreIndicatorSettings
        {
            Id = "mb_invalid",
            TypeEnum = IndicatorType.MarketBreadth,
            ParameterObject = new CoreMarketBreadthParameter { UniverseGroupId = "not-a-guid" }
        };

        Dictionary<string, IIndicatorResult> results = await pipeline.CalculateIndicatorsAsync(chartCandles, new[] { mcSetting, mbSetting, mbInvalid });

        Assert.Equal(0, provider.CallCount);
        Assert.True(results["mc_empty"].IsSuccessful);
        Assert.All(results["mc_empty"].MainValues, v => Assert.Null(v));
        Assert.True(results["mb_empty"].IsSuccessful);
        Assert.All(results["mb_empty"].MainValues, v => Assert.Null(v));
        Assert.False(results["mb_invalid"].IsSuccessful);
    }

    [Fact]
    public async Task CalculateIndicatorsAsync_WeeklyChart_InjectsWeeklyTimeFrameAndMatchesDirectCalculator()
    {
        Guid groupId = Guid.NewGuid();
        var start = new DateTime(2024, 1, 1); // Monday
        var chartCandles = BuildCoreCandles(start, 25, TimeFrame.W1, i => 100m + i);
        var u1 = ToCandleData(BuildCoreCandles(start, 25, TimeFrame.W1, i => 50m + i));
        var u2 = ToCandleData(BuildCoreCandles(start, 25, TimeFrame.W1, i => 50m + (i % 3 == 0 ? -i : i)));
        var universe = new Dictionary<string, IReadOnlyList<CandleData>>
        {
            ["W1_A"] = u1,
            ["W1_B"] = u2
        };

        var provider = new RecordingBreadthProvider(groupId, universe);
        var pipeline = new AnalysisPipelineService(marketBreadthProvider: provider);

        var setting = new CoreIndicatorSettings
        {
            Id = "mb_w1",
            TypeEnum = IndicatorType.MarketBreadth,
            ParameterObject = new CoreMarketBreadthParameter
            {
                UniverseGroupId = groupId.ToString("D"),
                MetricMode = MarketBreadthMetricMode.PctAboveMa,
                MaBars = 5
            }
        };

        // Test both explicit ITimeFrameAwareIndicatorCalculation and inferred-from-candles IAnalysisPipelineService
        Dictionary<string, IIndicatorResult> explicitResults = await ((ITimeFrameAwareIndicatorCalculation)pipeline)
            .CalculateIndicatorsAsync(chartCandles, new[] { setting }, secondaryCandlesBySymbol: null, TimeFrame.W1);
        Dictionary<string, IIndicatorResult> inferredResults = await pipeline
            .CalculateIndicatorsAsync(chartCandles, new[] { setting });

        Assert.All(provider.Calls, c => Assert.Equal(TimeFrame.W1, c.TimeFrame));

        MarketBreadthResult direct = MarketBreadthCalculator.Calculate(universe, new MarketBreadthParameters
        {
            MaBars = 5,
            TimeFrame = TimeFrame.W1
        });

        for (int i = 0; i < chartCandles.Count; i++)
        {
            decimal? expected = direct.Series.PctAboveMa[i].HasValue
                ? IndicatorCalculationHelper.ToDecimalOrNull(direct.Series.PctAboveMa[i]!.Value * IndicatorDefaultConstants.PercentScale)
                : null;
            Assert.Equal(expected, explicitResults["mb_w1"].MainValues[i]);
            Assert.Equal(expected, inferredResults["mb_w1"].MainValues[i]);
        }
    }

    [Fact]
    public async Task CalculateIndicatorsAsync_SharesProviderLookupAcrossIndicatorsWithSameKey_AndPreservesTwoTickerPath()
    {
        Guid groupId = Guid.NewGuid();
        var start = new DateTime(2024, 1, 1);
        var chartCandles = BuildCoreCandles(start, 30, TimeFrame.D1, i => 100m + i);
        var secCandles = BuildCoreCandles(start, 30, TimeFrame.D1, i => 50m + i).Cast<CoreCandleData?>().ToList();
        var universe = new Dictionary<string, IReadOnlyList<CandleData>>
        {
            ["A"] = ToCandleData(chartCandles)
        };

        var provider = new RecordingBreadthProvider(groupId, universe);
        var pipeline = new AnalysisPipelineService(marketBreadthProvider: provider);

        var mb1 = new CoreIndicatorSettings
        {
            Id = "mb1",
            TypeEnum = IndicatorType.MarketBreadth,
            ParameterObject = new CoreMarketBreadthParameter { UniverseGroupId = groupId.ToString("D"), MetricMode = MarketBreadthMetricMode.AdLine }
        };
        var mb2 = new CoreIndicatorSettings
        {
            Id = "mb2",
            TypeEnum = IndicatorType.MarketBreadth,
            ParameterObject = new CoreMarketBreadthParameter { UniverseGroupId = groupId.ToString("D"), MetricMode = MarketBreadthMetricMode.Cvi }
        };
        var pair = new CoreIndicatorSettings
        {
            Id = "pair1",
            TypeEnum = IndicatorType.PairRatio,
            ParameterObject = new CorePairRatioParameter { ComparisonSymbol = "SPY" }
        };

        var secondary = new Dictionary<string, IReadOnlyList<CoreCandleData?>> { ["SPY"] = secCandles };

        Dictionary<string, IIndicatorResult> results = await pipeline.CalculateIndicatorsAsync(chartCandles, new[] { mb1, mb2, pair }, secondary);

        Assert.Equal(1, provider.CallCount);
        Assert.True(results["mb1"].IsSuccessful);
        Assert.True(results["mb2"].IsSuccessful);
        Assert.True(results["pair1"].IsSuccessful);
        Assert.Equal(2m, results["pair1"].MainValues[0]);
        Assert.Equal(129m / 79m, results["pair1"].MainValues[^1]);
    }

    [Fact]
    public async Task CalculateIndicatorsAsync_WithEndDate_ForwardsEndDateToBreadthProviderAndUsesSeparateKey()
    {
        Guid groupId = Guid.NewGuid();
        var start = new DateTime(2024, 1, 1);
        var chartCandles = BuildCoreCandles(start, 20, TimeFrame.W1, i => 100m + i);
        var universe = new Dictionary<string, IReadOnlyList<CandleData>>
        {
            ["A"] = ToCandleData(BuildCoreCandles(start, 20, TimeFrame.W1, i => 80m + i))
        };

        var provider = new RecordingBreadthProvider(groupId, universe);
        var pipeline = new AnalysisPipelineService(marketBreadthProvider: provider);
        var setting = new CoreIndicatorSettings
        {
            Id = "mb_asof",
            TypeEnum = IndicatorType.MarketBreadth,
            ParameterObject = new CoreMarketBreadthParameter { UniverseGroupId = groupId.ToString("D"), MetricMode = MarketBreadthMetricMode.AdLine }
        };

        var cutoff = new DateOnly(2024, 3, 20);
        await pipeline.CalculateIndicatorsAsync(
            chartCandles,
            new[] { setting },
            secondaryCandlesBySymbol: null,
            TimeFrame.W1,
            endDate: cutoff);

        Assert.Equal(1, provider.CallCount);
        Assert.Equal(TimeFrame.W1, provider.Calls[0].TimeFrame);
        Assert.Equal(cutoff, provider.Calls[0].Parameters.EndDate);
    }
}
