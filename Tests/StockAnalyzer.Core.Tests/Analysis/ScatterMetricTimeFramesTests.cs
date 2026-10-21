#nullable enable
using System;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Analysis;
using Xunit;

namespace StockAnalyzer.Core.Tests.Analysis;

public class ScatterMetricTimeFramesTests
{
    [Fact]
    public void Supported_IsDayWeekMonth_InThatOrder()
    {
        Assert.Equal(new[] { TimeFrame.D1, TimeFrame.W1, TimeFrame.MN1 }, ScatterMetricTimeFrames.Supported.ToArray());
    }

    [Theory]
    [InlineData(TimeFrame.D1, true)]
    [InlineData(TimeFrame.W1, true)]
    [InlineData(TimeFrame.MN1, true)]
    [InlineData(TimeFrame.H1, false)]
    [InlineData(TimeFrame.H4, false)]
    [InlineData(TimeFrame.M1, false)]
    [InlineData(TimeFrame.M5, false)]
    [InlineData(TimeFrame.M15, false)]
    [InlineData(TimeFrame.M30, false)]
    public void IsSupported_IsTrueForExactlyTheSupportedTimeFrames(TimeFrame timeFrame, bool expected)
    {
        Assert.Equal(expected, ScatterMetricTimeFrames.IsSupported(timeFrame));
    }

    [Fact]
    public void IsSupported_AgreesWithSupported_ForEveryTimeFrame()
    {
        foreach (TimeFrame timeFrame in Enum.GetValues<TimeFrame>())
        {
            Assert.Equal(ScatterMetricTimeFrames.Supported.Contains(timeFrame), ScatterMetricTimeFrames.IsSupported(timeFrame));
        }
    }
}
