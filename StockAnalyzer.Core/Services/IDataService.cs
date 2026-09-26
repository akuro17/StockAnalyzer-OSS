using StockAnalyzer.Core.Models;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// UI-agnostic interface for loading market data.
/// Can be implemented by mock providers or real CSV/API loaders.
/// </summary>
public interface IDataService
{
    /// <summary>
    /// Loads candle data for the specified symbol and timeframe.
    /// </summary>
    Task<IReadOnlyList<CandleData>> LoadCandlesAsync(string symbol, TimeFrame timeFrame, int count = 100);
}
