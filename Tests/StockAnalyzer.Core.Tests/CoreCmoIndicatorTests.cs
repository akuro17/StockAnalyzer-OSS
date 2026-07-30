using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Oscillators;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Tests;

public class CoreCmoIndicatorTests
{
    private static List<CoreCandleData> CreateTestCandles(IEnumerable<decimal> closePrices)
    {
        var startDate = DateTime.Today;
        return closePrices.Select((price, i) => new CoreCandleData(
            startDate.AddDays(i), price, price, price, price, 1000
        )).ToList();
    }

    [Fact]
    public void Calculate_WithValidData_ReturnsCorrectValues()
    {
        var indicator = new CoreCmoIndicator { Period = 3 };
        var candles = CreateTestCandles(new decimal[] { 10, 12, 11, 13, 15, 14 });

        indicator.Calculate(candles);

        Assert.Equal(6, indicator.Values.Count);
        Assert.Null(indicator.Values[0]);
        Assert.Null(indicator.Values[1]);
        Assert.Null(indicator.Values[2]);
        // At index 3: Changes = (12-10)=2, (11-12)=-1, (13-11)=2. SumUp=4, SumDown=1. CMO = ((4-1)/(4+1))*100 = 60
        Assert.Equal(60m, Math.Round(indicator.Values[3]!.Value, 2));
        // At index 4: Changes = (11-12)=-1, (13-11)=2, (15-13)=2. SumUp=4, SumDown=1. CMO = 60
        Assert.Equal(60m, Math.Round(indicator.Values[4]!.Value, 2));
        // At index 5: Changes = (13-11)=2, (15-13)=2, (14-15)=-1. SumUp=4, SumDown=1. CMO = 60
        Assert.Equal(60m, Math.Round(indicator.Values[5]!.Value, 2));
    }

    [Fact]
    public void Calculate_WithEmptyData_ReturnsEmpty()
    {
        var indicator = new CoreCmoIndicator();
        indicator.Calculate(new List<CoreCandleData>());
        Assert.Empty(indicator.Values);
    }
}
