using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Portfolio;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// Service to compute derived financial metrics for StockAnalyzer.
/// Serves as the Single Source of Truth (SSoT) for all fundamental derived metric calculations.
/// </summary>
public static class FundamentalsCalculator
{
    #region Safe Arithmetic Primitives

    public static decimal? TryMultiply(decimal a, decimal b)
    {
        try { return a * b; }
        catch (OverflowException) { return null; }
    }

    public static decimal? TryMultiply(decimal? a, decimal? b)
    {
        if (!a.HasValue || !b.HasValue) return null;
        try { return a.Value * b.Value; }
        catch (OverflowException) { return null; }
    }

    public static decimal? TryDivide(decimal numerator, decimal denominator)
    {
        if (denominator == 0m) return null;
        try { return numerator / denominator; }
        catch (OverflowException) { return null; }
    }

    public static decimal? TryDividePercent(decimal numerator, decimal denominator)
    {
        if (denominator == 0m) return null;
        try { return (numerator / denominator) * 100m; }
        catch (OverflowException) { return null; }
    }

    #endregion

    #region Atomic Metric SSoT Calculations

    public static (decimal? High, decimal? Low) ComputeFiftyTwoWeekHighLow(
        IReadOnlyList<CandleData>? candles,
        decimal? fallbackHigh,
        decimal? fallbackLow)
    {
        if (candles == null || candles.Count == 0)
        {
            return (fallbackHigh, fallbackLow);
        }

        var latestDate = candles[candles.Count - 1].Timestamp;
        var cutoff = latestDate.AddYears(-1);

        decimal maxHigh = decimal.MinValue;
        decimal minLow = decimal.MaxValue;
        bool hasValidCandle = false;

        for (int i = candles.Count - 1; i >= 0; i--)
        {
            var candle = candles[i];
            if (candle.Timestamp < cutoff) break;

            if (candle.High > maxHigh) maxHigh = candle.High;
            if (candle.Low < minLow) minLow = candle.Low;
            hasValidCandle = true;
        }

        return hasValidCandle ? (maxHigh, minLow) : (fallbackHigh, fallbackLow);
    }

    public static decimal? ComputeMarketCap(decimal? price, decimal? sharesOutstanding, bool requireNonZeroShares = false)
    {
        if (!price.HasValue || !sharesOutstanding.HasValue) return null;
        if (requireNonZeroShares && sharesOutstanding.Value == 0m) return null;
        return TryMultiply(price.Value, sharesOutstanding.Value);
    }

    public static decimal? ComputePbr(decimal? price, decimal? bookValue, decimal? fallbackPriceToBook = null)
    {
        if (price.HasValue && bookValue.HasValue && bookValue.Value != 0m)
        {
            return TryDivide(price.Value, bookValue.Value);
        }
        return fallbackPriceToBook;
    }

    public static decimal? ComputeDividendYield(decimal? dividendRate, decimal? price, decimal? fallbackDividendYield = null)
    {
        if (dividendRate.HasValue && price.HasValue && price.Value != 0m)
        {
            return TryDividePercent(dividendRate.Value, price.Value);
        }
        return fallbackDividendYield;
    }

    public static decimal? ComputeEarningsYield(decimal? trailingEps, decimal? price, decimal? fallbackTrailingPe = null)
    {
        if (trailingEps.HasValue && price.HasValue && price.Value != 0m)
        {
            return TryDividePercent(trailingEps.Value, price.Value);
        }
        if (fallbackTrailingPe.HasValue && fallbackTrailingPe.Value != 0m)
        {
            return TryDividePercent(1m, fallbackTrailingPe.Value);
        }
        return null;
    }

    public static decimal? ComputeFcfYield(decimal? freeCashflow, decimal? marketCap)
    {
        if (freeCashflow.HasValue && marketCap.HasValue && marketCap.Value != 0m)
        {
            return TryDividePercent(freeCashflow.Value, marketCap.Value);
        }
        return null;
    }

    public static decimal? ComputeFcfMargin(decimal? freeCashflow, decimal? totalRevenue)
    {
        if (freeCashflow.HasValue && totalRevenue.HasValue && totalRevenue.Value != 0m)
        {
            return TryDividePercent(freeCashflow.Value, totalRevenue.Value);
        }
        return null;
    }

    public static decimal? ComputeNetDebt(decimal? totalDebt, decimal? totalCash)
    {
        if (!totalDebt.HasValue || !totalCash.HasValue) return null;
        try { return totalDebt.Value - totalCash.Value; }
        catch (OverflowException) { return null; }
    }

    public static decimal? ComputeNetDebtToEbitda(decimal? netDebt, decimal? ebitda, bool requirePositiveEbitda = true)
    {
        if (!netDebt.HasValue || !ebitda.HasValue) return null;
        if (requirePositiveEbitda && ebitda.Value <= 0m) return null;
        if (!requirePositiveEbitda && ebitda.Value == 0m) return null;
        return TryDivide(netDebt.Value, ebitda.Value);
    }

    public static decimal? ComputeDividendCoverage(decimal? trailingEps, decimal? dividendRate)
    {
        if (trailingEps.HasValue && dividendRate.HasValue && dividendRate.Value != 0m)
        {
            return TryDivide(trailingEps.Value, dividendRate.Value);
        }
        return null;
    }

    public static decimal? ComputePctFromFiftyTwoWeekHigh(decimal? price, decimal? fiftyTwoWeekHigh)
    {
        if (price.HasValue && fiftyTwoWeekHigh.HasValue && fiftyTwoWeekHigh.Value != 0m)
        {
            try { return ((price.Value / fiftyTwoWeekHigh.Value) - 1m) * 100m; }
            catch (OverflowException) { return null; }
        }
        return null;
    }

    public static decimal? ComputeFloatRatio(decimal? floatShares, decimal? sharesOutstanding)
    {
        if (floatShares.HasValue && sharesOutstanding.HasValue && sharesOutstanding.Value != 0m)
        {
            return TryDividePercent(floatShares.Value, sharesOutstanding.Value);
        }
        return null;
    }

    public static decimal? ComputeMarketCapPerEmployee(decimal? marketCap, long? fullTimeEmployees)
    {
        if (marketCap.HasValue && fullTimeEmployees.HasValue && fullTimeEmployees.Value != 0L)
        {
            return TryDivide(marketCap.Value, (decimal)fullTimeEmployees.Value);
        }
        return null;
    }

    public static decimal? ComputePegRatio(decimal? existingPegRatio, decimal? trailingPe, decimal? earningsGrowth)
    {
        if (existingPegRatio.HasValue) return existingPegRatio.Value;
        if (trailingPe.HasValue && earningsGrowth.HasValue && earningsGrowth.Value != 0m)
        {
            try
            {
                decimal growthPct = Math.Abs(earningsGrowth.Value) < 2.0m ? earningsGrowth.Value * 100m : earningsGrowth.Value;
                if (growthPct != 0m)
                {
                    return trailingPe.Value / growthPct;
                }
            }
            catch (OverflowException)
            {
                return null;
            }
        }
        return null;
    }

    public static decimal? ComputeOperatingCashFlowYield(decimal? operatingCashflow, decimal? marketCap)
    {
        if (operatingCashflow.HasValue && marketCap.HasValue && marketCap.Value != 0m)
        {
            return TryDividePercent(operatingCashflow.Value, marketCap.Value);
        }
        return null;
    }

    /// <summary>
    /// Computes Net Cash Ratio unified as percentage (100x scaled per Option A).
    /// ((TotalCash - TotalDebt) / MarketCap) * 100m
    /// </summary>
    public static decimal? ComputeNetCashRatio(decimal? totalCash, decimal? totalDebt, decimal? marketCap)
    {
        if (totalCash.HasValue && totalDebt.HasValue && marketCap.HasValue && marketCap.Value != 0m)
        {
            try { return ((totalCash.Value - totalDebt.Value) / marketCap.Value) * 100m; }
            catch (OverflowException) { return null; }
        }
        return null;
    }

    /// <summary>
    /// Backward-compatibility alias for percentage-scaled Net Cash Ratio.
    /// </summary>
    public static decimal? ComputeNetCashRatioPercent(decimal? totalCash, decimal? totalDebt, decimal? marketCap)
        => ComputeNetCashRatio(totalCash, totalDebt, marketCap);

    public static decimal? ComputePriceToCashFlowRatio(decimal? marketCap, decimal? operatingCashflow)
    {
        if (marketCap.HasValue && operatingCashflow.HasValue && operatingCashflow.Value != 0m)
        {
            return TryDivide(marketCap.Value, operatingCashflow.Value);
        }
        return null;
    }

    public static decimal? ComputeNetDebtEquityRatio(decimal? totalDebt, decimal? totalCash, decimal? bookValue, decimal? sharesOutstanding)
    {
        if (totalDebt.HasValue && totalCash.HasValue && bookValue.HasValue && sharesOutstanding.HasValue)
        {
            try
            {
                decimal equity = bookValue.Value * sharesOutstanding.Value;
                if (equity != 0m)
                {
                    return (totalDebt.Value - totalCash.Value) / equity;
                }
            }
            catch (OverflowException)
            {
                return null;
            }
        }
        return null;
    }

    public static decimal? ComputeDebtToEquityRatio(decimal? totalDebt, decimal? bookValue, decimal? sharesOutstanding)
    {
        if (totalDebt.HasValue && bookValue.HasValue && sharesOutstanding.HasValue)
        {
            try
            {
                decimal equity = bookValue.Value * sharesOutstanding.Value;
                if (equity != 0m)
                {
                    return totalDebt.Value / equity;
                }
            }
            catch (OverflowException)
            {
                return null;
            }
        }
        return null;
    }

    public static decimal? ComputeFiftyTwoWeekRangePosition(decimal? price, decimal? fiftyTwoWeekLow, decimal? fiftyTwoWeekHigh)
    {
        if (price.HasValue && fiftyTwoWeekLow.HasValue && fiftyTwoWeekHigh.HasValue)
        {
            try
            {
                decimal range = fiftyTwoWeekHigh.Value - fiftyTwoWeekLow.Value;
                if (range != 0m)
                {
                    return (price.Value - fiftyTwoWeekLow.Value) / range;
                }
            }
            catch (OverflowException)
            {
                return null;
            }
        }
        return null;
    }

    public static decimal? ComputePeRatio(decimal? price, decimal? eps)
    {
        if (price.HasValue && eps.HasValue && eps.Value != 0m)
        {
            return TryDivide(price.Value, eps.Value);
        }
        return null;
    }

    public static decimal? ComputeTurnoverRate(long volume, decimal? shares)
    {
        if (volume != 0L && shares.HasValue && shares.Value != 0m)
        {
            return TryDividePercent((decimal)volume, shares.Value);
        }
        return null;
    }

    #endregion

    public static TickerMetadata CalculateDerived(TickerMetadata meta, IReadOnlyList<CandleData>? candles = null)
    {
        var (fiftyTwoWeekHigh, fiftyTwoWeekLow) = ComputeFiftyTwoWeekHighLow(candles, meta.FiftyTwoWeekHigh, meta.FiftyTwoWeekLow);

        meta = meta with
        {
            FiftyTwoWeekHigh = fiftyTwoWeekHigh,
            FiftyTwoWeekLow = fiftyTwoWeekLow
        };

        // 1. Market Cap
        decimal? marketCap = meta.MarketCap ?? ComputeMarketCap(meta.CurrentPrice, meta.SharesOutstanding, requireNonZeroShares: false);

        // 2. PBR
        decimal? pbrCalculated = ComputePbr(meta.CurrentPrice, meta.BookValue, meta.PriceToBook);

        // 3. Dividend Yield
        decimal? dividendYieldCalculated = ComputeDividendYield(meta.DividendRate, meta.CurrentPrice, meta.DividendYield);

        // 4. Earnings Yield
        decimal? earningsYield = ComputeEarningsYield(meta.TrailingEps, meta.CurrentPrice, meta.TrailingPE);

        // 5. FCF Yield
        decimal? fcfYield = ComputeFcfYield(meta.FreeCashflow, marketCap);

        // 6. FCF Margin
        decimal? fcfMargin = ComputeFcfMargin(meta.FreeCashflow, meta.TotalRevenue);

        // 7. Net Debt
        decimal? netDebt = ComputeNetDebt(meta.TotalDebt, meta.TotalCash);

        // 8. Net Debt to EBITDA
        decimal? netDebtToEbitda = ComputeNetDebtToEbitda(netDebt, meta.Ebitda, requirePositiveEbitda: true);

        // 9. Dividend Coverage
        decimal? dividendCoverage = ComputeDividendCoverage(meta.TrailingEps, meta.DividendRate);

        // 10. % From 52 Week High
        decimal? pctFromFiftyTwoWeekHigh = ComputePctFromFiftyTwoWeekHigh(meta.CurrentPrice, meta.FiftyTwoWeekHigh);

        // 11. Float Ratio
        decimal? floatRatio = ComputeFloatRatio(meta.FloatShares, meta.SharesOutstanding);

        // 12. Market Cap Per Employee
        decimal? marketCapPerEmployee = ComputeMarketCapPerEmployee(marketCap, meta.FullTimeEmployees);

        // 13. PEG Ratio
        decimal? pegRatio = ComputePegRatio(meta.PegRatio, meta.TrailingPE, meta.EarningsGrowth);

        // 14. Operating Cash Flow Yield
        decimal? operatingCashFlowYield = ComputeOperatingCashFlowYield(meta.OperatingCashflow, marketCap);

        // 15. Net Cash Ratio (Unified 100x percent)
        decimal? netCashRatio = ComputeNetCashRatio(meta.TotalCash, meta.TotalDebt, marketCap);

        // 16. PCFR (Price-to-Cash Flow Ratio)
        decimal? priceToCashFlowRatio = ComputePriceToCashFlowRatio(marketCap, meta.OperatingCashflow);

        // 17. Net D/E Ratio
        decimal? netDebtEquityRatio = ComputeNetDebtEquityRatio(meta.TotalDebt, meta.TotalCash, meta.BookValue, meta.SharesOutstanding);

        // 18. 52-Week Range Position
        decimal? fiftyTwoWeekRangePosition = ComputeFiftyTwoWeekRangePosition(meta.CurrentPrice, meta.FiftyTwoWeekLow, meta.FiftyTwoWeekHigh);

        return meta with
        {
            MarketCap = marketCap,
            PbrCalculated = pbrCalculated,
            DividendYieldCalculated = dividendYieldCalculated,
            EarningsYield = earningsYield,
            FcfYield = fcfYield,
            FcfMargin = fcfMargin,
            NetDebt = netDebt,
            NetDebtToEbitda = netDebtToEbitda,
            DividendCoverage = dividendCoverage,
            PctFromFiftyTwoWeekHigh = pctFromFiftyTwoWeekHigh,
            FloatRatio = floatRatio,
            MarketCapPerEmployee = marketCapPerEmployee,
            PegRatio = pegRatio,
            OperatingCashFlowYield = operatingCashFlowYield,
            NetCashRatio = netCashRatio,
            PriceToCashFlowRatio = priceToCashFlowRatio,
            NetDebtEquityRatio = netDebtEquityRatio,
            FiftyTwoWeekRangePosition = fiftyTwoWeekRangePosition
        };
    }
}
