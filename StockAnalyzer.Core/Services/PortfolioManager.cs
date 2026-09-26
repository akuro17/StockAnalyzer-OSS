using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models.Portfolio;

namespace StockAnalyzer.Core.Services;

public class PortfolioManager : IPortfolioManager
{
    private readonly IUserPortfolioRepository _portfolioRepository;
    private readonly ILogger<PortfolioManager> _logger;

    public PortfolioManager(
        IUserPortfolioRepository? portfolioRepository = null,
        ILogger<PortfolioManager>? logger = null)
    {
        _portfolioRepository = portfolioRepository ?? NullUserPortfolioRepository.Instance;
        _logger = logger ?? NullLogger<PortfolioManager>.Instance;
    }

    private sealed class OpenLot
    {
        public Transaction Transaction { get; }
        public decimal RemainingQuantity { get; set; }

        public OpenLot(Transaction transaction)
        {
            Transaction = transaction;
            RemainingQuantity = transaction.Quantity;
        }
    }

    private sealed class ProcessResult
    {
        public decimal Cash { get; set; }
        public Dictionary<CurrencyCode, decimal> CashBalances { get; set; } = new();
        public Dictionary<string, Position> Positions { get; set; } = new();
        public List<ClosedPosition> ClosedPositions { get; set; } = new();
        public decimal TotalRealizedPnL { get; set; }
        public Dictionary<string, List<OpenLot>> OpenLots { get; set; } = new();
    }

    private ProcessResult ProcessTransactions(decimal initialCash, IEnumerable<Transaction> transactions)
    {
        var result = new ProcessResult();
        result.Cash = initialCash;
        result.CashBalances[CurrencyCode.JPY] = initialCash;

        void AdjustCash(CurrencyCode currency, decimal amount)
        {
            if (currency == CurrencyCode.JPY)
            {
                result.Cash += amount;
            }
            result.CashBalances[currency] = result.CashBalances.GetValueOrDefault(currency, 0m) + amount;
        }

        var sorted = transactions.OrderBy(t => t.ExecutedAt).ToList();
        bool isMultiCurrency = sorted.Any(t => t.Price.Currency == CurrencyCode.USD);

        for (int i = 0; i < sorted.Count; i++)
        {
            var transaction = sorted[i];
            switch (transaction.Type)
            {
                case TransactionType.Deposit:
                    AdjustCash(transaction.Price.Currency, transaction.CashAmount);
                    break;

                case TransactionType.Withdrawal:
                    AdjustCash(transaction.Price.Currency, -transaction.CashAmount);
                    if (result.CashBalances[transaction.Price.Currency] < 0)
                    {
                        throw new InvalidOperationException($"Insufficient funds for withdrawal of {transaction.Price.Currency}.");
                    }
                    break;

                case TransactionType.Long:
                    {
                        var currency = transaction.Price.Currency;
                        var totalCost = (transaction.Quantity * transaction.Price.Amount) + transaction.Commission.Amount;
                        AdjustCash(currency, -totalCost);

                        var key = transaction.Ticker!;
                        if (result.Positions.TryGetValue(key, out var existing))
                        {
                            var totalQty = existing.Quantity + transaction.Quantity;
                            var newAvg = ((existing.Quantity * existing.AverageCostPerUnit) + (transaction.Quantity * transaction.Price.Amount)) / totalQty;
                            result.Positions[key] = new Position(transaction.Ticker!, totalQty, newAvg, averageCost: new Money(newAvg, currency));
                        }
                        else
                        {
                            result.Positions[key] = new Position(transaction.Ticker!, transaction.Quantity, transaction.Price.Amount, averageCost: transaction.Price);
                        }

                        if (!result.OpenLots.TryGetValue(key, out var lots))
                        {
                            lots = new List<OpenLot>();
                            result.OpenLots[key] = lots;
                        }
                        lots.Add(new OpenLot(transaction));
                    }
                    break;

                case TransactionType.Short:
                    {
                        var currency = transaction.Price.Currency;
                        var totalProceeds = (transaction.Quantity * transaction.Price.Amount) - transaction.Commission.Amount;
                        AdjustCash(currency, totalProceeds);

                        var key = transaction.Ticker! + "_Short";
                        if (result.Positions.TryGetValue(key, out var existing))
                        {
                            var totalQty = existing.Quantity + transaction.Quantity;
                            var newAvg = ((existing.Quantity * existing.AverageCostPerUnit) + (transaction.Quantity * transaction.Price.Amount)) / totalQty;
                            result.Positions[key] = new Position(transaction.Ticker!, totalQty, newAvg, isShort: true, averageCost: new Money(newAvg, currency));
                        }
                        else
                        {
                            result.Positions[key] = new Position(transaction.Ticker!, transaction.Quantity, transaction.Price.Amount, isShort: true, averageCost: transaction.Price);
                        }

                        if (!result.OpenLots.TryGetValue(key, out var lots))
                        {
                            lots = new List<OpenLot>();
                            result.OpenLots[key] = lots;
                        }
                        lots.Add(new OpenLot(transaction));
                    }
                    break;

                case TransactionType.ExitLong:
                case TransactionType.ExitShort:
                    {
                        var currency = transaction.Price.Currency;
                        var key = transaction.Ticker! + (transaction.Type == TransactionType.ExitShort ? "_Short" : "");
                        if (!result.Positions.TryGetValue(key, out var existing) || existing.Quantity < transaction.Quantity)
                        {
                            _logger.LogWarning("Insufficient shares to sell/exit for {Ticker}. Required: {Required}, Current: {Current}", transaction.Ticker, transaction.Quantity, existing?.Quantity ?? 0);
                            throw new InvalidOperationException("Insufficient shares to sell/exit.");
                        }

                        if (existing.IsShort)
                        {
                            var totalCoverCost = (transaction.Quantity * transaction.Price.Amount) + transaction.Commission.Amount;
                            AdjustCash(currency, -totalCoverCost);
                        }
                        else
                        {
                            var totalProceeds = (transaction.Quantity * transaction.Price.Amount) - transaction.Commission.Amount;
                            AdjustCash(currency, totalProceeds);
                        }

                        if (!result.OpenLots.TryGetValue(key, out var lots) || lots.Count == 0)
                        {
                            throw new InvalidOperationException("No open lots to exit.");
                        }

                        decimal exitQtyRemaining = transaction.Quantity;

                        if (transaction.RelatedTransactionId.HasValue)
                        {
                            var targetLot = lots.FirstOrDefault(l => l.Transaction.Id == transaction.RelatedTransactionId.Value);
                            if (targetLot != null)
                            {
                                var matchQty = Math.Min(exitQtyRemaining, targetLot.RemainingQuantity);
                                if (matchQty > 0)
                                {
                                    var entryFeeAllocated = Math.Round(targetLot.Transaction.Fee * (matchQty / targetLot.Transaction.Quantity), 4);
                                    var exitFeeAllocated = Math.Round(transaction.Fee * (matchQty / transaction.Quantity), 4);

                                    decimal pnl = 0;
                                    if (existing.IsShort)
                                    {
                                        pnl = matchQty * (targetLot.Transaction.PricePerUnit - transaction.PricePerUnit) - exitFeeAllocated - entryFeeAllocated;
                                    }
                                    else
                                    {
                                        pnl = matchQty * (transaction.PricePerUnit - targetLot.Transaction.PricePerUnit) - exitFeeAllocated - entryFeeAllocated;
                                    }

                                    result.ClosedPositions.Add(new ClosedPosition(
                                        Guid.NewGuid(),
                                        transaction.Ticker!,
                                        targetLot.Transaction.Type,
                                        matchQty,
                                        targetLot.Transaction.PricePerUnit,
                                        transaction.PricePerUnit,
                                        targetLot.Transaction.ExecutedAt,
                                        transaction.ExecutedAt,
                                        pnl,
                                        exitFeeAllocated + entryFeeAllocated
                                    ));
                                    decimal pnlInBase = pnl;
                                    if (transaction.Price.Currency != CurrencyCode.USD)
                                    {
                                        if (isMultiCurrency && !transaction.AppliedRate.HasValue)
                                        {
                                            throw new InvalidOperationException($"Applied rate must be specified for non-USD exit transaction {transaction.Id} to calculate realized PnL in base currency USD.");
                                        }
                                        if (transaction.AppliedRate.HasValue)
                                        {
                                            pnlInBase = transaction.AppliedRate.Value.Convert(new Money(pnl, transaction.Price.Currency)).Amount;
                                        }
                                    }
                                    result.TotalRealizedPnL += pnlInBase;

                                    targetLot.RemainingQuantity -= matchQty;
                                    exitQtyRemaining -= matchQty;

                                    if (targetLot.RemainingQuantity <= 0)
                                    {
                                        lots.Remove(targetLot);
                                    }
                                }
                            }
                        }

                        while (exitQtyRemaining > 0 && lots.Count > 0)
                        {
                            var oldestLot = lots[0];
                            var matchQty = Math.Min(exitQtyRemaining, oldestLot.RemainingQuantity);

                            var entryFeeAllocated = Math.Round(oldestLot.Transaction.Fee * (matchQty / oldestLot.Transaction.Quantity), 4);
                            var exitFeeAllocated = Math.Round(transaction.Fee * (matchQty / transaction.Quantity), 4);

                            decimal pnl = 0;
                            if (existing.IsShort)
                            {
                                pnl = matchQty * (oldestLot.Transaction.PricePerUnit - transaction.PricePerUnit) - exitFeeAllocated - entryFeeAllocated;
                            }
                            else
                            {
                                pnl = matchQty * (transaction.PricePerUnit - oldestLot.Transaction.PricePerUnit) - exitFeeAllocated - entryFeeAllocated;
                            }

                            result.ClosedPositions.Add(new ClosedPosition(
                                Guid.NewGuid(),
                                transaction.Ticker!,
                                oldestLot.Transaction.Type,
                                matchQty,
                                oldestLot.Transaction.PricePerUnit,
                                transaction.PricePerUnit,
                                oldestLot.Transaction.ExecutedAt,
                                transaction.ExecutedAt,
                                pnl,
                                exitFeeAllocated + entryFeeAllocated
                            ));
                            decimal pnlInBase = pnl;
                            if (transaction.Price.Currency != CurrencyCode.USD)
                            {
                                if (isMultiCurrency && !transaction.AppliedRate.HasValue)
                                {
                                    throw new InvalidOperationException($"Applied rate must be specified for non-USD exit transaction {transaction.Id} to calculate realized PnL in base currency USD.");
                                }
                                if (transaction.AppliedRate.HasValue)
                                {
                                    pnlInBase = transaction.AppliedRate.Value.Convert(new Money(pnl, transaction.Price.Currency)).Amount;
                                }
                            }
                            result.TotalRealizedPnL += pnlInBase;

                            oldestLot.RemainingQuantity -= matchQty;
                            exitQtyRemaining -= matchQty;

                            if (oldestLot.RemainingQuantity <= 0)
                            {
                                lots.RemoveAt(0);
                            }
                        }

                        if (exitQtyRemaining > 0)
                        {
                            throw new InvalidOperationException("Insufficient open lots matching the exit quantity.");
                        }

                        key = transaction.Ticker! + (existing.IsShort ? "_Short" : "");
                        var remainingQty = existing.Quantity - transaction.Quantity;
                        if (remainingQty == 0)
                        {
                            result.Positions.Remove(key);
                            result.OpenLots.Remove(key);
                        }
                        else
                        {
                            result.Positions[key] = new Position(transaction.Ticker!, remainingQty, existing.AverageCostPerUnit, existing.IsShort, averageCost: existing.AverageCost);
                        }
                    }
                    break;

                default:
                    throw new NotSupportedException($"Transaction type {transaction.Type} is not supported.");
            }
        }

        return result;
    }

    public Portfolio RebuildPortfolio(decimal initialCash, IReadOnlyList<Transaction> transactions)
    {
        var result = ProcessTransactions(initialCash, transactions);
        return new Portfolio(
            result.Cash,
            result.Positions.ToImmutableDictionary(),
            transactions.OrderBy(t => t.ExecutedAt).ToImmutableList(),
            result.ClosedPositions.ToImmutableList(),
            result.TotalRealizedPnL,
            cashBalances: result.CashBalances.ToImmutableDictionary()
        );
    }

    public Portfolio ApplyTransaction(Portfolio current, Transaction transaction)
    {
        var nextHistory = current.History.Concat(new[] { transaction }).ToList();
        
        var oldResult = ProcessTransactions(0m, current.History);
        var newResult = ProcessTransactions(0m, nextHistory);
        
        decimal finalCash = current.CashBalance + (newResult.Cash - oldResult.Cash);
        
        var finalCashBalances = new Dictionary<CurrencyCode, decimal>(current.CashBalances);
        foreach (var kvp in newResult.CashBalances)
        {
            var diff = kvp.Value - oldResult.CashBalances.GetValueOrDefault(kvp.Key, 0m);
            if (diff != 0)
            {
                finalCashBalances[kvp.Key] = finalCashBalances.GetValueOrDefault(kvp.Key, 0m) + diff;
            }
        }

        return new Portfolio(
            finalCash,
            newResult.Positions.ToImmutableDictionary(),
            nextHistory.ToImmutableList(),
            newResult.ClosedPositions.ToImmutableList(),
            newResult.TotalRealizedPnL,
            cashBalances: finalCashBalances.ToImmutableDictionary()
        );
    }

    public PortfolioEvaluationResult Evaluate(Portfolio portfolio, IReadOnlyDictionary<string, decimal> latestPrices)
    {
        var positionValues = new Dictionary<string, decimal>();
        var positionPLs = new Dictionary<string, decimal>();
        var metrics = Calculate(portfolio, latestPrices, positionValues, positionPLs);

        return new PortfolioEvaluationResult(
            metrics,
            positionValues.ToImmutableDictionary(),
            positionPLs.ToImmutableDictionary()
        );
    }

    public PortfolioEvaluationResult Evaluate(
        Portfolio portfolio, 
        IReadOnlyDictionary<string, decimal> latestPrices,
        IReadOnlyDictionary<CurrencyCode, ExchangeRate> latestRates,
        CurrencyCode targetBaseCurrency)
    {
        var positionValues = new Dictionary<string, decimal>();
        var positionPLs = new Dictionary<string, decimal>();
        var metrics = Calculate(portfolio, latestPrices, latestRates, targetBaseCurrency, positionValues, positionPLs);

        return new PortfolioEvaluationResult(
            metrics,
            positionValues.ToImmutableDictionary(),
            positionPLs.ToImmutableDictionary()
        );
    }

    public PortfolioMetrics GetMetrics(Portfolio portfolio, IReadOnlyDictionary<string, decimal> latestPrices)
    {
        return Calculate(portfolio, latestPrices, null, null);
    }

    public PortfolioMetrics GetMetrics(
        Portfolio portfolio, 
        IReadOnlyDictionary<string, decimal> latestPrices,
        IReadOnlyDictionary<CurrencyCode, ExchangeRate> latestRates,
        CurrencyCode targetBaseCurrency)
    {
        return Calculate(portfolio, latestPrices, latestRates, targetBaseCurrency, null, null);
    }

    /// <summary>
    /// Core calculation logic for portfolio valuation. 
    /// Optionally populates detail dictionaries if they are not null.
    /// </summary>
    private PortfolioMetrics Calculate(
        Portfolio portfolio, 
        IReadOnlyDictionary<string, decimal> latestPrices, 
        Dictionary<string, decimal>? positionValues, 
        Dictionary<string, decimal>? positionPLs)
    {
        decimal totalPositionValue = 0;
        decimal totalUnrealizedPL = 0;

        foreach (var kvp in portfolio.Positions)
        {
            var key = kvp.Key;
            var position = kvp.Value;
            decimal currentPrice;
            decimal pl = 0;
            
            if (latestPrices.TryGetValue(position.Ticker, out var price))
            {
                currentPrice = price;
                pl = position.IsShort 
                    ? position.Quantity * (position.AverageCostPerUnit - currentPrice)
                    : position.Quantity * (currentPrice - position.AverageCostPerUnit);
                totalUnrealizedPL += pl;
            }
            else
            {
                currentPrice = position.AverageCostPerUnit;
            }

            var value = position.IsShort 
                ? -position.Quantity * currentPrice 
                : position.Quantity * currentPrice;
            totalPositionValue += value;

            if (positionValues != null) positionValues[key] = value;
            if (positionPLs != null) positionPLs[key] = pl;
        }

        var totalValue = portfolio.CashBalance + totalPositionValue;
        var cashRatio = totalValue == 0 ? 0 : (portfolio.CashBalance / totalValue) * 100;

        return new PortfolioMetrics(
            totalValue,
            totalUnrealizedPL,
            portfolio.TotalRealizedPnL,
            portfolio.CashBalance,
            cashRatio
        );
    }

    private PortfolioMetrics Calculate(
        Portfolio portfolio, 
        IReadOnlyDictionary<string, decimal> latestPrices, 
        IReadOnlyDictionary<CurrencyCode, ExchangeRate> latestRates,
        CurrencyCode targetBaseCurrency,
        Dictionary<string, decimal>? positionValues, 
        Dictionary<string, decimal>? positionPLs)
    {
        decimal totalPositionValueInBase = 0;
        decimal totalUnrealizedPLInBase = 0;

        foreach (var kvp in portfolio.Positions)
        {
            var key = kvp.Key;
            var position = kvp.Value;
            var assetCurrency = position.AverageCost.Currency;
            decimal currentPriceLocal;
            
            if (latestPrices.TryGetValue(position.Ticker, out var price))
            {
                currentPriceLocal = price;
            }
            else
            {
                currentPriceLocal = position.AverageCostPerUnit;
            }

            // Calculate valuation and PnL in asset's native currency
            var marketValueLocal = position.IsShort 
                ? -position.Quantity * currentPriceLocal 
                : position.Quantity * currentPriceLocal;

            var plLocal = position.IsShort 
                ? position.Quantity * (position.AverageCostPerUnit - currentPriceLocal)
                : position.Quantity * (currentPriceLocal - position.AverageCostPerUnit);

            // Convert to target base currency
            decimal marketValueBase = 0;
            decimal plBase = 0;

            if (assetCurrency == targetBaseCurrency)
            {
                marketValueBase = marketValueLocal;
                plBase = plLocal;
            }
            else if (latestRates.TryGetValue(assetCurrency, out var rate))
            {
                marketValueBase = rate.Convert(new Money(marketValueLocal, assetCurrency)).Amount;
                plBase = rate.Convert(new Money(plLocal, assetCurrency)).Amount;
            }
            else
            {
                // Fallback to Mid rate assumes 1
                marketValueBase = marketValueLocal;
                plBase = plLocal;
            }

            totalPositionValueInBase += marketValueBase;
            totalUnrealizedPLInBase += plBase;

            if (positionValues != null) positionValues[key] = marketValueBase;
            if (positionPLs != null) positionPLs[key] = plBase;
        }

        // Calculate Cash in target base currency
        decimal totalCashInBase = 0;
        foreach (var kvp in portfolio.CashBalances)
        {
            var currency = kvp.Key;
            var balance = kvp.Value;

            if (currency == targetBaseCurrency)
            {
                totalCashInBase += balance;
            }
            else if (latestRates.TryGetValue(currency, out var rate))
            {
                totalCashInBase += rate.Convert(new Money(balance, currency)).Amount;
            }
            else
            {
                totalCashInBase += balance;
            }
        }

        var totalValueInBase = totalCashInBase + totalPositionValueInBase;
        var cashRatio = totalValueInBase == 0 ? 0 : (totalCashInBase / totalValueInBase) * 100;

        decimal totalRealizedPLInBase = 0;
        if (targetBaseCurrency == CurrencyCode.USD)
        {
            totalRealizedPLInBase = portfolio.TotalRealizedPnL;
        }
        else if (latestRates.TryGetValue(CurrencyCode.USD, out var rate))
        {
            totalRealizedPLInBase = rate.Convert(new Money(portfolio.TotalRealizedPnL, CurrencyCode.USD)).Amount;
        }
        else
        {
            totalRealizedPLInBase = portfolio.TotalRealizedPnL;
        }

        return new PortfolioMetrics(
            totalValueInBase,
            totalUnrealizedPLInBase,
            totalRealizedPLInBase,
            totalCashInBase,
            cashRatio
        );
    }

    public async Task<AllocationResult> GetAllocationAsync(
        Portfolio portfolio, 
        IReadOnlyDictionary<string, decimal> latestPrices, 
        IMarketDataProvider marketDataProvider)
    {
        decimal totalEquityValue = 0;
        var sectorValueMap = new Dictionary<string, decimal>();

        // 1. Fetch metadata and accumulate sector values in parallel
        var tasks = portfolio.Positions.Values.Select(async position =>
        {
            if (!latestPrices.TryGetValue(position.Ticker, out var price))
            {
                price = position.AverageCostPerUnit;
            }

            var marketValue = position.Quantity * price;
            var metadata = await marketDataProvider.GetMetadataAsync(position.Ticker);
            var sector = string.IsNullOrEmpty(metadata.Sector) ? StockAnalyzer.Core.Constants.LayoutConstants.CategoryUnknown : metadata.Sector;

            return (Sector: sector, Value: marketValue);
        });

        var posResults = await Task.WhenAll(tasks);

        foreach (var res in posResults)
        {
            totalEquityValue += res.Value;
            if (!sectorValueMap.TryGetValue(res.Sector, out var current)) current = 0;
            sectorValueMap[res.Sector] = current + res.Value;
        }

        var totalValue = totalEquityValue + portfolio.CashBalance;

        // 2. Perform LRM for Asset Allocations (Equity vs Cash)
        var assetRaw = new List<(string Category, decimal Value)> 
        { 
            (StockAnalyzer.Core.Constants.LayoutConstants.CategoryEquity, totalEquityValue), 
            (StockAnalyzer.Core.Constants.LayoutConstants.CategoryCash, portfolio.CashBalance) 
        };
        var assetAllocations = ApplyLrm(assetRaw, totalValue);

        // 3. Perform LRM for Sector Allocations
        var sectorRaw = sectorValueMap
            .Select(kvp => (Category: kvp.Key, Value: kvp.Value))
            .ToList();
        
        if (portfolio.CashBalance > 0)
        {
            sectorRaw.Add((Category: StockAnalyzer.Core.Constants.LayoutConstants.CategoryCash, Value: portfolio.CashBalance));
        }

        var sectorAllocations = ApplyLrm(sectorRaw, totalValue);

        return new AllocationResult(sectorAllocations, assetAllocations, totalValue);
    }

    public async Task<IReadOnlyList<HeatmapEntry>> GetPerformanceHeatmapAsync(
        Portfolio portfolio,
        PerformancePeriod period,
        IMarketDataProvider marketDataProvider,
        CancellationToken cancellationToken = default)
    {
        var symbols = portfolio.Positions.Keys.ToList();
        if (symbols.Count == 0) return Array.Empty<HeatmapEntry>();

        var latestPrices = await marketDataProvider.GetLatestPricesAsync(symbols);
        var entries = new List<HeatmapEntry>();
        decimal totalValue = 0;

        // First pass: Calculate current values and total for weights
        var posValues = new Dictionary<string, decimal>();
        foreach (var symbol in symbols)
        {
            var pos = portfolio.Positions[symbol];
            if (latestPrices.TryGetValue(symbol, out var price))
            {
                var val = pos.Quantity * price;
                posValues[symbol] = val;
                totalValue += val;
            }
        }

        if (totalValue == 0) return Array.Empty<HeatmapEntry>();

        // Pre-fetch ALL ticker data to avoid N×lock-acquire pattern.
        // Each GetTickersDataAsync call acquires/releases the DB lock once,
        // but we yield between calls to prevent lock starvation of other consumers.
        var allTickerData = new Dictionary<string, IReadOnlyList<CandleData>>();
        foreach (var symbol in symbols)
        {
            if (cancellationToken.IsCancellationRequested) break;
            allTickerData[symbol] = await marketDataProvider.GetTickersDataAsync(symbol, TimeFrame.D1);
            // Yield to allow other DB consumers (Chart Load, PortfolioSummary) to acquire the lock
            await Task.Yield();
        }

        // Second pass: Compute entries from pre-fetched data (no further DB access needed)
        foreach (var symbol in symbols)
        {
            if (cancellationToken.IsCancellationRequested) break;

            var pos = portfolio.Positions[symbol];
            if (!latestPrices.TryGetValue(symbol, out var endPrice)) continue;

            var metadata = await marketDataProvider.GetMetadataAsync(symbol);
            var startPrice = GetStartPriceFromData(allTickerData.GetValueOrDefault(symbol), period);

            float ret = 0;
            if (startPrice > 0)
            {
                ret = (float)((endPrice - startPrice) / startPrice);
            }
            else
            {
                ret = float.NaN;
            }

            float weight = totalValue > 0 ? (float)(posValues[symbol] / totalValue) : 0;

            entries.Add(new HeatmapEntry(
                Ticker: symbol,
                Region: string.IsNullOrEmpty(metadata.Region) ? StockAnalyzer.Core.Constants.LayoutConstants.CategoryUnknown : metadata.Region,
                Sector: string.IsNullOrEmpty(metadata.Sector) ? StockAnalyzer.Core.Constants.LayoutConstants.CategoryUnknown : metadata.Sector,
                Return: ret,
                Weight: weight
            ));
        }

        return entries;
    }

    private async Task<decimal> GetStartPriceAsync(IMarketDataProvider provider, string symbol, PerformancePeriod period)
    {
        var data = await provider.GetTickersDataAsync(symbol, TimeFrame.D1);
        return GetStartPriceFromData(data, period);
    }

    /// <summary>
    /// Computes the start price from pre-fetched candle data. Pure in-memory computation, no DB access.
    /// </summary>
    private static decimal GetStartPriceFromData(IReadOnlyList<CandleData>? data, PerformancePeriod period)
    {
        if (data == null || data.Count == 0) return 0;

        int index = -1;
        var now = DateTime.Now;
        switch (period)
        {
            case PerformancePeriod.OneDay:
                // Previous trading day's close
                index = data.Count - 2;
                break;
            case PerformancePeriod.FiveDays:
                // 5 trading days ago close
                index = data.Count - 6;
                break;
            case PerformancePeriod.MonthToDate:
                // Last business day of previous month
                var firstOfMonth = new DateTime(now.Year, now.Month, 1);
                index = FindNearestIndex(data, firstOfMonth) - 1;
                break;
            case PerformancePeriod.YearToDate:
                // Last business day of previous year
                var firstOfYear = new DateTime(now.Year, 1, 1);
                index = FindNearestIndex(data, firstOfYear) - 1;
                break;
            case PerformancePeriod.OneYear:
                // Close on same day 1 year ago (or nearest preceding business day)
                var yearAgo = now.AddYears(-1);
                index = FindNearestIndex(data, yearAgo) - 1;
                break;
        }

        if (index < 0) index = 0;
        if (index >= data.Count) return 0;

        return data[index].Close;
    }

    private static int FindNearestIndex(IReadOnlyList<CandleData> data, DateTime target)
    {
        for (int i = 0; i < data.Count; i++)
        {
            if (data[i].Timestamp >= target) return i;
        }
        return data.Count;
    }

    private static IReadOnlyList<AllocationEntry> ApplyLrm(IEnumerable<(string Category, decimal Value)> rawEntries, decimal totalValue)
    {
        if (totalValue <= 0) return Array.Empty<AllocationEntry>();

        // Target 100.00% as 10000 units
        const int targetUnits = 10000;
        
        var temp = rawEntries.Select(e => {
            decimal exactUnits = (e.Value / totalValue) * targetUnits;
            int floorUnits = (int)Math.Floor(exactUnits);
            decimal remainder = exactUnits - floorUnits;
            return new { Category = e.Category, Value = e.Value, Units = floorUnits, Remainder = remainder };
        }).OrderByDescending(x => x.Remainder).ToList();

        int currentSum = temp.Sum(x => x.Units);
        int deficit = targetUnits - currentSum;

        // Distribute deficit to highest remainders
        var finalized = new List<AllocationEntry>(temp.Count);
        for (int i = 0; i < temp.Count; i++)
        {
            int units = temp[i].Units;
            if (i < deficit) units++;
            
            finalized.Add(new AllocationEntry(temp[i].Category, temp[i].Value, units / 100m));
        }

        // Return in original order (or sorted by market value) - let's sort by market value for better UX
        return finalized.OrderByDescending(a => a.MarketValue).ToList();
    }

    public ValueTask<Portfolio> LoadPortfolioAsync(CancellationToken ct = default)
    {
        return _portfolioRepository.LoadPortfolioAsync(ct);
    }

    public ValueTask SavePortfolioAsync(Portfolio portfolio, CancellationToken ct = default)
    {
        return _portfolioRepository.SavePortfolioAsync(portfolio, ct);
    }
}
