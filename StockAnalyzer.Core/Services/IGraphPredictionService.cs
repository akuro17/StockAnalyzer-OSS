using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Training;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// Inference over a multi-symbol graph model (T25). It is a consumer of its own: it shares no base class, state or helper with
/// <see cref="IPredictionService"/>, which stays a single-symbol service.
/// </summary>
public interface IGraphPredictionService : IDisposable
{
    /// <summary>The verified contract of the loaded model; <see langword="null"/> until <see cref="LoadAsync"/> succeeds.</summary>
    GraphModelInfo? Model { get; }

    /// <summary>
    /// Loads a published graph run (<c>&lt;DataRoot&gt;/TrainingArtifacts/Graph/&lt;runId&gt;</c>: <c>graph.onnx</c> and <c>graph.json</c>) from an explicit path and verifies
    /// the file hash, the contract, the node order and the tensor shapes. A model that is already loaded stays active (and keeps serving) when the new one is refused.
    /// </summary>
    /// <exception cref="System.IO.InvalidDataException">The run is not a valid graph model.</exception>
    /// <exception cref="System.IO.FileNotFoundException">A file of the run is missing.</exception>
    Task LoadAsync(string runDirectory, CancellationToken cancellationToken = default);

    /// <summary>
    /// Predicts every node at <paramref name="asOf"/> (default: the newest date of the supplied histories) from per-symbol daily candles. A symbol that is not one of the
    /// model's nodes is refused; a node without a candle history, or without a full feature window at the as-of date, is masked. Only candles dated at or before the
    /// as-of date are read. An expected failure is a result with a reason, never a zero prediction.
    /// </summary>
    Task<GraphPredictionResult> PredictAsync(IReadOnlyDictionary<string, IReadOnlyList<CandleData>> histories, DateTime? asOf = null,
        CancellationToken cancellationToken = default);
}
