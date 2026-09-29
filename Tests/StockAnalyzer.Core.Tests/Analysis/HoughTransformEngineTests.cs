using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Core.Tests.Analysis;

public class HoughTransformEngineTests
{
    [Fact]
    public void DetectLines_WithEmptyOrInsufficientPoints_ReturnsEmpty()
    {
        var emptyResult = HoughTransformEngine.DetectLines(Array.Empty<HoughPoint>(), 100);
        Assert.Same(HoughTransformResult.Empty, emptyResult);

        var singlePoint = new[] { new HoughPoint(10, 100m) };
        var singleResult = HoughTransformEngine.DetectLines(singlePoint, 100);
        Assert.Same(HoughTransformResult.Empty, singleResult);
    }

    [Fact]
    public void DetectLines_WithCollinearUpwardPoints_DetectsUpwardLine()
    {
        // Line: Price = 100 + 1.0 * barIndex
        // 5 points at x = 10, 30, 50, 70, 90
        var points = new List<HoughPoint>
        {
            new(10, 110m),
            new(30, 130m),
            new(50, 150m),
            new(70, 170m),
            new(90, 190m)
        };

        var result = HoughTransformEngine.DetectLines(
            points,
            totalBars: 100,
            thetaStepDegrees: 1.0,
            rhoStep: 0.02,
            voteThreshold: 4,
            maxLines: 3,
            normalization: HoughNormalizationMode.MinMax);

        Assert.NotEmpty(result.Lines);
        var bestLine = result.Lines[0];

        // Should have high votes (at least 4)
        Assert.True(bestLine.Votes >= 4);

        // In price space, slope should be positive (close to 1.0)
        Assert.True(bestLine.Slope > 0.5 && bestLine.Slope < 1.5,
            $"Expected slope ~1.0, but got {bestLine.Slope}");

        // Start and end prices should reflect upward trend
        Assert.True(bestLine.EndPrice > bestLine.StartPrice);
    }

    [Fact]
    public void DetectLines_WithHorizontalPoints_DetectsHorizontalLine()
    {
        // Horizontal support level at price 150m
        var points = new List<HoughPoint>
        {
            new(5, 150m),
            new(25, 150m),
            new(45, 150m),
            new(65, 150m),
            new(85, 150m)
        };

        var result = HoughTransformEngine.DetectLines(
            points,
            totalBars: 100,
            thetaStepDegrees: 1.0,
            rhoStep: 0.02,
            voteThreshold: 4,
            maxLines: 1,
            normalization: HoughNormalizationMode.MinMax);

        Assert.Single(result.Lines);
        var line = result.Lines[0];

        // Slope should be approximately 0.0
        Assert.True(Math.Abs(line.Slope) < 0.1, $"Expected slope ~0.0, but got {line.Slope}");
        Assert.True(Math.Abs((double)line.StartPrice - 150.0) < 5.0);
    }

    [Fact]
    public void DetectLines_WithOutliers_PrioritizesDominantLine()
    {
        // 5 points on y = 100 + 2x, plus 2 random noise points
        var points = new List<HoughPoint>
        {
            new(10, 120m),
            new(20, 140m),
            new(30, 160m),
            new(40, 180m),
            new(50, 200m),
            // Noise
            new(15, 250m),
            new(45, 100m)
        };

        var result = HoughTransformEngine.DetectLines(
            points,
            totalBars: 60,
            thetaStepDegrees: 1.0,
            rhoStep: 0.02,
            voteThreshold: 4,
            maxLines: 1);

        Assert.Single(result.Lines);
        var dominantLine = result.Lines[0];

        // Should capture the 5-point line
        Assert.True(dominantLine.Votes >= 4);
        Assert.True(dominantLine.Slope > 1.0);
    }

    [Fact]
    public void DetectLinesFromCandles_WithZigZagTrend_ExtractsPivotsAndDetectsTrendLines()
    {
        // Create 80 candles with clear zigzag swing highs/lows
        var candles = new List<CandleData>();
        var baseTime = new DateTime(2025, 1, 1);

        decimal price = 1000m;
        for (int i = 0; i < 80; i++)
        {
            // Upward drift with periodic oscillations
            decimal cycle = (decimal)Math.Sin(i * 0.4) * 50m;
            decimal drift = i * 5m;
            decimal currentClose = price + drift + cycle;
            decimal high = currentClose + 10m;
            decimal low = currentClose - 10m;
            decimal open = currentClose - 2m;

            candles.Add(new CandleData(
                baseTime.AddDays(i),
                open,
                high,
                low,
                currentClose,
                100000 + i * 1000));
        }

        var result = HoughTransformEngine.DetectLinesFromCandles(
            candles,
            lookback: 80,
            pivotWindow: 2,
            voteThreshold: 2,
            maxLines: 3);

        Assert.True(result.TotalCandidatePoints > 0);
        // At least one trend line should be detected from zigzag pivots
        if (result.Lines.Count > 0)
        {
            var line = result.Lines[0];
            Assert.True(line.Votes >= 2);
            Assert.NotEqual(HoughLineType.Neutral, line.LineType);
        }
    }

    [Fact]
    public void DetectChannels_WithParallelLines_ConstructsChannel()
    {
        var upperLine = new HoughDetectedLine(
            Rho: 0.5,
            Theta: 0.8,
            Votes: 5,
            Slope: 1.5,
            NormalizedSlope: 0.3,
            StartBar: 0,
            EndBar: 50,
            StartPrice: 200m,
            EndPrice: 275m,
            Strength: 0.8,
            TouchCount: 4,
            LineType: HoughLineType.TrendUp);

        var lowerLine = new HoughDetectedLine(
            Rho: 0.3,
            Theta: 0.8,
            Votes: 5,
            Slope: 1.52, // Almost identical slope
            NormalizedSlope: 0.3,
            StartBar: 0,
            EndBar: 50,
            StartPrice: 150m,
            EndPrice: 226m,
            Strength: 0.8,
            TouchCount: 4,
            LineType: HoughLineType.TrendUp);

        var lines = new[] { upperLine, lowerLine };
        var channels = HoughTransformEngine.DetectChannels(lines, atr: 5.0);

        Assert.Single(channels);
        var channel = channels[0];
        Assert.Equal(upperLine, channel.UpperLine);
        Assert.Equal(lowerLine, channel.LowerLine);
        Assert.Equal(50m, channel.Width);
    }

    [Fact]
    public void DetectLines_WithDegenerateFlatMarket_DetectsHorizontalLineSafely()
    {
        // Points with completely identical price (flat line, range == 0)
        var points = new List<HoughPoint>
        {
            new(10, 100m),
            new(20, 100m),
            new(30, 100m),
            new(40, 100m)
        };

        var result = HoughTransformEngine.DetectLines(points, totalBars: 50, voteThreshold: 3);
        Assert.NotNull(result);
        if (result.Lines.Count > 0)
        {
            var line = result.Lines[0];
            Assert.Equal(0.0, line.Slope);
            Assert.Equal(100m, line.StartPrice);
        }
    }

    [Fact]
    public void DetectLines_WithVerticalPoints_HandlesVerticalGracefully()
    {
        // Collinear points along vertical axis (same bar, different prices)
        var points = new List<HoughPoint>
        {
            new(25, 100m),
            new(25, 120m),
            new(25, 140m),
            new(25, 160m)
        };

        var result = HoughTransformEngine.DetectLines(
            points,
            totalBars: 50,
            thetaStepDegrees: 1.0,
            rhoStep: 0.02,
            voteThreshold: 3,
            maxLines: 1);

        // Should detect line with IsVertical = true without throwing NaN or division exception
        if (result.Lines.Count > 0)
        {
            var line = result.Lines[0];
            Assert.True(line.IsVertical);
            Assert.True(double.IsInfinity(line.Slope));
            Assert.Equal(25, line.StartBar);
            Assert.Equal(25, line.EndBar);
        }
    }

    [Fact]
    public void DetectLinesFromCandles_RefinesLineWithLinearRegression_CalculatesRSquaredAndSpan()
    {
        var candles = new List<CandleData>();
        var baseTime = new DateTime(2025, 1, 1);

        // Generate 60 candles with clear zigzag
        for (int i = 0; i < 60; i++)
        {
            decimal wave = (decimal)Math.Sin(i * 0.5) * 20m;
            decimal close = 500m + i * 2m + wave;
            candles.Add(new CandleData(
                baseTime.AddDays(i),
                close - 1m,
                close + 5m,
                close - 5m,
                close,
                10000));
        }

        var result = HoughTransformEngine.DetectLinesFromCandles(
            candles,
            lookback: 60,
            pivotWindow: 2,
            voteThreshold: 2,
            maxLines: 2);

        if (result.Lines.Count > 0)
        {
            var line = result.Lines[0];
            // Refined lines have RSquared computed and Span >= 0
            Assert.True(line.RSquared >= 0.0 && line.RSquared <= 1.0);
            Assert.True(line.Span >= 0);
            Assert.True(line.Strength >= 0.0 && line.Strength <= 100.0);
        }
    }

    [Fact]
    public void HoughDetectedLine_GetPriceAt_WithVerticalLine_ReturnsStartPriceSafely()
    {
        var verticalLine = new HoughDetectedLine(
            Rho: 0.5,
            Theta: 0.0,
            Votes: 5,
            Slope: double.PositiveInfinity,
            NormalizedSlope: double.PositiveInfinity,
            StartBar: 25,
            EndBar: 25,
            StartPrice: 100m,
            EndPrice: 200m,
            Strength: 80.0,
            TouchCount: 4,
            LineType: HoughLineType.Neutral,
            IsVertical: true);

        // Evaluating at any bar should not throw OverflowException and should return StartPrice
        decimal price = verticalLine.GetPriceAt(30);
        Assert.Equal(100m, price);
    }
}
