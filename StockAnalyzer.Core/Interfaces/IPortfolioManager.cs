using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models.Portfolio;

namespace StockAnalyzer.Core.Interfaces;

public interface IPortfolioManager
{
    /// <summary>
    /// Applies a transaction to the current portfolio and returns a new updated portfolio instance.
    /// </summary>
    Portfolio ApplyTransaction(Portfolio current, Transaction transaction);

    /// <summary>
    /// Rebuilds the entire portfolio state in memory by sequentially re-applying sorted transactions.
    /// </summary>
    Portfolio RebuildPortfolio(decimal initialCash, IReadOnlyList<Transaction> transactions);

    /// <summary>
    /// Evaluates the portfolio's current value and performance based on the provided latest prices.
    /// </summary>
    PortfolioEvaluationResult Evaluate(Portfolio portfolio, IReadOnlyDictionary<string, decimal> latestPrices);

    /// <summary>
    /// Evaluates the portfolio's current value and performance based on the provided latest prices and exchange rates.
    /// </summary>
    PortfolioEvaluationResult Evaluate(
        Portfolio portfolio, 
        IReadOnlyDictionary<string, decimal> latestPrices,
        IReadOnlyDictionary<CurrencyCode, ExchangeRate> latestRates,
        CurrencyCode targetBaseCurrency);

    /// <summary>
    /// Calculates aggregate metrics without allocating dictionaries for per-position details.
    /// </summary>
    PortfolioMetrics GetMetrics(Portfolio portfolio, IReadOnlyDictionary<string, decimal> latestPrices);

    /// <summary>
    /// Calculates aggregate metrics in a target base currency using the provided exchange rates.
    /// </summary>
    PortfolioMetrics GetMetrics(
        Portfolio portfolio, 
        IReadOnlyDictionary<string, decimal> latestPrices,
        IReadOnlyDictionary<CurrencyCode, ExchangeRate> latestRates,
        CurrencyCode targetBaseCurrency);

    /// <summary>
    /// Calculates sector and asset allocations using the Largest Remainder Method for precise weighting.
    /// </summary>
    Task<AllocationResult> GetAllocationAsync(
        Portfolio portfolio, 
        IReadOnlyDictionary<string, decimal> latestPrices, 
        StockAnalyzer.Core.Services.IMarketDataProvider marketDataProvider);

    /// <summary>
    /// Retrieves performance heatmap data for the specified period.
    /// </summary>
    Task<IReadOnlyList<HeatmapEntry>> GetPerformanceHeatmapAsync(
        Portfolio portfolio,
        PerformancePeriod period,
        StockAnalyzer.Core.Services.IMarketDataProvider marketDataProvider,
        System.Threading.CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads the saved user portfolio asynchronously from the repository.
    /// </summary>
    ValueTask<Portfolio> LoadPortfolioAsync(System.Threading.CancellationToken ct = default);

    /// <summary>
    /// Saves the current user portfolio state asynchronously to the repository.
    /// </summary>
    ValueTask SavePortfolioAsync(Portfolio portfolio, System.Threading.CancellationToken ct = default);
}

