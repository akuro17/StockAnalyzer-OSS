using System;
using System.Linq;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Core.Tests.Analysis;

public sealed class SpiralPriceModelEngineTests
{
    [Fact]
    public void Analyze_Archimedean_UsesManualCoefficientAndChronologicalPrices()
    {
        var parameters = new SpiralAnalysisParameters(4, 0, SpiralPriceModelKind.Archimedean, 100m, RadialGrowthPerRadian: 2d);
        SpiralAnalysisResult result = SpiralPriceModelEngine.Analyze(CreateCandles(100m, 102m, 106m), parameters);

        Assert.False(result.StoppedByExponentLimit);
        Assert.Equal(3, result.Samples.Count);
        Assert.Equal(100d, result.Samples[0].ModelRadius);
        Assert.Equal(100d + Math.PI, result.Samples[1].ModelRadius!.Value, 10);
        Assert.Equal(2d, result.Samples[1].PriceVelocityPerBar);
        Assert.Equal(2d, result.Samples[2].PriceAccelerationPerBarSquared);
    }

    [Fact]
    public void Analyze_NonPositivePrice_SkipsSampleWithoutConnectingDerivatives()
    {
        var parameters = new SpiralAnalysisParameters(4, 0, SpiralPriceModelKind.Logarithmic, 100m, LogGrowthPerRadian: 0d);
        SpiralAnalysisResult result = SpiralPriceModelEngine.Analyze(CreateCandles(100m, 0m, 110m), parameters);

        Assert.Equal(SpiralAnalysisSampleStatus.NonPositivePrice, result.Samples[1].Status);
        Assert.Null(result.Samples[1].ModelRadius);
        Assert.Null(result.Samples[2].PriceVelocityPerBar);
        Assert.Null(result.Samples[2].PriceAccelerationPerBarSquared);
    }

    [Fact]
    public void Analyze_ExponentAboveLimit_StopsBeforeOverflow()
    {
        var parameters = new SpiralAnalysisParameters(1, 0, SpiralPriceModelKind.Logarithmic, 1m, LogGrowthPerRadian: 1d);
        SpiralAnalysisResult result = SpiralPriceModelEngine.Analyze(CreateCandles(Enumerable.Repeat(1m, 120).ToArray()), parameters);

        Assert.True(result.StoppedByExponentLimit);
        Assert.All(result.Samples, sample => Assert.True(sample.AngleRadians <= SpiralAnalysisParameters.MaximumExponent));
    }

    [Fact]
    public void Analyze_Golden_GrowsByPhiPerQuarterTurn()
    {
        var parameters = new SpiralAnalysisParameters(4, 0, SpiralPriceModelKind.Golden, 10m);
        SpiralAnalysisResult result = SpiralPriceModelEngine.Analyze(CreateCandles(10m, 10m), parameters);

        Assert.Equal(10d, result.Samples[0].ModelRadius);
        Assert.Equal(10d * ((1d + Math.Sqrt(5d)) / 2d), result.Samples[1].ModelRadius!.Value, 10);
    }

    [Fact]
    public void Validate_RejectsDefaultBarsPerTurnAndAutomaticCoefficientCombination()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SpiralPriceModelEngine.Analyze(CreateCandles(100m), new SpiralAnalysisParameters(0, 0, SpiralPriceModelKind.Logarithmic, 100m, LogGrowthPerRadian: 0d)));
        Assert.Throws<ArgumentException>(() =>
            SpiralPriceModelEngine.Analyze(CreateCandles(100m), new SpiralAnalysisParameters(4, 0, SpiralPriceModelKind.Logarithmic, 100m, 1d, 0d)));
    }

    [Fact]
    public void Analyze_ExplicitRange_RejectsReversedAndOutOfRangeBounds()
    {
        CoreCandleData[] candles = CreateCandles(100m, 101m, 102m);

        Assert.Throws<ArgumentOutOfRangeException>(() => SpiralPriceModelEngine.Analyze(candles,
            new SpiralAnalysisParameters(4, 1, SpiralPriceModelKind.Logarithmic, 100m, LogGrowthPerRadian: 0d, EndIndex: 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => SpiralPriceModelEngine.Analyze(candles,
            new SpiralAnalysisParameters(4, 0, SpiralPriceModelKind.Logarithmic, 100m, LogGrowthPerRadian: 0d, EndIndex: 3)));
    }

    [Fact]
    public void Analyze_UsesOnlyExplicitInclusiveRangeAndCalculatesSafeFitMetrics()
    {
        SpiralAnalysisResult result = SpiralPriceModelEngine.Analyze(CreateCandles(100m, 102m, 106m, 999m),
            new SpiralAnalysisParameters(4, 0, SpiralPriceModelKind.Logarithmic, 100m, LogGrowthPerRadian: 0d, EndIndex: 2));

        Assert.Equal(3, result.Samples.Count);
        Assert.Equal(3, result.Summary.ValidSampleCount);
        Assert.Equal(8m / 3m, result.Summary.MeanAbsoluteError);
        Assert.Equal(3.6514837167011076d, (double)result.Summary.RootMeanSquareError!.Value, 12);
        Assert.Equal(0.0355663998379978d, (double)result.Summary.NormalizedRootMeanSquareError!.Value, 12);
        Assert.Equal(6m / 106m, result.Summary.LatestResidualPercent);
        Assert.Equal(SpiralPriceMotionState.RisingAccelerating, result.Summary.LatestMotionState);
    }

    [Fact]
    public void Analyze_AllNonPositivePrices_LeavesFitMetricsUndefined()
    {
        SpiralAnalysisResult result = SpiralPriceModelEngine.Analyze(CreateCandles(0m, -1m, 0m),
            new SpiralAnalysisParameters(4, 0, SpiralPriceModelKind.Logarithmic, 100m, LogGrowthPerRadian: 0d));

        Assert.Equal(0, result.Summary.ValidSampleCount);
        Assert.Null(result.Summary.NormalizedRootMeanSquareError);
        Assert.Null(result.Summary.LatestResidualPercent);
        Assert.All(result.Samples, sample => Assert.Equal(SpiralAnalysisSampleStatus.NonPositivePrice, sample.Status));
    }

    [Fact]
    public void Analyze_AppliedPriceType_UsesSharedTypicalPriceDefinition()
    {
        CoreCandleData[] candles =
        {
            new(new DateTime(2024, 1, 1), 7m, 15m, 9m, 12m, 1L),
            new(new DateTime(2024, 1, 2), 8m, 18m, 6m, 15m, 1L)
        };
        var parameters = new SpiralAnalysisParameters(4, 0, SpiralPriceModelKind.Logarithmic, 12m,
            LogGrowthPerRadian: 0d, AppliedPriceType: PriceType.Typical);

        SpiralAnalysisResult result = SpiralPriceModelEngine.Analyze(candles, parameters);

        Assert.Equal(12m, result.Samples[0].Price);
        Assert.Equal(13m, result.Samples[1].Price);
        Assert.Equal(PriceType.Typical, result.Parameters.AppliedPriceType);
    }

    [Fact]
    public void Analyze_ClockwiseDirection_NegatesAngleAndModelVelocity()
    {
        CoreCandleData[] candles = CreateCandles(100m, 101m);
        var counterclockwise = new SpiralAnalysisParameters(4, 0, SpiralPriceModelKind.Logarithmic, 100m,
            LogGrowthPerRadian: 0.1d);
        var clockwise = new SpiralAnalysisParameters(4, 0, SpiralPriceModelKind.Logarithmic, 100m,
            LogGrowthPerRadian: 0.1d, IsClockwise: true);

        SpiralAnalysisResult positive = SpiralPriceModelEngine.Analyze(candles, counterclockwise);
        SpiralAnalysisResult negative = SpiralPriceModelEngine.Analyze(candles, clockwise);

        Assert.Equal(Math.PI / 2d, positive.Samples[1].AngleRadians, 12);
        Assert.Equal(-Math.PI / 2d, negative.Samples[1].AngleRadians, 12);
        Assert.True(positive.Samples[1].ModelRadius > 100d);
        Assert.True(negative.Samples[1].ModelRadius < 100d);
        Assert.True(positive.Summary.LatestModelVelocityPerBar > 0d);
        Assert.True(negative.Summary.LatestModelVelocityPerBar < 0d);
    }

    [Fact]
    public void InvalidTail_ClearsLatestValuesButRetainsFitCount()
    {
        var result = SpiralPriceModelEngine.Analyze(CreateCandles(100m, 102m, 0m),
            new SpiralAnalysisParameters(4, 0, SpiralPriceModelKind.Logarithmic, 100m, LogGrowthPerRadian: 0d));
        Assert.Equal(2, result.Summary.ValidSampleCount);
        Assert.Null(result.Summary.LatestResidualPercent);
        Assert.Null(result.Summary.LatestPriceVelocityPerBar);
        Assert.Null(result.Summary.LatestModelVelocityPerBar);
    }

    [Theory]
    [InlineData("0.0000000000000000000000000001", "100")]
    [InlineData("100", "79228162514264337593543950335")]
    public void NumericBoundary_DoesNotThrow(string priceText, string radiusText)
    {
        decimal price = decimal.Parse(priceText, System.Globalization.CultureInfo.InvariantCulture);
        decimal radius = decimal.Parse(radiusText, System.Globalization.CultureInfo.InvariantCulture);
        var result = SpiralPriceModelEngine.Analyze(CreateCandles(price, price),
            new SpiralAnalysisParameters(4, 0, SpiralPriceModelKind.Logarithmic, radius, LogGrowthPerRadian: 0d));
        Assert.Equal(2, result.Samples.Count);
    }

    [Fact]
    public void AggregateOverflow_PreservesValidPairCount()
    {
        var result = SpiralPriceModelEngine.Analyze(CreateCandles(1000000000000000m, 1000000000000000m),
            new SpiralAnalysisParameters(4, 0, SpiralPriceModelKind.Logarithmic, 1m, LogGrowthPerRadian: 0d));
        Assert.Equal(2, result.Summary.ValidSampleCount);
        Assert.Null(result.Summary.RootMeanSquareError);
    }

    private static CoreCandleData[] CreateCandles(params decimal[] closes)
    {
        var candles = new CoreCandleData[closes.Length];
        for (int i = 0; i < closes.Length; i++)
        {
            candles[i] = new CoreCandleData(new DateTime(2024, 1, 1).AddDays(i), closes[i], closes[i], closes[i], closes[i], 1L);
        }
        return candles;
    }
}
