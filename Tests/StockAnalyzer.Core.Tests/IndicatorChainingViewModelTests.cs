using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Models.Indicators.Oscillators;

namespace StockAnalyzer.Core.Tests;

public class IndicatorChainingViewModelTests
{
    private static List<CoreCandleData> CreateOscillatingCandles(int count = 30)
    {
        var startDate = DateTime.Today;
        var list = new List<CoreCandleData>();
        for (int i = 0; i < count; i++)
        {
            decimal basePrice = 100m + (decimal)Math.Sin(i * 0.5) * 20m;
            list.Add(new CoreCandleData(
                startDate.AddDays(i),
                Open: basePrice - 2m,
                High: basePrice + 10m,
                Low: basePrice - 10m,
                Close: basePrice + 3m,
                Volume: 1000
            ));
        }
        return list;
    }

    [Fact]
    public void CoreRsiIndicator_ConfiguredWithCoreRsiParameter_RespectsPeriodAndPriceSource()
    {
        var rsi = new CoreRsiIndicator();
        rsi.Configure(new CoreRsiParameter { Period = 7 });
        rsi.PriceSource = PriceType.High;

        var candles = CreateOscillatingCandles(30);
        rsi.Calculate(candles);

        Assert.Equal(7, rsi.Period);
        Assert.Equal(candles.Count, rsi.Values.Count);
        
        // Values after Period should not be null
        Assert.NotNull(rsi.Values[15]);
    }

    [Fact]
    public void IndicatorReferenceOption_BuildsValidOption()
    {
        var option = new IndicatorReferenceOption
        {
            Id = "test-id-1234",
            DisplayName = "SMA (20)"
        };

        Assert.Equal("test-id-1234", option.Id);
        Assert.Equal("SMA (20)", option.DisplayName);
        Assert.Equal("SMA (20)", option.ToString());
    }

    [Fact]
    public async Task DynamicModulation_WithRemappedIds_CalculatesAdaptiveSeriesSuccessfully()
    {
        // 1. Create upstream Driver setting (e.g. SMA as driver with ID = old-driver-id)
        var driverSetting = new CoreIndicatorSettings
        {
            Id = "old-driver-id",
            TypeEnum = IndicatorType.SMA,
            DisplayName = "Period Driver SMA",
            ParameterObject = new CoreSmaParameter { Period = 5 },
            IsEnabled = true
        };

        // 2. Create downstream consumer setting (SMA with DynamicPeriodIndicatorId = old-driver-id)
        var consumerSetting = new CoreIndicatorSettings
        {
            Id = "old-consumer-id",
            TypeEnum = IndicatorType.SMA,
            DisplayName = "Adaptive SMA",
            ParameterObject = new CoreSmaParameter { Period = 20 },
            DynamicPeriodIndicatorId = "old-driver-id",
            IsEnabled = true
        };

        var originalList = new List<CoreIndicatorSettings> { driverSetting, consumerSetting };

        // 3. Simulate ApplyIndicators Remapping
        var idMap = new Dictionary<string, string>();
        var remappedList = new List<CoreIndicatorSettings>();

        foreach (var ind in originalList)
        {
            var oldId = ind.Id;
            var clone = ind.Clone();
            idMap[oldId] = clone.Id;
            remappedList.Add(clone);
        }

        foreach (var clone in remappedList)
        {
            if (!string.IsNullOrEmpty(clone.DynamicPeriodIndicatorId) && idMap.TryGetValue(clone.DynamicPeriodIndicatorId, out var newDriverId))
            {
                clone.DynamicPeriodIndicatorId = newDriverId;
            }
        }

        // Assert remapped ID matches new driver ID
        var newDriver = remappedList[0];
        var newConsumer = remappedList[1];

        Assert.NotEqual("old-driver-id", newDriver.Id);
        Assert.Equal(newDriver.Id, newConsumer.DynamicPeriodIndicatorId);

        // 4. Execute through AnalysisPipelineService
        var pipeline = new StockAnalyzer.Core.Services.Analysis.AnalysisPipelineService();
        var candles = CreateOscillatingCandles(30);

        var results = await pipeline.CalculateIndicatorsAsync(candles, remappedList);

        Assert.True(results.ContainsKey(newDriver.Id));
        Assert.True(results.ContainsKey(newConsumer.Id));

        var driverResult = results[newDriver.Id];
        var consumerResult = results[newConsumer.Id];

        Assert.True(driverResult.IsSuccessful);
        Assert.True(consumerResult.IsSuccessful);
        Assert.NotEmpty(consumerResult.MainValues);
    }
}
