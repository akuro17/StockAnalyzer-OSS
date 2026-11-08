using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Core.Helpers;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// Decorator for <see cref="IDataService"/> that transparently intercepts synthetic custom formula symbols
/// (e.g. 7203-T / 7267-T, 7203-T - 7267-T, AAPL + MSFT) and calculates synthetic OHLCV candles on the fly
/// without ever persisting them to disk.
/// </summary>
public sealed class SyntheticSymbolDataService : IDataService, IDataReadStatusSource
{
    private readonly IDataService _inner;
    private readonly IComparisonDataAligner _aligner;
    private readonly ILogger<SyntheticSymbolDataService> _logger;

    public SyntheticSymbolDataService(
        IDataService inner,
        IComparisonDataAligner? aligner = null,
        ILogger<SyntheticSymbolDataService>? logger = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _aligner = aligner ?? new ComparisonDataAligner(inner);
        _logger = logger ?? NullLogger<SyntheticSymbolDataService>.Instance;
    }

    public Task<IReadOnlyList<CandleData>> LoadCandlesAsync(string symbol, TimeFrame timeFrame, int count = 100)
        => LoadCandlesAsync(symbol, timeFrame, count, includePartialBar: true, CancellationToken.None);

    public async Task<IReadOnlyList<CandleData>> LoadCandlesAsync(
        string symbol,
        TimeFrame timeFrame,
        int count,
        bool includePartialBar,
        CancellationToken cancellationToken = default)
    {
        if (!SyntheticSymbolParser.TryParse(symbol, out var expr))
        {
            return await _inner.LoadCandlesAsync(symbol, timeFrame, count, includePartialBar, cancellationToken).ConfigureAwait(false);
        }

        if (!expr.IsOperatorSupported)
        {
            _logger.LogWarning("Synthetic symbol '{Symbol}' uses unsupported operator '{OperatorChar}'. Returning empty candles.", symbol, expr.OperatorChar);
            return Array.Empty<CandleData>();
        }

        try
        {
            if (timeFrame == TimeFrame.D1)
            {
                var aligned = await _aligner.AlignAsync(
                    expr.LeftSymbol,
                    new[] { expr.RightSymbol },
                    TimeFrame.D1,
                    count,
                    cancellationToken).ConfigureAwait(false);

                var computed = SyntheticCandleCalculator.ComputeCandles(aligned, expr);
                if (count > 0 && computed.Count > count)
                {
                    return computed.TakeLast(count).ToArray();
                }
                return computed;
            }

            if (TimeFrameAggregator.IsPeriodFrame(timeFrame))
            {
                // For weekly/monthly, align daily candles first, compute synthetic daily candles,
                // and aggregate to weekly/monthly using AggregateToPeriodStart.
                int dailyFetchCount;
                if (count <= 0)
                {
                    dailyFetchCount = 0;
                }
                else if (timeFrame == TimeFrame.W1)
                {
                    int safeCount = Math.Min(count, 50000);
                    dailyFetchCount = Math.Max(safeCount * 7 + 14, 100);
                }
                else
                {
                    // MN1
                    int safeCount = Math.Min(count, 20000);
                    dailyFetchCount = Math.Max(safeCount * 25 + 35, 100);
                }

                var dailyAligned = await _aligner.AlignAsync(
                    expr.LeftSymbol,
                    new[] { expr.RightSymbol },
                    TimeFrame.D1,
                    dailyFetchCount,
                    cancellationToken).ConfigureAwait(false);

                var syntheticDaily = SyntheticCandleCalculator.ComputeCandles(dailyAligned, expr);
                if (syntheticDaily.Count > 0)
                {
                    var aggregated = TimeFrameAggregator.AggregateToPeriodStart(syntheticDaily, timeFrame);
                    var resultList = aggregated.ToList();

                    if (!includePartialBar && syntheticDaily.Count > 0)
                    {
                        DateTime refDate = syntheticDaily[^1].Timestamp.Date;
                        while (resultList.Count > 0 && TimeFrameAggregator.IsPartialPeriod(resultList[^1].Timestamp, timeFrame, refDate))
                        {
                            resultList.RemoveAt(resultList.Count - 1);
                        }
                    }

                    if (count > 0 && resultList.Count > count)
                    {
                        return resultList.TakeLast(count).ToArray();
                    }
                    return resultList;
                }

                // Fallback for environments or tests where only period frames (W1/MN1) are present
                var periodAligned = await _aligner.AlignAsync(
                    expr.LeftSymbol,
                    new[] { expr.RightSymbol },
                    timeFrame,
                    count,
                    cancellationToken).ConfigureAwait(false);

                var syntheticPeriod = SyntheticCandleCalculator.ComputeCandles(periodAligned, expr);
                if (count > 0 && syntheticPeriod.Count > count)
                {
                    return syntheticPeriod.TakeLast(count).ToArray();
                }
                return syntheticPeriod;
            }

            // Intraday or other timeframes: align directly
            var directAligned = await _aligner.AlignAsync(
                expr.LeftSymbol,
                new[] { expr.RightSymbol },
                timeFrame,
                count,
                cancellationToken).ConfigureAwait(false);

            var directComputed = SyntheticCandleCalculator.ComputeCandles(directAligned, expr);
            if (count > 0 && directComputed.Count > count)
            {
                return directComputed.TakeLast(count).ToArray();
            }
            return directComputed;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load/compute candles for synthetic symbol '{Symbol}'. Returning empty candles.", symbol);
            return Array.Empty<CandleData>();
        }
    }

    public Task<MonitoringObservation?> LoadMonitoringObservationAsync(
        string symbol,
        TimeFrame timeFrame,
        CancellationToken cancellationToken = default)
    {
        if (SyntheticSymbolParser.IsSyntheticExpression(symbol))
        {
            return Task.FromResult<MonitoringObservation?>(null);
        }

        return _inner.LoadMonitoringObservationAsync(symbol, timeFrame, cancellationToken);
    }

    public Task<DataReadResult> LoadCandlesWithStatusAsync(
        string symbol,
        TimeFrame timeFrame,
        int count,
        CancellationToken cancellationToken = default)
    {
        if (SyntheticSymbolParser.IsSyntheticExpression(symbol))
        {
            return Task.FromResult(new DataReadResult(DataReadStatus.FileMissing, Array.Empty<CandleData>()));
        }

        if (_inner is IDataReadStatusSource statusSource)
        {
            return statusSource.LoadCandlesWithStatusAsync(symbol, timeFrame, count, cancellationToken);
        }

        return Task.FromResult(new DataReadResult(DataReadStatus.FileMissing, Array.Empty<CandleData>()));
    }
}
