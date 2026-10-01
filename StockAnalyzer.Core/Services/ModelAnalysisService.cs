using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Training;

namespace StockAnalyzer.Core.Services;

public sealed record ModelAnalysisSnapshot(string ModelId, ModelAnalysis Analysis);
public sealed record RegimeAssignment(int Id, DateTime Anchor);

public interface IModelAnalysisService
{
    Task<ModelAnalysisSnapshot?> LoadAsync(string modelId, CancellationToken ct = default);
    RegimeAssignment? Assign(ModelAnalysisSnapshot snapshot, IReadOnlyList<CandleData> candles, TimeframeType timeframe);
}

/// <summary>Loads verified immutable analysis outside rendering; never fits at inference time.</summary>
public sealed class ModelAnalysisService(IModelGenerationRegistry registry, IMLDataProcessor processor) : IModelAnalysisService
{
    private readonly ConcurrentDictionary<string, ModelAnalysisSnapshot> _cache = new(StringComparer.Ordinal);

    public Task<ModelAnalysisSnapshot?> LoadAsync(string modelId, CancellationToken ct = default) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        using var lease = registry.Acquire(modelId);
        if (lease is null) return null;
        var manifest = registry.GetManifest(modelId);
        if (manifest is null || !manifest.RequiredSidecars.Any(s => s.Kind == "analysis")) return null;
        if (_cache.TryGetValue(modelId, out var cached)) return cached;
        using var session = new InferenceSession(lease.ModelPath);
        using var metrics = JsonDocument.Parse(File.ReadAllText(lease.ModelPath + ".metrics.json"));
        var analysis = ModelAnalysis.Load(lease.ModelPath, session.ModelMetadata.CustomMetadataMap,
            manifest.ModelHash, metrics.RootElement.GetProperty("data_revision").GetString() ?? "");
        ct.ThrowIfCancellationRequested();
        var snapshot = new ModelAnalysisSnapshot(modelId, analysis);
        _cache.TryAdd(modelId, snapshot);
        return snapshot;
    }, ct);

    public RegimeAssignment? Assign(ModelAnalysisSnapshot snapshot, IReadOnlyList<CandleData> candles, TimeframeType timeframe)
    {
        var state = snapshot.Analysis.Regime;
        if (!snapshot.Analysis.Timeframe.Equals(timeframe.ToString(), StringComparison.OrdinalIgnoreCase)
            || state.Status != "available" || candles.Count <= ModelAnalysisContract.VolatilityBars) return null;
        int first = candles.Count - ModelAnalysisContract.VolatilityBars - 1;
        for (int i = first; i < candles.Count; i++)
            if (candles[i].Close <= 0 || (i > first && candles[i].Timestamp <= candles[i - 1].Timestamp)) return null;
        Span<float> returns = stackalloc float[ModelAnalysisContract.VolatilityBars + 1];
        processor.ComputeLogReturns(candles, first, returns.Length, returns);
        double mean = 0;
        for (int i = 1; i < returns.Length; i++)
        {
            if (!float.IsFinite(returns[i])) return null;
            mean += returns[i];
        }
        mean /= ModelAnalysisContract.VolatilityBars;
        double variance = 0;
        for (int i = 1; i < returns.Length; i++) { double delta = returns[i] - mean; variance += delta * delta; }
        double volatility = Math.Sqrt(variance / ModelAnalysisContract.VolatilityBars);
        double x = (returns[^1] - state.Mean[0]) / (state.Std[0] == 0 ? 1 : state.Std[0]);
        double y = (volatility - state.Mean[1]) / (state.Std[1] == 0 ? 1 : state.Std[1]);
        if (!double.IsFinite(x) || !double.IsFinite(y)) return null;
        int selected = 0;
        double best = double.PositiveInfinity;
        for (int i = 0; i < state.K; i++)
        {
            double dx = x - state.Centroids[i][0], dy = y - state.Centroids[i][1];
            double distance = dx * dx + dy * dy;
            if (distance < best) { best = distance; selected = i; }
        }
        return double.IsFinite(best) ? new RegimeAssignment(selected, candles[^1].Timestamp) : null;
    }
}
