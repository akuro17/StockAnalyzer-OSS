using StockAnalyzer.Core.Models;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Threading;

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

    /// <summary>
    /// Loads a complete provider-owned observation snapshot, with finality bound to its exact rows.
    /// True means the provider explicitly attests period closure; dates, file age and later rows
    /// are not evidence. Return null when this source has no observation/finality capability.
    /// Never derive weekly/monthly closure from final daily constituents.
    /// </summary>
    Task<MonitoringObservation?> LoadMonitoringObservationAsync(string symbol, TimeFrame timeFrame,
        CancellationToken cancellationToken = default) => Task.FromResult<MonitoringObservation?>(null);
}
