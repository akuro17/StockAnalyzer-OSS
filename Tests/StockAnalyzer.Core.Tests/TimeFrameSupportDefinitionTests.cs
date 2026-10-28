using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Analysis;
using StockAnalyzer.Core.ReadOnly;
using Xunit;

namespace StockAnalyzer.Core.Tests;

/// <summary>
/// The lists of time frames that the data layer can serve (gateway loading, backtest runs, scatter metrics) are
/// views of one definition. Each of them is Day, Week, Month in that order.
/// </summary>
public class TimeFrameSupportDefinitionTests
{
    private static readonly TimeFrame[] DayWeekMonth = { TimeFrame.D1, TimeFrame.W1, TimeFrame.MN1 };

    [Fact]
    public void EveryListOfSupportedTimeFrames_IsDayWeekMonthInThatOrder()
    {
        Assert.Equal(DayWeekMonth, ReadOnlyGatewayContract.SupportedTimeFrames.ToArray());
        Assert.Equal(DayWeekMonth, ReadOnlyGatewayContract.SupportedBacktestTimeFrames.ToArray());
        Assert.Equal(DayWeekMonth, ScatterMetricTimeFrames.Supported.ToArray());
    }

    [Fact]
    public void EveryListOfSupportedTimeFrames_IsTheSameInstanceAsTheSingleDefinition()
    {
        Assert.Same(DataServiceTimeFrames.Supported, ReadOnlyGatewayContract.SupportedTimeFrames);
        Assert.Same(DataServiceTimeFrames.Supported, ReadOnlyGatewayContract.SupportedBacktestTimeFrames);
        Assert.Same(DataServiceTimeFrames.Supported, ScatterMetricTimeFrames.Supported);
    }
}
