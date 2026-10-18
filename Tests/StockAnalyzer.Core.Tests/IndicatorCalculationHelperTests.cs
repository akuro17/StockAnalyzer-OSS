using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models.Indicators;
using Xunit;

namespace StockAnalyzer.Core.Tests;

public class IndicatorCalculationHelperTests
{
    [Fact]
    public void CalculateSmaWithNulls_EmptyOrNullData_ReturnsEmptyList()
    {
        Assert.Empty(IndicatorCalculationHelper.CalculateSmaWithNulls(new List<decimal?>(), 5));
        Assert.Empty(IndicatorCalculationHelper.CalculateSmaWithNulls(null!, 5));
    }

    [Fact]
    public void CalculateSmaWithNulls_InvalidPeriod_ThrowsArgumentOutOfRangeException()
    {
        var data = new decimal?[] { 10m, 20m, 30m };
        Assert.Throws<ArgumentOutOfRangeException>(() => IndicatorCalculationHelper.CalculateSmaWithNulls(data, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => IndicatorCalculationHelper.CalculateSmaWithNulls(data, -1));
    }

    [Fact]
    public void CalculateSmaWithNulls_ValidData_ReturnsExactAverages()
    {
        var data = new decimal?[] { 10m, 20m, 30m, 40m, 50m };
        var result = IndicatorCalculationHelper.CalculateSmaWithNulls(data, 3);

        Assert.Equal(5, result.Count);
        Assert.Null(result[0]);
        Assert.Null(result[1]);
        Assert.Equal(20m, result[2]); // (10+20+30)/3
        Assert.Equal(30m, result[3]); // (20+30+40)/3
        Assert.Equal(40m, result[4]); // (30+40+50)/3
    }

    [Fact]
    public void CalculateSmaWithNulls_WithInteriorNull_PropagatesNullUntilWindowClears()
    {
        var data = new decimal?[] { 10m, 20m, null, 40m, 50m, 60m };
        var result = IndicatorCalculationHelper.CalculateSmaWithNulls(data, 3);

        Assert.Equal(6, result.Count);
        Assert.Null(result[0]); // warmup
        Assert.Null(result[1]); // warmup
        Assert.Null(result[2]); // contains null at index 2
        Assert.Null(result[3]); // contains null at index 2
        Assert.Null(result[4]); // contains null at index 2
        Assert.Equal(50m, result[5]); // (40+50+60)/3 = 50, window is clear
    }

    [Fact]
    public void CalculateSmaWithNulls_PeriodOne_ReturnsSameValues()
    {
        var data = new decimal?[] { 10m, null, 30m };
        var result = IndicatorCalculationHelper.CalculateSmaWithNulls(data, 1);

        Assert.Equal(3, result.Count);
        Assert.Equal(10m, result[0]);
        Assert.Null(result[1]);
        Assert.Equal(30m, result[2]);
    }

    [Fact]
    public void CalculateSmaWithNulls_LongSeries_ResyncPreservesExactPrecision()
    {
        // 1200 values of 100m to trigger resynchronization at 1000 bars
        var data = new List<decimal?>(1200);
        for (int i = 0; i < 1200; i++)
        {
            data.Add(100m);
        }

        var result = IndicatorCalculationHelper.CalculateSmaWithNulls(data, 10);
        Assert.Equal(1200, result.Count);
        for (int i = 0; i < 9; i++)
        {
            Assert.Null(result[i]);
        }
        for (int i = 9; i < 1200; i++)
        {
            Assert.Equal(100m, result[i]);
        }
    }
}
