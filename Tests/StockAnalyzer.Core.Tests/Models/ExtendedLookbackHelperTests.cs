using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Trend;
using StockAnalyzer.Core.Models.Parameters;
using Xunit;

namespace StockAnalyzer.Core.Tests.Models;

public class ExtendedLookbackHelperTests
{
    [Fact]
    public void CalculateRequiredLookback_EmptyList_ReturnsZero()
    {
        var result = ExtendedLookbackHelper.CalculateRequiredLookback(new List<CoreIndicatorSettings>());

        Assert.Equal(0, result);
    }

    [Fact]
    public void CalculateRequiredLookback_SingleEnabledPeriodIndicator_ReturnsItsPeriod()
    {
        var settings = new List<CoreIndicatorSettings>
        {
            new() { IsEnabled = true, ParameterObject = new CoreSmaParameter { Period = 200 } }
        };

        var result = ExtendedLookbackHelper.CalculateRequiredLookback(settings);

        Assert.Equal(200, result);
    }

    [Fact]
    public void CalculateRequiredLookback_MultipleEnabledIndicators_ReturnsMaxPeriod()
    {
        var settings = new List<CoreIndicatorSettings>
        {
            new() { IsEnabled = true, ParameterObject = new CoreSmaParameter { Period = 20 } },
            new() { IsEnabled = true, ParameterObject = new CoreSmaParameter { Period = 50 } }
        };

        var result = ExtendedLookbackHelper.CalculateRequiredLookback(settings);

        Assert.Equal(50, result);
    }

    [Fact]
    public void CalculateRequiredLookback_DisabledIndicator_IsIgnored()
    {
        var settings = new List<CoreIndicatorSettings>
        {
            new() { IsEnabled = false, ParameterObject = new CoreSmaParameter { Period = 300 } },
            new() { IsEnabled = true, ParameterObject = new CoreSmaParameter { Period = 20 } }
        };

        var result = ExtendedLookbackHelper.CalculateRequiredLookback(settings);

        Assert.Equal(20, result);
    }

    [Fact]
    public void CalculateRequiredLookback_MacdParameter_ReturnsLongPeriodPlusSignalPeriodMinusOne()
    {
        var settings = new List<CoreIndicatorSettings>
        {
            new() { IsEnabled = true, ParameterObject = new CoreMacdParameter { ShortPeriod = 12, LongPeriod = 26, SignalPeriod = 9 } }
        };

        var result = ExtendedLookbackHelper.CalculateRequiredLookback(settings);

        Assert.Equal(34, result);
    }

    [Fact]
    public void CalculateRequiredLookback_MovingAverageCrossParameter_ReturnsMaxOfShortAndLong()
    {
        var settings = new List<CoreIndicatorSettings>
        {
            new() { IsEnabled = true, ParameterObject = new CoreMovingAverageCrossParameter { ShortPeriod = 10, LongPeriod = 999 } }
        };

        var result = ExtendedLookbackHelper.CalculateRequiredLookback(settings);

        Assert.Equal(999, result);
    }

    [Fact]
    public void CalculateRequiredLookback_StochasticParameter_ReturnsKPeriodPlusSmoothPlusDPeriodMinusTwo()
    {
        var settings = new List<CoreIndicatorSettings>
        {
            new() { IsEnabled = true, ParameterObject = new CoreStochasticParameter { KPeriod = 14, DPeriod = 3, Smooth = 3 } }
        };

        var result = ExtendedLookbackHelper.CalculateRequiredLookback(settings);

        Assert.Equal(18, result);
    }

    [Fact]
    public void CalculateRequiredLookback_SchaffTrendCycleParameter_ReturnsMaxOfCycleAndLongPeriod()
    {
        var settings = new List<CoreIndicatorSettings>
        {
            new() { IsEnabled = true, ParameterObject = new CoreSchaffTrendCycleParameter { CyclePeriod = 10, ShortPeriod = 23, LongPeriod = 50 } }
        };

        var result = ExtendedLookbackHelper.CalculateRequiredLookback(settings);

        Assert.Equal(50, result);
    }

    [Fact]
    public void CalculateRequiredLookback_CoppockCurveParameter_ReturnsMaxOfRocPeriodsPlusWmaPeriod()
    {
        var settings = new List<CoreIndicatorSettings>
        {
            new() { IsEnabled = true, ParameterObject = new CoreCoppockCurveParameter { LongRocPeriod = 14, ShortRocPeriod = 11, WmaPeriod = 10 } }
        };

        var result = ExtendedLookbackHelper.CalculateRequiredLookback(settings);

        Assert.Equal(24, result);
    }

    [Fact]
    public void CalculateRequiredLookback_SmiParameter_ReturnsFormulaResultNotBarePeriod()
    {
        var settings = new List<CoreIndicatorSettings>
        {
            new() { IsEnabled = true, ParameterObject = new CoreSmiParameter { Period = 14, Smooth1 = 5, Smooth2 = 3 } }
        };

        var result = ExtendedLookbackHelper.CalculateRequiredLookback(settings);

        Assert.Equal(20, result);
    }

    [Fact]
    public void CalculateRequiredLookback_TrixParameter_ReturnsFormulaResultNotBarePeriod()
    {
        var settings = new List<CoreIndicatorSettings>
        {
            new() { IsEnabled = true, ParameterObject = new CoreTrixParameter { Period = 15 } }
        };

        var result = ExtendedLookbackHelper.CalculateRequiredLookback(settings);

        Assert.Equal(44, result);
    }

    [Fact]
    public void CalculateRequiredLookback_ParameterWithoutOverrideOrPlainPeriod_ContributesNothing()
    {
        var settings = new List<CoreIndicatorSettings>
        {
            new() { IsEnabled = true, ParameterObject = new CoreZigZagParameter { Threshold = 5.0m } }
        };

        var result = ExtendedLookbackHelper.CalculateRequiredLookback(settings);

        Assert.Equal(0, result);
    }

    [Fact]
    public void CalculateRequiredLookback_IchimokuParameter_ReturnsMaxSampleMinusOne()
    {
        var settings = new List<CoreIndicatorSettings>
        {
            new() { IsEnabled = true, ParameterObject = new CoreIchimokuParameter { TenkanSample = 9, KijunSample = 26, SenkouBSample = 52 } }
        };

        var result = ExtendedLookbackHelper.CalculateRequiredLookback(settings);

        Assert.Equal(51, result);
    }

    [Fact]
    public void CalculateRequiredLookback_NullParameterObject_IsIgnored()
    {
        var settings = new List<CoreIndicatorSettings>
        {
            new() { IsEnabled = true, ParameterObject = null }
        };

        var result = ExtendedLookbackHelper.CalculateRequiredLookback(settings);

        Assert.Equal(0, result);
    }

    [Fact]
    public void CalculateRetainedCount_SingleSeriesResult_ReturnsBaseDisplayCountUnchanged()
    {
        var values = Enumerable.Repeat((decimal?)1m, 30).ToList();
        var result = IndicatorResult.Success(values);

        var retained = ExtendedLookbackHelper.CalculateRetainedCount(result, baseDisplayCount: 20, calculatedCandleCount: 30);

        Assert.Equal(20, retained);
    }

    [Fact]
    public void CalculateRetainedCount_IchimokuResult_ExtendsByForwardProjectedTail()
    {
        var indicator = new CoreIchimokuIndicator { TenkanPeriod = 3, KijunPeriod = 5, SenkouPeriod = 7 };
        var highs = new decimal[] { 10, 11, 12, 11, 12, 13, 14, 13, 12 };
        var lows = new decimal[] { 8, 9, 10, 9, 10, 11, 12, 11, 10 };
        var startDate = DateTime.Today;
        var candles = highs.Select((high, i) => new CoreCandleData(
            startDate.AddDays(i), (high + lows[i]) / 2, (high + lows[i]) / 2, high, lows[i], 1000)).ToList();

        var result = indicator.Calculate(candles);
        Assert.True(result.IsSuccessful, result.ErrorMessage);

        int forwardTail = indicator.SenkouSpanA.Count - candles.Count;
        Assert.True(forwardTail > 0, "Test precondition: SenkouSpanA must project beyond the input candle count.");

        var retained = ExtendedLookbackHelper.CalculateRetainedCount(result, baseDisplayCount: 4, calculatedCandleCount: candles.Count);

        Assert.Equal(4 + forwardTail, retained);
    }

    [Theory]
    [InlineData(ChartType.Candlestick)]
    [InlineData(ChartType.OHLCBar)]
    [InlineData(ChartType.Line)]
    [InlineData(ChartType.Area)]
    [InlineData(ChartType.HeikinAshi)]
    [InlineData(ChartType.ThreeLineBreak)]
    public void IsEligibleChartType_TimeSeriesAndThreeLineBreak_ReturnsTrue(ChartType chartType)
    {
        Assert.True(ExtendedLookbackHelper.IsEligibleChartType(chartType));
    }

    [Theory]
    [InlineData(ChartType.Renko)]
    [InlineData(ChartType.Kagi)]
    [InlineData(ChartType.PointAndFigure)]
    [InlineData(ChartType.ReverseWatch)]
    [InlineData(ChartType.RelativePerformance)]
    [InlineData(ChartType.Invisible)]
    public void IsEligibleChartType_ExcludedChartTypes_ReturnsFalse(ChartType chartType)
    {
        Assert.False(ExtendedLookbackHelper.IsEligibleChartType(chartType));
    }
}
