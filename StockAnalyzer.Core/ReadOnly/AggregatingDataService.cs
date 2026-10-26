using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Core.ReadOnly;

/// <summary>
/// An <see cref="IDataService"/> decorator that transparently aggregates weekly and monthly candles
/// from daily data via <see cref="ITimeFrameManager"/> when the inner service has no stored files for them.
/// </summary>
public sealed class AggregatingDataService : IDataService
{
    private readonly IDataService _inner;
    private readonly ITimeFrameManager _timeFrameManager;

    public AggregatingDataService(IDataService inner, ITimeFrameManager timeFrameManager)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _timeFrameManager = timeFrameManager ?? throw new ArgumentNullException(nameof(timeFrameManager));
    }

    public async Task<IReadOnlyList<CandleData>> LoadCandlesAsync(string symbol, TimeFrame timeFrame, int count = 100)
    {
        IReadOnlyList<CandleData> direct = await _inner.LoadCandlesAsync(symbol, timeFrame, count).ConfigureAwait(false);
        if (direct.Count > 0 || (timeFrame != TimeFrame.W1 && timeFrame != TimeFrame.MN1))
        {
            return direct;
        }

        return await _timeFrameManager.GetCandlesAsync(symbol, timeFrame, count, CancellationToken.None).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CandleData>> LoadCandlesAsync(
        string symbol,
        TimeFrame timeFrame,
        int count,
        bool includePartialBar,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<CandleData> direct = await _inner.LoadCandlesAsync(symbol, timeFrame, count, includePartialBar, cancellationToken).ConfigureAwait(false);
        if (direct.Count > 0 || (timeFrame != TimeFrame.W1 && timeFrame != TimeFrame.MN1))
        {
            return direct;
        }

        return await _timeFrameManager.GetCandlesAsync(symbol, timeFrame, count, cancellationToken).ConfigureAwait(false);
    }

    public Task<MonitoringObservation?> LoadMonitoringObservationAsync(
        string symbol,
        TimeFrame timeFrame,
        CancellationToken cancellationToken = default)
        => _inner.LoadMonitoringObservationAsync(symbol, timeFrame, cancellationToken);
}
