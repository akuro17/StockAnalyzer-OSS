using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Avalonia.ViewModels;

public partial class ChartViewModel
{
    private readonly IAttentionService? _attentionService;
    private readonly IEnsembleAttentionService? _ensembleAttentionService;
    private readonly IEnsembleSettingsManager? _ensembleSettings;
    private AttentionHeatmapViewModel? _attentionHeatmap;

    /// <summary>One attention request: the key it publishes under and, for an ensemble, the saved ensemble it was begun for (null = a single generation).</summary>
    private sealed record PendingAttention(AttentionRequestKey Key, EnsembleSpec? Ensemble);

    /// <summary>State, snapshot and legend text of the attention overlay (T24b).</summary>
    public AttentionHeatmapViewModel AttentionHeatmap => LazyInitializer.EnsureInitialized(ref _attentionHeatmap, CreateAttentionHeatmap)!;

    private AttentionHeatmapViewModel CreateAttentionHeatmap()
    {
        var created = new AttentionHeatmapViewModel(_dispatcherService);
        created.SetChartType(ChartType);
        created.PropertyChanged += (_, e) =>
        {
            // The chart rebuilds its snapshot when the published attention changes (never from a render path).
            if (e.PropertyName == nameof(AttentionHeatmapViewModel.Snapshot)) OnPropertyChanged(nameof(AttentionSnapshot));
        };
        return created;
    }

    /// <summary>The immutable attention the renderer draws (null = none). The renderer itself applies the chart type gate.</summary>
    public AttentionSnapshot? AttentionSnapshot => _attentionHeatmap?.Snapshot;

    private void ClearAttention() => _attentionHeatmap?.Clear();

    /// <summary>
    /// Begins the attention request of a displayed prediction; null when nothing can be requested (a fallback shows nothing, an unregistered model is
    /// marked unsupported). A prediction without a single generation id is an ensemble's: its attention is requested for <see cref="PredictionResult.Ensemble"/> under the ensemble fingerprint.
    /// </summary>
    private PendingAttention? BeginAttention(PredictionResult result, string symbol, TimeframeType timeframe, CandleData anchor, long revision)
    {
        if (_attentionService is null || result.IsFallback)
        {
            ClearAttention();
            return null;
        }

        if (result.ModelId is not { } modelId)
        {
            var ensemble = result.Ensemble;   // the ensemble that produced the displayed prediction, not the one saved now
            if (ensemble is null || _ensembleAttentionService is null)
            {
                AttentionHeatmap.MarkUnsupported(AttentionContract.UnsupportedReason.NoModel);
                return null;
            }

            var ensembleKey = new AttentionRequestKey(ensemble.Fingerprint(), symbol, timeframe.ToString(), anchor.Timestamp, revision);
            AttentionHeatmap.Begin(ensembleKey);
            return new PendingAttention(ensembleKey, ensemble);
        }

        var key = new AttentionRequestKey(modelId, symbol, timeframe.ToString(), anchor.Timestamp, revision);
        AttentionHeatmap.Begin(key);
        return new PendingAttention(key, null);
    }

    private async Task PublishAttentionAsync(PendingAttention pending, CandleData[] history, CancellationToken token)
    {
        var key = pending.Key;
        AttentionResult outcome;
        try
        {
            outcome = pending.Ensemble is { } ensemble
                ? await _ensembleAttentionService!.ComputeAsync(ensemble, key, history, token).ConfigureAwait(false)
                : await _attentionService!.ComputeAsync(key, history, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Attention is unavailable for this chart request.");
            outcome = AttentionResult.Failed(ex.Message);
        }

        if (token.IsCancellationRequested || _isDisposed) return;
        // Same rule as the prediction display: a result of a generation (or of an ensemble) that is no longer the active one is not shown.
        bool stillActive = pending.Ensemble is not null
            ? IsSavedEnsemble(key.ModelId)
            : _predictionService?.ActiveModelId == key.ModelId;
        if (!stillActive) ClearAttention();
        else AttentionHeatmap.Publish(key, outcome);
    }
}
