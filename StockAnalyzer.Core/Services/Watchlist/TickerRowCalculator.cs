using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Utilities;

namespace StockAnalyzer.Core.Services.Watchlist;

/// <summary>
/// Pure static calculation engine that hydrates a <see cref="TickerRow"/> from metadata and historical candle data.
/// Produces identical metrics to the Tickers-tab hydration logic (ApplyMetadataToItem and ApplyTimeSeriesDataToItem).
/// </summary>
public static class TickerRowCalculator
{
    public static TickerRow Compute(string symbol, TickerMetadata? metadata, IReadOnlyList<CandleData>? lastBarsAscending)
    {
        string canonicalSymbol = TickerSymbol.Canonicalize(symbol);
        string name = string.IsNullOrEmpty(metadata?.ShortName) ? symbol : metadata.Value.ShortName;
        string sector = metadata?.Sector ?? string.Empty;
        string industry = metadata?.Industry ?? string.Empty;
        string? tag = metadata?.Tag;
        string? notes = null; // Notes is not populated by ApplyMetadataToItem (loaded in UI from UserStrategyMetadataRepository)
        string? reminder = null; // Reminder is not populated by ApplyMetadataToItem

        // Base fundamental fields copied from metadata
        decimal? returnOnEquity = metadata?.ReturnOnEquity;
        decimal? returnOnAssets = metadata?.ReturnOnAssets;
        decimal? grossMargins = metadata?.GrossMargins;
        decimal? operatingMargins = metadata?.OperatingMargins;
        decimal? profitMargins = metadata?.ProfitMargins;
        decimal? currentRatio = metadata?.CurrentRatio;
        decimal? debtToEquity = metadata?.DebtToEquity;
        decimal? ebitda = metadata?.Ebitda;
        decimal? freeCashflow = metadata?.FreeCashflow;
        decimal? operatingCashflow = metadata?.OperatingCashflow;
        decimal? trailingPE = metadata?.TrailingPE;
        decimal? forwardPE = metadata?.ForwardPE;
        decimal? priceToBook = metadata?.PriceToBook;
        decimal? trailingEps = metadata?.TrailingEps;
        decimal? forwardEps = metadata?.ForwardEps;
        decimal? bookValue = metadata?.BookValue;
        decimal? sharesOutstanding = metadata?.SharesOutstanding;
        decimal? floatShares = metadata?.FloatShares;
        decimal? shortRatio = metadata?.ShortRatio;
        decimal? shortPercentOfFloat = metadata?.ShortPercentOfFloat;
        decimal? heldPercentInsiders = metadata?.HeldPercentInsiders;
        decimal? heldPercentInstitutions = metadata?.HeldPercentInstitutions;
        string? longBusinessSummary = metadata?.LongBusinessSummary;
        long? fullTimeEmployees = metadata?.FullTimeEmployees;
        decimal? fiftyTwoWeekHigh = metadata?.FiftyTwoWeekHigh;
        decimal? fiftyTwoWeekLow = metadata?.FiftyTwoWeekLow;
        decimal? revenueGrowth = metadata?.RevenueGrowth;
        decimal? earningsGrowth = metadata?.EarningsGrowth;
        decimal? enterpriseValue = metadata?.EnterpriseValue;
        decimal? enterpriseToEbitda = metadata?.EnterpriseToEbitda;
        decimal? beta = metadata?.Beta;
        decimal? payoutRatio = metadata?.PayoutRatio;
        decimal? dividendRate = metadata?.DividendRate;
        decimal? dividendYield = metadata?.DividendYield;
        decimal? totalDebt = metadata?.TotalDebt;
        decimal? totalCash = metadata?.TotalCash;
        decimal? totalRevenue = metadata?.TotalRevenue;
        decimal? marketCap = metadata?.MarketCap;
        decimal? pbrCalculated = metadata?.PbrCalculated;
        decimal? dividendYieldCalculated = metadata?.DividendYieldCalculated;
        decimal? earningsYield = metadata?.EarningsYield;
        decimal? fcfYield = metadata?.FcfYield;
        decimal? fcfMargin = metadata?.FcfMargin;
        decimal? netDebt = metadata?.NetDebt;
        decimal? netDebtToEbitda = metadata?.NetDebtToEbitda;
        decimal? dividendCoverage = metadata?.DividendCoverage;
        decimal? pctFromFiftyTwoWeekHigh = metadata?.PctFromFiftyTwoWeekHigh;
        decimal? floatRatio = metadata?.FloatRatio;
        decimal? marketCapPerEmployee = metadata?.MarketCapPerEmployee;
        decimal? pegRatio = metadata?.PegRatio;
        decimal? operatingCashFlowYield = metadata?.OperatingCashFlowYield;
        decimal? netCashRatio = metadata?.NetCashRatio;
        decimal? priceToSalesTrailing12Months = metadata?.PriceToSalesTrailing12Months;
        decimal? enterpriseToRevenue = metadata?.EnterpriseToRevenue;
        decimal? ebitdaMargins = metadata?.EbitdaMargins;
        decimal? quickRatio = metadata?.QuickRatio;
        decimal? averageVolume = metadata?.AverageVolume;
        decimal? priceToCashFlowRatio = metadata?.PriceToCashFlowRatio;
        decimal? netDebtEquityRatio = metadata?.NetDebtEquityRatio;
        decimal? fiftyTwoWeekRangePosition = metadata?.FiftyTwoWeekRangePosition;
        decimal? averageTurnoverRate = metadata?.AverageTurnoverRate;
        decimal? averageFloatTurnover = metadata?.AverageFloatTurnover;

        // yfinance fields
        long? exDividendDate = metadata?.ExDividendDate;
        string? exchangeTimezoneName = metadata?.ExchangeTimezoneName;
        long? gmtOffSetMilliseconds = metadata?.GmtOffSetMilliseconds;
        long? lastFiscalYearEnd = metadata?.LastFiscalYearEnd;
        long? mostRecentQuarter = metadata?.MostRecentQuarter;
        long? numberOfAnalystOpinions = metadata?.NumberOfAnalystOpinions;
        string? quoteType = metadata?.QuoteType;
        string? recommendationKey = metadata?.RecommendationKey;
        decimal? recommendationMean = metadata?.RecommendationMean;
        string? region = metadata?.Region;
        decimal? targetHighPrice = metadata?.TargetHighPrice;
        decimal? targetLowPrice = metadata?.TargetLowPrice;
        decimal? targetMeanPrice = metadata?.TargetMeanPrice;
        decimal? targetMedianPrice = metadata?.TargetMedianPrice;
        DateTime? metadataLastUpdated = metadata?.MetadataLastUpdated;

        // Signal Flags & Strategy Prices
        decimal? longVal = metadata?.Long ?? metadata?.EntryPrice;
        decimal? exitLong = metadata?.ExitLong ?? metadata?.TargetPrice;
        decimal? stopLossLong = metadata?.StopLossLong ?? metadata?.StopLoss;
        decimal? shortVal = metadata?.Short;
        decimal? exitShort = metadata?.ExitShort;
        decimal? stopLossShort = metadata?.StopLossShort;
        bool? isLong = metadata?.IsLong;
        bool? isTPLong = metadata?.IsTPLong;
        bool? isSLLong = metadata?.IsSLLong;
        bool? isShort = metadata?.IsShort;
        bool? isTPShort = metadata?.IsTPShort;
        bool? isSLShort = metadata?.IsSLShort;

        // Market / Time-Series data
        decimal open = 0m;
        decimal high = 0m;
        decimal low = 0m;
        decimal close = 0m;
        long volume = 0;
        decimal change = 0m;
        double changePercent = 0.0;
        DateTimeOffset? lastUpdatedUtc = null;

        bool hasCandles = lastBarsAscending != null && lastBarsAscending.Count > 0;
        if (hasCandles)
        {
            var bars = lastBarsAscending!;
            bool isSorted = true;
            for (int i = 1; i < bars.Count; i++)
            {
                if (bars[i].Timestamp < bars[i - 1].Timestamp)
                {
                    isSorted = false;
                    break;
                }
            }

            var sorted = isSorted ? bars : bars.OrderBy(c => c.Timestamp).ToList();
            var latest = sorted[sorted.Count - 1];
            lastUpdatedUtc = new DateTimeOffset(latest.Timestamp, TimeSpan.Zero);
            open = latest.Open;
            high = latest.High;
            low = latest.Low;
            close = latest.Close;
            volume = latest.Volume;

            var pc = PriceChange.FromPreviousClose(sorted, sorted.Count - 1);
            change = pc.Amount;
            changePercent = (double)pc.Percent;
        }
        else
        {
            // Close fallback from CurrentPrice when no candle data is provided
            if (metadata.HasValue && close == 0 && metadata.Value.CurrentPrice.HasValue && metadata.Value.CurrentPrice.Value != 0)
            {
                close = metadata.Value.CurrentPrice.Value;
                if (metadata.Value.LastClose.HasValue && metadata.Value.LastClose.Value != 0)
                {
                    try
                    {
                        decimal lastClose = metadata.Value.LastClose.Value;
                        decimal diff = close - lastClose;
                        double pct = (double)(diff / lastClose * 100m);
                        change = diff;
                        changePercent = pct;
                    }
                    catch (OverflowException)
                    {
                        change = 0m;
                        changePercent = 0.0;
                    }
                }
            }
        }

        // Daily turnover rates
        decimal? dailyTurnoverRate = FundamentalsCalculator.ComputeTurnoverRate(volume, sharesOutstanding);
        decimal? dailyFloatShareTurnoverRatio = FundamentalsCalculator.ComputeTurnoverRate(volume, floatShares);

        // Recalculate 13 price-dependent metrics from latest Close when close != 0 and candles were provided (ApplyTimeSeriesDataToItem)
        if (hasCandles && close != 0)
        {
            // 1. Market Cap
            if (sharesOutstanding.HasValue && sharesOutstanding.Value != 0)
            {
                marketCap = FundamentalsCalculator.ComputeMarketCap(close, sharesOutstanding, requireNonZeroShares: true);
            }

            // 2. PBR
            if (bookValue.HasValue && bookValue.Value != 0)
            {
                pbrCalculated = FundamentalsCalculator.ComputePbr(close, bookValue);
            }

            // 3. Dividend Yield
            if (dividendRate.HasValue)
            {
                dividendYieldCalculated = FundamentalsCalculator.ComputeDividendYield(dividendRate, close);
            }

            // 4. Earnings Yield
            if (trailingEps.HasValue)
            {
                earningsYield = FundamentalsCalculator.ComputeEarningsYield(trailingEps, close);
            }

            // 5. FCF Yield
            if (freeCashflow.HasValue && marketCap.HasValue && marketCap.Value != 0)
            {
                fcfYield = FundamentalsCalculator.ComputeFcfYield(freeCashflow, marketCap);
            }

            // 6. % From 52 Week High
            if (fiftyTwoWeekHigh.HasValue && fiftyTwoWeekHigh.Value != 0)
            {
                pctFromFiftyTwoWeekHigh = FundamentalsCalculator.ComputePctFromFiftyTwoWeekHigh(close, fiftyTwoWeekHigh);
            }

            // 7. Market Cap Per Employee
            if (marketCap.HasValue && fullTimeEmployees.HasValue && fullTimeEmployees.Value != 0)
            {
                marketCapPerEmployee = FundamentalsCalculator.ComputeMarketCapPerEmployee(marketCap, fullTimeEmployees);
            }

            // 8. Operating Cash Flow Yield
            if (operatingCashflow.HasValue && marketCap.HasValue && marketCap.Value != 0)
            {
                operatingCashFlowYield = FundamentalsCalculator.ComputeOperatingCashFlowYield(operatingCashflow, marketCap);
            }

            // 9. Net Cash Ratio (Unified 100x percent per Option A)
            netCashRatio = FundamentalsCalculator.ComputeNetCashRatio(totalCash, totalDebt, marketCap);

            // 10. Price to Cash Flow Ratio
            if (operatingCashflow.HasValue && marketCap.HasValue && operatingCashflow.Value != 0)
            {
                priceToCashFlowRatio = FundamentalsCalculator.ComputePriceToCashFlowRatio(marketCap, operatingCashflow);
            }

            // 11. 52-Week Range Position
            fiftyTwoWeekRangePosition = FundamentalsCalculator.ComputeFiftyTwoWeekRangePosition(close, fiftyTwoWeekLow, fiftyTwoWeekHigh);

            // 12. Trailing P/E
            if (trailingEps.HasValue && trailingEps.Value != 0)
            {
                trailingPE = FundamentalsCalculator.ComputePeRatio(close, trailingEps);
            }

            // 13. Forward P/E
            if (forwardEps.HasValue && forwardEps.Value != 0)
            {
                forwardPE = FundamentalsCalculator.ComputePeRatio(close, forwardEps);
            }
        }

        return new TickerRow
        {
            Symbol = canonicalSymbol,
            Name = name,
            Sector = sector,
            Industry = industry,
            Open = open,
            High = high,
            Low = low,
            Close = close,
            Volume = volume,
            Change = change,
            ChangePercent = changePercent,
            LastUpdatedUtc = lastUpdatedUtc,
            ReturnOnEquity = returnOnEquity,
            ReturnOnAssets = returnOnAssets,
            GrossMargins = grossMargins,
            OperatingMargins = operatingMargins,
            ProfitMargins = profitMargins,
            CurrentRatio = currentRatio,
            DebtToEquity = debtToEquity,
            Ebitda = ebitda,
            FreeCashflow = freeCashflow,
            OperatingCashflow = operatingCashflow,
            TrailingPE = trailingPE,
            ForwardPE = forwardPE,
            PriceToBook = priceToBook,
            TrailingEps = trailingEps,
            ForwardEps = forwardEps,
            BookValue = bookValue,
            SharesOutstanding = sharesOutstanding,
            FloatShares = floatShares,
            ShortRatio = shortRatio,
            ShortPercentOfFloat = shortPercentOfFloat,
            HeldPercentInsiders = heldPercentInsiders,
            HeldPercentInstitutions = heldPercentInstitutions,
            LongBusinessSummary = longBusinessSummary,
            FullTimeEmployees = fullTimeEmployees,
            FiftyTwoWeekHigh = fiftyTwoWeekHigh,
            FiftyTwoWeekLow = fiftyTwoWeekLow,
            RevenueGrowth = revenueGrowth,
            EarningsGrowth = earningsGrowth,
            EnterpriseValue = enterpriseValue,
            EnterpriseToEbitda = enterpriseToEbitda,
            Beta = beta,
            PayoutRatio = payoutRatio,
            DividendRate = dividendRate,
            DividendYield = dividendYield,
            TotalDebt = totalDebt,
            TotalCash = totalCash,
            TotalRevenue = totalRevenue,
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
            PriceToSalesTrailing12Months = priceToSalesTrailing12Months,
            EnterpriseToRevenue = enterpriseToRevenue,
            EbitdaMargins = ebitdaMargins,
            QuickRatio = quickRatio,
            AverageVolume = averageVolume,
            PriceToCashFlowRatio = priceToCashFlowRatio,
            NetDebtEquityRatio = netDebtEquityRatio,
            FiftyTwoWeekRangePosition = fiftyTwoWeekRangePosition,
            DailyTurnoverRate = dailyTurnoverRate,
            AverageTurnoverRate = averageTurnoverRate,
            DailyFloatShareTurnoverRatio = dailyFloatShareTurnoverRatio,
            AverageFloatTurnover = averageFloatTurnover,
            ExDividendDate = exDividendDate,
            ExchangeTimezoneName = exchangeTimezoneName,
            GmtOffSetMilliseconds = gmtOffSetMilliseconds,
            LastFiscalYearEnd = lastFiscalYearEnd,
            MostRecentQuarter = mostRecentQuarter,
            NumberOfAnalystOpinions = numberOfAnalystOpinions,
            QuoteType = quoteType,
            RecommendationKey = recommendationKey,
            RecommendationMean = recommendationMean,
            Region = region,
            TargetHighPrice = targetHighPrice,
            TargetLowPrice = targetLowPrice,
            TargetMeanPrice = targetMeanPrice,
            TargetMedianPrice = targetMedianPrice,
            MetadataLastUpdated = metadataLastUpdated,
            Tag = tag,
            Notes = notes,
            Reminder = reminder,
            Long = longVal,
            ExitLong = exitLong,
            StopLossLong = stopLossLong,
            Short = shortVal,
            ExitShort = exitShort,
            StopLossShort = stopLossShort,
            IsLong = isLong,
            IsTPLong = isTPLong,
            IsSLLong = isSLLong,
            IsShort = isShort,
            IsTPShort = isTPShort,
            IsSLShort = isSLShort,
        };
    }


}
