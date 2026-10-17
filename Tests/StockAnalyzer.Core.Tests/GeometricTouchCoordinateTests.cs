using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.GeometricPattern;
using StockAnalyzer.Core.Models.MarketStructure;
using Xunit;

namespace StockAnalyzer.Core.Tests;

public class GeometricTouchCoordinateTests
{
    private static (List<CandleData> Candles, List<PivotPoint> Highs, List<PivotPoint> Lows) CreateChannelWithOffset(int k)
    {
        int totalCandles = k + 80;
        var candles = new List<CandleData>(totalCandles);
        DateTime baseDate = new(2025, 1, 1);

        for (int i = 0; i < totalCandles; i++)
        {
            decimal u = 1000m + 2m * (i - k - 50);
            decimal l = u - 8m;
            decimal open = l + 2m;
            decimal close = u - 2m;
            decimal high = u;
            decimal low = l;

            candles.Add(new CandleData(baseDate.AddDays(i), open, high, low, close, 1000));
        }

        var highs = new List<PivotPoint>
        {
            new(k + 50, baseDate.AddDays(k + 50), 1000m + 2m * (k + 50 - k - 50), isHigh: true), // index: k+50, price: 1000m
            new(k + 60, baseDate.AddDays(k + 60), 1000m + 2m * (k + 60 - k - 50), isHigh: true), // index: k+60, price: 1020m
            new(k + 70, baseDate.AddDays(k + 70), 1000m + 2m * (k + 70 - k - 50), isHigh: true), // index: k+70, price: 1040m
        };

        var lows = new List<PivotPoint>
        {
            new(k + 52, baseDate.AddDays(k + 52), 1000m + 2m * (k + 52 - k - 50) - 8m, isHigh: false), // index: k+52, price: 996m
            new(k + 62, baseDate.AddDays(k + 62), 1000m + 2m * (k + 62 - k - 50) - 8m, isHigh: false), // index: k+62, price: 1016m
            new(k + 72, baseDate.AddDays(k + 72), 1000m + 2m * (k + 72 - k - 50) - 8m, isHigh: false), // index: k+72, price: 1036m
        };

        return (candles, highs, lows);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(50)]
    [InlineData(200)]
    public void AnalyzeFormation_WithVariousOffsets_DetectsAscendingChannelWithSlope2(int k)
    {
        var (candles, highs, lows) = CreateChannelWithOffset(k);

        var formation = GeometricPatternDetector.AnalyzeFormation(highs, lows, candles);

        Assert.NotNull(formation);
        Assert.Equal(GeometricFormationType.AscendingChannel, formation.Type);
        Assert.Equal(2.0, formation.UpperSlope, precision: 6);
        Assert.Equal(2.0, formation.LowerSlope, precision: 6);
    }
}
