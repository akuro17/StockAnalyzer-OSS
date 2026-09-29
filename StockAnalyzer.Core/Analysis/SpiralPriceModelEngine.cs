using System;
using System.Threading;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;

namespace StockAnalyzer.Core.Analysis;

/// <summary>
/// Calculates manual price-radius spiral models from chronological candles.
/// Prices remain decimal until they enter the polar-coordinate calculation boundary.
/// </summary>
public static class SpiralPriceModelEngine
{
    private static readonly double GoldenGrowthPerRadian = 2.0 * Math.Log((1.0 + Math.Sqrt(5.0)) / 2.0) / Math.PI;

    public static SpiralAnalysisResult Analyze(
        IReadOnlyList<CoreCandleData> candles,
        SpiralAnalysisParameters parameters)
        => Analyze(candles, parameters, CancellationToken.None);

    public static SpiralAnalysisResult Analyze(
        IReadOnlyList<CoreCandleData> candles,
        SpiralAnalysisParameters parameters,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(candles);
        parameters.Validate(candles.Count);

        int startIndex = checked((int)parameters.StartIndex);
        int endIndex = checked((int)parameters.GetEndIndex(candles.Count));
        var samples = new SpiralAnalysisSample[endIndex - startIndex + 1];
        IReadOnlyList<decimal> prices = PriceDataHelper.ExtractNonNullablePriceSeries(candles, parameters.AppliedPriceType);
        double radiansPerBar = MathConstants.TwoPi / parameters.BarsPerTurn;
        double direction = parameters.IsClockwise ? -1d : 1d;
        double initialRadius = (double)parameters.InitialRadius;
        decimal? previousPrice = null;
        decimal? priceBeforePrevious = null;
        decimal absoluteErrorSum = 0m;
        decimal squaredErrorSum = 0m;
        decimal validPriceSum = 0m;
        int validModelCount = 0;
        decimal? latestResidualPercent = null;
        decimal? latestVelocity = null;
        decimal? latestAcceleration = null;
        double? latestModelVelocity = null;
        double? latestModelAcceleration = null;
        bool fitMetricsAvailable = true;
        bool numericLimit = false;
        int outputCount = 0;

        for (int sourceIndex = startIndex; sourceIndex <= endIndex; sourceIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CoreCandleData candle = candles[sourceIndex];
            decimal price = prices[sourceIndex];
            double angle = direction * (sourceIndex - startIndex) * radiansPerBar;
            double exponent = GetExponent(parameters, angle);
            if (exponent > SpiralAnalysisParameters.MaximumExponent)
            {
                Array.Resize(ref samples, outputCount);
                return new SpiralAnalysisResult(parameters, samples, stoppedByExponentLimit: true,
                    CreateSummary(validModelCount, absoluteErrorSum, squaredErrorSum, validPriceSum, latestResidualPercent,
                        latestVelocity, latestAcceleration, latestModelVelocity, latestModelAcceleration, fitMetricsAvailable)) { HasNumericLimit = numericLimit };
            }

            latestResidualPercent = null;
            latestVelocity = null;
            latestAcceleration = null;
            latestModelVelocity = null;
            latestModelAcceleration = null;

            if (price <= 0m)
            {
                samples[outputCount++] = new SpiralAnalysisSample(
                    (uint)sourceIndex, candle.Timestamp, SpiralAnalysisSampleStatus.NonPositivePrice, price,
                    angle, 0d, null, null, null, null);
                previousPrice = null;
                priceBeforePrevious = null;
                continue;
            }

            double radius = (double)price;
            double? modelRadius = CalculateModelRadius(parameters, initialRadius, angle, exponent);
            double? residual = modelRadius.HasValue ? radius - modelRadius.Value : null;
            decimal? velocityDecimal = previousPrice.HasValue ? price - previousPrice.Value : null;
            decimal? accelerationDecimal = null;
            if (previousPrice.HasValue && priceBeforePrevious.HasValue)
            {
                try { accelerationDecimal = price - (2m * previousPrice.Value) + priceBeforePrevious.Value; }
                catch (OverflowException) { numericLimit = true; }
            }
            double? velocity = velocityDecimal.HasValue ? (double)velocityDecimal.Value : null;
            double? acceleration = accelerationDecimal.HasValue ? (double)accelerationDecimal.Value : null;

            if (modelRadius is { } calculatedRadius && TryGetModelPrice(calculatedRadius, out decimal modelPrice))
            {
                decimal error = price - modelPrice;
                decimal absoluteError = decimal.Abs(error);
                validModelCount++;
                if (fitMetricsAvailable)
                {
                    try
                    {
                        decimal nextAbsolute = absoluteErrorSum + absoluteError;
                        decimal nextSquared = squaredErrorSum + error * error;
                        decimal nextPrices = validPriceSum + price;
                        absoluteErrorSum = nextAbsolute;
                        squaredErrorSum = nextSquared;
                        validPriceSum = nextPrices;
                    }
                    catch (OverflowException)
                    {
                        fitMetricsAvailable = false;
                        numericLimit = true;
                    }
                }
                try { latestResidualPercent = error / price; }
                catch (OverflowException) { numericLimit = true; }
                double signedRadiansPerBar = direction * radiansPerBar;
                latestModelVelocity = GetModelVelocityPerBar(parameters, calculatedRadius, signedRadiansPerBar);
                latestModelAcceleration = GetModelAccelerationPerBarSquared(parameters, calculatedRadius, signedRadiansPerBar);
                if (latestModelVelocity.HasValue && !double.IsFinite(latestModelVelocity.Value)) { latestModelVelocity = null; numericLimit = true; }
                if (latestModelAcceleration.HasValue && !double.IsFinite(latestModelAcceleration.Value)) { latestModelAcceleration = null; numericLimit = true; }
            }
            else if (modelRadius.HasValue) { numericLimit = true; }

            samples[outputCount++] = new SpiralAnalysisSample(
                (uint)sourceIndex, candle.Timestamp, SpiralAnalysisSampleStatus.Valid, price,
                angle, radius, modelRadius, residual, velocity, acceleration);
            priceBeforePrevious = previousPrice;
            previousPrice = price;
            latestVelocity = velocityDecimal;
            latestAcceleration = accelerationDecimal;
        }

        return new SpiralAnalysisResult(parameters, samples, stoppedByExponentLimit: false,
            CreateSummary(validModelCount, absoluteErrorSum, squaredErrorSum, validPriceSum, latestResidualPercent,
                latestVelocity, latestAcceleration, latestModelVelocity, latestModelAcceleration, fitMetricsAvailable)) { HasNumericLimit = numericLimit };
    }

    private static double GetExponent(SpiralAnalysisParameters parameters, double angle)
        => parameters.Model switch
        {
            SpiralPriceModelKind.Logarithmic => parameters.LogGrowthPerRadian!.Value * angle,
            SpiralPriceModelKind.Golden => GoldenGrowthPerRadian * angle,
            _ => 0d
        };

    private static double? CalculateModelRadius(SpiralAnalysisParameters parameters, double initialRadius, double angle, double exponent)
    {
        double radius = parameters.Model switch
        {
            SpiralPriceModelKind.Archimedean => initialRadius + (parameters.RadialGrowthPerRadian!.Value * angle),
            SpiralPriceModelKind.Logarithmic or SpiralPriceModelKind.Golden => initialRadius * Math.Exp(exponent),
            _ => double.NaN
        };

        return double.IsFinite(radius) && radius >= 0d ? radius : null;
    }

    private static bool TryGetModelPrice(double radius, out decimal modelPrice)
    {
        if (double.IsFinite(radius) && radius >= 0d && radius <= (double)decimal.MaxValue)
        {
            try { modelPrice = (decimal)radius; return true; }
            catch (OverflowException) { }
        }

        modelPrice = 0m;
        return false;
    }

    private static SpiralAnalysisSummary CreateSummary(
        int validModelCount,
        decimal absoluteErrorSum,
        decimal squaredErrorSum,
        decimal validPriceSum,
        decimal? latestResidualPercent,
        decimal? latestVelocity,
        decimal? latestAcceleration,
        double? latestModelVelocity,
        double? latestModelAcceleration,
        bool fitMetricsAvailable)
    {
        if (validModelCount == 0 || !fitMetricsAvailable)
        {
            return new SpiralAnalysisSummary(validModelCount, null, null, null, latestResidualPercent, latestVelocity, latestAcceleration,
                latestModelVelocity, latestModelAcceleration, GetMotionState(latestVelocity, latestAcceleration));
        }

        decimal count = validModelCount;
        decimal mae = absoluteErrorSum / count;
        decimal rmse = DecimalSquareRoot(squaredErrorSum / count);
        decimal meanPrice = validPriceSum / count;
        decimal? nrmse = null;
        try { nrmse = meanPrice == 0m ? null : rmse / meanPrice; }
        catch (OverflowException) { }
        return new SpiralAnalysisSummary(validModelCount, mae, rmse, nrmse, latestResidualPercent, latestVelocity,
            latestAcceleration, latestModelVelocity, latestModelAcceleration, GetMotionState(latestVelocity, latestAcceleration));
    }

    private static double? GetModelVelocityPerBar(SpiralAnalysisParameters parameters, double radius, double radiansPerBar)
        => parameters.Model switch
        {
            SpiralPriceModelKind.Archimedean => parameters.RadialGrowthPerRadian!.Value * radiansPerBar,
            SpiralPriceModelKind.Logarithmic => parameters.LogGrowthPerRadian!.Value * radiansPerBar * radius,
            SpiralPriceModelKind.Golden => GoldenGrowthPerRadian * radiansPerBar * radius,
            _ => null
        };

    private static double? GetModelAccelerationPerBarSquared(SpiralAnalysisParameters parameters, double radius, double radiansPerBar)
    {
        double growth = parameters.Model switch
        {
            SpiralPriceModelKind.Logarithmic => parameters.LogGrowthPerRadian!.Value,
            SpiralPriceModelKind.Golden => GoldenGrowthPerRadian,
            _ => 0d
        };
        return parameters.Model == SpiralPriceModelKind.Archimedean ? 0d : growth * growth * radiansPerBar * radiansPerBar * radius;
    }

    private static SpiralPriceMotionState GetMotionState(decimal? velocity, decimal? acceleration)
    {
        if (!velocity.HasValue || !acceleration.HasValue) return SpiralPriceMotionState.Unavailable;
        if (velocity == 0m) return acceleration == 0m ? SpiralPriceMotionState.Flat : SpiralPriceMotionState.Unavailable;
        if (acceleration == 0m) return velocity > 0m ? SpiralPriceMotionState.RisingConstant : SpiralPriceMotionState.FallingConstant;
        if (velocity > 0m) return acceleration > 0m ? SpiralPriceMotionState.RisingAccelerating : SpiralPriceMotionState.RisingDecelerating;
        return acceleration < 0m ? SpiralPriceMotionState.FallingAccelerating : SpiralPriceMotionState.FallingDecelerating;
    }

    private static decimal DecimalSquareRoot(decimal value)
    {
        if (value < 0m) throw new ArgumentOutOfRangeException(nameof(value));
        if (value == 0m) return 0m;

        decimal estimate = value >= 1m ? value : 1m;
        for (int iteration = 0; iteration < 64; iteration++)
        {
            decimal next = (estimate + (value / estimate)) / 2m;
            if (next == estimate) return next;
            estimate = next;
        }

        return estimate;
    }
}
