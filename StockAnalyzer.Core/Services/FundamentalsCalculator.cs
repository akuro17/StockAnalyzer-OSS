using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Portfolio;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// Service to compute derived financial metrics for StockAnalyzer.
/// </summary>
public static class FundamentalsCalculator
{
    public static TickerMetadata CalculateDerived(TickerMetadata meta, IReadOnlyList<CandleData>? candles = null)
    {
        decimal? fiftyTwoWeekHigh = meta.FiftyTwoWeekHigh;
        decimal? fiftyTwoWeekLow = meta.FiftyTwoWeekLow;

        if (candles != null && candles.Count > 0)
        {
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

            if (hasValidCandle)
            {
                fiftyTwoWeekHigh = maxHigh;
                fiftyTwoWeekLow = minLow;
            }
        }

        meta = meta with
        {
            FiftyTwoWeekHigh = fiftyTwoWeekHigh,
            FiftyTwoWeekLow = fiftyTwoWeekLow
        };

        // 1. Market Cap
        decimal? marketCap = meta.MarketCap ?? (meta.CurrentPrice.HasValue && meta.SharesOutstanding.HasValue 
            ? meta.CurrentPrice.Value * meta.SharesOutstanding.Value 
            : null);

        // 2. PBR
        decimal? pbrCalculated = null;
        if (meta.CurrentPrice.HasValue && meta.BookValue.HasValue && meta.BookValue.Value != 0)
        {
            pbrCalculated = meta.CurrentPrice.Value / meta.BookValue.Value;
        }
        else
        {
            pbrCalculated = meta.PriceToBook;
        }

        // 3. Dividend Yield
        decimal? dividendYieldCalculated = null;
        if (meta.DividendRate.HasValue && meta.CurrentPrice.HasValue && meta.CurrentPrice.Value != 0)
        {
            dividendYieldCalculated = (meta.DividendRate.Value / meta.CurrentPrice.Value) * 100m;
        }
        else if (meta.DividendYield.HasValue)
        {
            dividendYieldCalculated = meta.DividendYield.Value * 100m;
        }

        // 4. Earnings Yield
        decimal? earningsYield = null;
        if (meta.TrailingEps.HasValue && meta.CurrentPrice.HasValue && meta.CurrentPrice.Value != 0)
        {
            earningsYield = (meta.TrailingEps.Value / meta.CurrentPrice.Value) * 100m;
        }
        else if (meta.TrailingPE.HasValue && meta.TrailingPE.Value != 0)
        {
            earningsYield = (1m / meta.TrailingPE.Value) * 100m;
        }

        // 5. FCF Yield
        decimal? fcfYield = null;
        if (meta.FreeCashflow.HasValue && marketCap.HasValue && marketCap.Value != 0)
        {
            fcfYield = (meta.FreeCashflow.Value / marketCap.Value) * 100m;
        }

        // 6. FCF Margin
        decimal? fcfMargin = null;
        if (meta.FreeCashflow.HasValue && meta.TotalRevenue.HasValue && meta.TotalRevenue.Value != 0)
        {
            fcfMargin = (meta.FreeCashflow.Value / meta.TotalRevenue.Value) * 100m;
        }

        // 7. Net Debt
        decimal? netDebt = null;
        if (meta.TotalDebt.HasValue && meta.TotalCash.HasValue)
        {
            netDebt = meta.TotalDebt.Value - meta.TotalCash.Value;
        }

        // 8. Net Debt to EBITDA
        decimal? netDebtToEbitda = null;
        if (netDebt.HasValue && meta.Ebitda.HasValue && meta.Ebitda.Value > 0)
        {
            netDebtToEbitda = netDebt.Value / meta.Ebitda.Value;
        }

        // 9. Dividend Coverage
        decimal? dividendCoverage = null;
        if (meta.TrailingEps.HasValue && meta.DividendRate.HasValue && meta.DividendRate.Value != 0)
        {
            dividendCoverage = meta.TrailingEps.Value / meta.DividendRate.Value;
        }

        // 10. % From 52 Week High
        decimal? pctFromFiftyTwoWeekHigh = null;
        if (meta.CurrentPrice.HasValue && meta.FiftyTwoWeekHigh.HasValue && meta.FiftyTwoWeekHigh.Value != 0)
        {
            pctFromFiftyTwoWeekHigh = ((meta.CurrentPrice.Value / meta.FiftyTwoWeekHigh.Value) - 1m) * 100m;
        }

        // 11. Float Ratio
        decimal? floatRatio = null;
        if (meta.FloatShares.HasValue && meta.SharesOutstanding.HasValue && meta.SharesOutstanding.Value != 0)
        {
            floatRatio = (meta.FloatShares.Value / meta.SharesOutstanding.Value) * 100m;
        }

        // 12. Market Cap Per Employee
        decimal? marketCapPerEmployee = null;
        if (marketCap.HasValue && meta.FullTimeEmployees.HasValue && meta.FullTimeEmployees.Value != 0)
        {
            marketCapPerEmployee = marketCap.Value / (decimal)meta.FullTimeEmployees.Value;
        }

        // 13. PEG Ratio (Fallback to calculation if yfinance is null)
        decimal? pegRatio = meta.PegRatio;
        if (!pegRatio.HasValue && meta.TrailingPE.HasValue && meta.EarningsGrowth.HasValue && meta.EarningsGrowth.Value != 0)
        {
            pegRatio = meta.TrailingPE.Value / (meta.EarningsGrowth.Value * 100m);
        }

        // 14. Operating Cash Flow Yield
        decimal? operatingCashFlowYield = null;
        if (meta.OperatingCashflow.HasValue && marketCap.HasValue && marketCap.Value != 0)
        {
            operatingCashFlowYield = (meta.OperatingCashflow.Value / marketCap.Value) * 100m;
        }

        // 15. Net Cash Ratio
        decimal? netCashRatio = null;
        if (marketCap.HasValue && meta.TotalCash.HasValue && meta.TotalDebt.HasValue && marketCap.Value != 0)
        {
            netCashRatio = (meta.TotalCash.Value - meta.TotalDebt.Value) / marketCap.Value;
        }

        // 16. PCFR (Price-to-Cash Flow Ratio)
        decimal? priceToCashFlowRatio = null;
        if (marketCap.HasValue && meta.OperatingCashflow.HasValue && meta.OperatingCashflow.Value != 0)
        {
            priceToCashFlowRatio = marketCap.Value / meta.OperatingCashflow.Value;
        }

        // 17. Net D/E Ratio
        decimal? netDebtEquityRatio = null;
        if (meta.TotalDebt.HasValue && meta.TotalCash.HasValue && meta.BookValue.HasValue && meta.SharesOutstanding.HasValue)
        {
            decimal equity = meta.BookValue.Value * meta.SharesOutstanding.Value;
            if (equity != 0)
            {
                netDebtEquityRatio = (meta.TotalDebt.Value - meta.TotalCash.Value) / equity;
            }
        }

        // 18. 52-Week Range Position
        decimal? fiftyTwoWeekRangePosition = null;
        if (meta.CurrentPrice.HasValue && meta.FiftyTwoWeekLow.HasValue && meta.FiftyTwoWeekHigh.HasValue)
        {
            decimal range = meta.FiftyTwoWeekHigh.Value - meta.FiftyTwoWeekLow.Value;
            if (range != 0)
            {
                fiftyTwoWeekRangePosition = (meta.CurrentPrice.Value - meta.FiftyTwoWeekLow.Value) / range;
            }
        }

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
