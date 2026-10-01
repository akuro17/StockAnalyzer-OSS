using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Avalonia.Common;

namespace StockAnalyzer.Avalonia.ViewModels;

public partial class ChartViewModel
{
    private readonly IPredictionLogService? _predictionLog;
    private readonly TimeProvider _timeProvider;
    internal Task MonitoringUpdateTask { get; private set; } = Task.CompletedTask;
    internal Task MonitoringObservationTask { get; private set; } = Task.CompletedTask;

    private async Task RefreshMonitoringAsync(string symbol, TimeframeType timeframe,
        CoreCandleData[] acceptedCandles, CancellationToken token)
    {
        if (_predictionLog is null) return;
        try
        {
            var bars = acceptedCandles.Select(c => new CandleData(c.Timestamp, c.Open, c.High,
                c.Low, c.Close, c.Volume)).ToImmutableArray();
            var observation = await _dataService.LoadMonitoringObservationAsync(symbol,
                timeframe.ToCoreTimeFrame(), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (observation is not null)
            {
                if (observation.Candles.IsDefault || observation.FinalBars.IsDefault
                    || observation.Candles.Length != observation.FinalBars.Length
                    || observation.SourceRevision != PredictionLogService.SourceRevision(observation.Candles))
                    throw new InvalidDataException("Provider finality is not bound to the observation snapshot.");
                // A separately provided period snapshot must match this accepted chart population.
                // In particular, daily finality cannot attest locally aggregated weekly/monthly bars.
                var byTime = observation.Candles.ToDictionary(c => PredictionLogService.Utc(c.Timestamp));
                if (bars.Any(c => !byTime.TryGetValue(PredictionLogService.Utc(c.Timestamp), out var source)
                    || c.Open != source.Open || c.High != source.High || c.Low != source.Low
                    || c.Close != source.Close || c.Volume != source.Volume))
                    observation = null;
            }
            observation ??= new MonitoringObservation(PredictionLogService.SourceRevision(bars),
                _timeProvider.GetUtcNow(), bars, Enumerable.Repeat(false, bars.Length).ToImmutableArray());
            token.ThrowIfCancellationRequested();
            await _predictionLog.RefreshAsync(symbol, timeframe, observation, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { _logger?.LogWarning(ex, "Prediction monitoring could not refresh provider observations."); }
    }

    private async Task PersistPredictionAsync(PredictionResult result, string symbol, TimeframeType timeframe,
        IReadOnlyList<CandleData> inputs, CancellationToken token)
    {
        if (_predictionLog is null) return;
        try { await _predictionLog.RecordAsync(result, symbol, timeframe, inputs, token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { _logger?.LogWarning(ex, "Prediction monitoring could not record the accepted chart prediction."); }
    }
}
