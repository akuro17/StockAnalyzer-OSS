using System;
using System.Runtime.CompilerServices;

namespace StockAnalyzer.Utilities;

public static class LogScaleTransform
{
    private const double DefaultLogRangeEpsilon = 1e-8;
    private const int DefaultPriceRoundingDecimals = 8;

    public static double PriceToY_Log(
        decimal price,
        decimal minPrice,
        decimal maxPrice,
        double canvasHeight,
        int roundingDecimals = DefaultPriceRoundingDecimals,
        double logRangeEpsilon = DefaultLogRangeEpsilon)
    {
        ValidatePriceRangeAndCanvas(minPrice, maxPrice, canvasHeight, nameof(minPrice), nameof(maxPrice), nameof(canvasHeight));
        ValidateEpsilon(logRangeEpsilon, nameof(logRangeEpsilon));

        double minD = (double)minPrice;
        double maxD = (double)maxPrice;
        double priceD = (double)price;

        ValidateFiniteValues(minD, maxD, priceD, nameof(minPrice), nameof(maxPrice), nameof(price));

        if (priceD <= minD)
            return canvasHeight;
        if (priceD >= maxD)
            return 0.0;

        double logRange = Math.Log10(maxD / minD);

        if (Math.Abs(logRange) <= logRangeEpsilon)
            return canvasHeight / 2.0;

        double logValue = Math.Log10(priceD / minD);
        double normalized = logValue / logRange;

        normalized = Math.Max(0.0, Math.Min(1.0, normalized));

        return canvasHeight * (1.0 - normalized);
    }

    public static decimal YToPrice_Log(
        double y,
        decimal minPrice,
        decimal maxPrice,
        double canvasHeight,
        int roundingDecimals = DefaultPriceRoundingDecimals,
        double logRangeEpsilon = DefaultLogRangeEpsilon)
    {
        ValidatePriceRangeAndCanvas(minPrice, maxPrice, canvasHeight, nameof(minPrice), nameof(maxPrice), nameof(canvasHeight));
        ValidateEpsilon(logRangeEpsilon, nameof(logRangeEpsilon));

        if (y < 0.0)
            y = 0.0;
        else if (y > canvasHeight)
            y = canvasHeight;

        double minD = (double)minPrice;
        double maxD = (double)maxPrice;

        ValidateFiniteValues(minD, maxD, 0.0, nameof(minPrice), nameof(maxPrice), "computed_range");

        if (y <= 0.0)
            return maxPrice;
        if (y >= canvasHeight)
            return minPrice;

        double logRange = Math.Log10(maxD / minD);

        if (Math.Abs(logRange) <= logRangeEpsilon)
            return (minPrice + maxPrice) / 2m;

        double normalized = 1.0 - (y / canvasHeight);
        normalized = Math.Max(0.0, Math.Min(1.0, normalized));

        double logValue = normalized * logRange;
        double priceD = minD * Math.Pow(10.0, logValue);

        if (double.IsNaN(priceD) || double.IsInfinity(priceD))
            throw new InvalidOperationException("Computed price is not a finite number.");

        decimal price = ClampAndRoundDecimal(priceD, minPrice, maxPrice, roundingDecimals);

        return price;
    }

    public static double PriceToY_Linear(
        decimal price,
        decimal minPrice,
        decimal maxPrice,
        double canvasHeight)
    {
        ValidatePriceRangeAndCanvas(minPrice, maxPrice, canvasHeight, nameof(minPrice), nameof(maxPrice), nameof(canvasHeight));

        double minD = (double)minPrice;
        double maxD = (double)maxPrice;
        double priceD = (double)price;

        ValidateFiniteValues(minD, maxD, priceD, nameof(minPrice), nameof(maxPrice), nameof(price));

        if (priceD <= minD)
            return canvasHeight;
        if (priceD >= maxD)
            return 0.0;

        double rangeD = maxD - minD;
        double normalized = (priceD - minD) / rangeD;

        normalized = Math.Max(0.0, Math.Min(1.0, normalized));

        return canvasHeight * (1.0 - normalized);
    }

    public static decimal YToPrice_Linear(
        double y,
        decimal minPrice,
        decimal maxPrice,
        double canvasHeight,
        int roundingDecimals = DefaultPriceRoundingDecimals)
    {
        ValidatePriceRangeAndCanvas(minPrice, maxPrice, canvasHeight, nameof(minPrice), nameof(maxPrice), nameof(canvasHeight));

        if (y < 0.0)
            y = 0.0;
        else if (y > canvasHeight)
            y = canvasHeight;

        double minD = (double)minPrice;
        double maxD = (double)maxPrice;

        ValidateFiniteValues(minD, maxD, 0.0, nameof(minPrice), nameof(maxPrice), "computed_range");

        if (y <= 0.0)
            return maxPrice;
        if (y >= canvasHeight)
            return minPrice;

        double rangeD = maxD - minD;
        double normalized = 1.0 - (y / canvasHeight);
        normalized = Math.Max(0.0, Math.Min(1.0, normalized));

        double priceD = minD + (normalized * rangeD);

        if (double.IsNaN(priceD) || double.IsInfinity(priceD))
            throw new InvalidOperationException("Computed price is not a finite number.");

        decimal price = ClampAndRoundDecimal(priceD, minPrice, maxPrice, roundingDecimals);

        return price;
    }

    public static (double y, decimal price) GetScaleRatioLogarithmic(
        decimal price,
        decimal minPrice,
        decimal maxPrice,
        double logRangeEpsilon = DefaultLogRangeEpsilon)
    {
        if (minPrice <= 0m || maxPrice <= 0m)
            throw new ArgumentOutOfRangeException(nameof(minPrice), "Prices must be positive for logarithmic scale.");

        if (minPrice >= maxPrice)
            throw new ArgumentException($"{nameof(minPrice)} must be less than {nameof(maxPrice)}.");

        double minD = (double)minPrice;
        double maxD = (double)maxPrice;
        double priceD = (double)price;

        ValidateFiniteValues(minD, maxD, priceD, nameof(minPrice), nameof(maxPrice), nameof(price));

        double logRange = Math.Log10(maxD / minD);

        if (Math.Abs(logRange) <= logRangeEpsilon)
            return (0.5, (minPrice + maxPrice) / 2m);

        if (priceD <= minD)
            return (1.0, minPrice);
        if (priceD >= maxD)
            return (0.0, maxPrice);

        double logValue = Math.Log10(priceD / minD);
        double normalized = logValue / logRange;

        normalized = Math.Max(0.0, Math.Min(1.0, normalized));

        return (1.0 - normalized, price);
    }

    public static (double y, decimal price) GetScaleRatioLinear(
        decimal price,
        decimal minPrice,
        decimal maxPrice)
    {
        if (minPrice >= maxPrice)
            throw new ArgumentException($"{nameof(minPrice)} must be less than {nameof(maxPrice)}.");

        double minD = (double)minPrice;
        double maxD = (double)maxPrice;
        double priceD = (double)price;

        ValidateFiniteValues(minD, maxD, priceD, nameof(minPrice), nameof(maxPrice), nameof(price));

        double rangeD = maxD - minD;

        if (priceD <= minD)
            return (1.0, minPrice);
        if (priceD >= maxD)
            return (0.0, maxPrice);

        double normalized = (priceD - minD) / rangeD;
        normalized = Math.Max(0.0, Math.Min(1.0, normalized));

        return (1.0 - normalized, price);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ValidatePriceRangeAndCanvas(decimal minPrice, decimal maxPrice, double canvasHeight, string minParamName, string maxParamName, string canvasParamName)
    {
        if (minPrice <= 0m)
            throw new ArgumentOutOfRangeException(minParamName, minPrice, $"{minParamName} must be positive.");

        if (maxPrice <= 0m)
            throw new ArgumentOutOfRangeException(maxParamName, maxPrice, $"{maxParamName} must be positive.");

        if (minPrice >= maxPrice)
            throw new ArgumentException($"{minParamName} ({minPrice}) must be less than {maxParamName} ({maxPrice}).");

        if (canvasHeight <= 0.0)
            throw new ArgumentOutOfRangeException(canvasParamName, canvasHeight, $"{canvasParamName} must be positive.");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ValidateFiniteValues(double minD, double maxD, double priceD, string minParamName, string maxParamName, string priceParamName)
    {
        if (double.IsNaN(minD) || double.IsInfinity(minD))
            throw new ArgumentException($"{minParamName} must be a finite number.", minParamName);

        if (double.IsNaN(maxD) || double.IsInfinity(maxD))
            throw new ArgumentException($"{maxParamName} must be a finite number.", maxParamName);

        if (double.IsNaN(priceD) || double.IsInfinity(priceD))
            throw new ArgumentException($"{priceParamName} must be a finite number.", priceParamName);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ValidateEpsilon(double epsilon, string paramName)
    {
        if (epsilon < 0.0 || epsilon > 0.1)
            throw new ArgumentOutOfRangeException(paramName, epsilon, "Epsilon must be between 0.0 and 0.1.");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static decimal ClampAndRoundDecimal(double priceD, decimal minPrice, decimal maxPrice, int roundingDecimals)
    {
        decimal price;
        try
        {
            price = (decimal)priceD;
        }
        catch (OverflowException)
        {
            return priceD > 0 ? maxPrice : minPrice;
        }

        price = Math.Round(price, Math.Min(roundingDecimals, 28));

        if (price < minPrice)
            price = minPrice;
        else if (price > maxPrice)
            price = maxPrice;

        return price;
    }
}
