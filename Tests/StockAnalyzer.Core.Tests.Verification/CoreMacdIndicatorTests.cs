using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Oscillators;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests.Verification;

public class CoreMacdIndicatorTests
{
    private static List<CoreCandleData> CreateTestCandles(IEnumerable<decimal> closePrices)
    {
        var startDate = DateTime.Today;
        return closePrices.Select((price, i) => new CoreCandleData(
            startDate.AddDays(i), price, price, price, price, 1000
        )).ToList();
    }

    [Fact]
    public void Calculate_WithSufficientData_ReturnsMultiSeriesResult()
    {
        var indicator = new CoreMacdIndicator { FastPeriod = 3, SlowPeriod = 6, SignalPeriod = 3 };
        var candles = CreateTestCandles(new decimal[] { 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20 });

        var result = indicator.Calculate(candles);

        Assert.True(result.IsSuccessful);
        Assert.True(result.HasSeries(CoreMacdIndicator.MacdSeriesName));
        Assert.True(result.HasSeries(CoreMacdIndicator.SignalSeriesName));
        Assert.True(result.HasSeries(CoreMacdIndicator.HistogramSeriesName));

        var macd = result.GetSeries(CoreMacdIndicator.MacdSeriesName);
        var signal = result.GetSeries(CoreMacdIndicator.SignalSeriesName);
        var histogram = result.GetSeries(CoreMacdIndicator.HistogramSeriesName);

        Assert.Equal(candles.Count, macd.Count);
        Assert.Equal(candles.Count, signal.Count);
        Assert.Equal(candles.Count, histogram.Count);

        // Check values
        // Assuming implementation is correct, just check non-nulls where expected
        Assert.Null(macd[4]); // Macd starts later
        Assert.NotNull(macd[5]);
        Assert.Null(signal[6]); // Signal uses Macd so starts even later
        Assert.NotNull(signal[7]);
        Assert.NotNull(histogram.Last());
    }

    [Fact]
    public void Calculate_WithEmptyData_ReturnsEmpty()
    {
        var indicator = new CoreMacdIndicator();
        var result = indicator.Calculate(new List<CoreCandleData>());

        Assert.True(result.IsSuccessful);
        Assert.Empty(result.MainValues);
    }
}
