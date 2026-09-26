using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Advanced;
using Xunit;

namespace StockAnalyzer.Core.Tests.Indicators;

public class CoreFrechetOscillatorIndicatorTests
{
    private static List<CoreCandleData> CreateCandles(params decimal[] closes)
    {
        return closes
            .Select((c, i) => new CoreCandleData(DateTime.Today.AddDays(i), c, c, c, c, 0))
            .ToList();
    }

    private static async Task<IReadOnlyList<decimal?>> RunAsync(CoreFrechetOscillatorIndicator indicator, List<CoreCandleData> candles)
    {
        var result = await indicator.CalculateAsync(candles, new CoreExecutionContext());
        Assert.True(result.IsSuccessful);
        return result.GetSeries(IndicatorResult.MainSeriesName);
    }

    [Fact]
    public async Task CalculateAsync_LinearTrend_ReturnsZeroAfterWarmup()
    {
        var indicator = new CoreFrechetOscillatorIndicator { Period = 2, Lag = 2 };

        var series = await RunAsync(indicator, CreateCandles(1, 2, 3, 4, 5));

        Assert.Null(series[2]);
        Assert.Equal(0.0m, series[3]);
        Assert.Equal(0.0m, series[4]);
    }

    [Fact]
    public async Task CalculateAsync_MissingCandleInWindow_ReturnsNullForAffectedBarsOnly()
    {
        // Index 4 is missing; with Period = 2 and Lag = 2 the windows of bars 4..7 touch it.
        var candles = CreateCandles(1, 2, 3, 4, 5, 6, 7, 8, 9);
        candles[4] = null!;
        var indicator = new CoreFrechetOscillatorIndicator { Period = 2, Lag = 2 };

        var series = await RunAsync(indicator, candles);

        Assert.Equal(0.0m, series[3]);
        for (int i = 4; i <= 7; i++) Assert.Null(series[i]);
        Assert.Equal(0.0m, series[8]);
    }
}
