using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.ML.OnnxRuntime;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Core.Tests.TestHelpers;

/// <summary>
/// Builds a model file and the independent-evaluation metrics beside it that <see cref="ModelGenerationRegistry"/> accepts (a registrable generation): the model is a
/// copy of a fixture, the metrics carry the evidence the registry checks (status, revision, scored model hash, target, timeframe, horizon, class order, the final fold
/// and one row per fold). Optionally the metrics also carry the held-out log-loss and a confidence histogram.
/// </summary>
internal static class RegistrableGeneration
{
    /// <summary>The fixture model used unless a test names another one.</summary>
    public static readonly string DefaultFixture = Path.Combine("Assets", "trend_predictor_goodmeta.onnx");

    private const int HistogramBins = 100, BelowThresholdBin = 36, AtThresholdBin = 55;

    /// <summary>
    /// Copies the fixture to a new folder under <paramref name="root"/> as <paramref name="name"/> and writes its metrics (macro-F1 <paramref name="score"/> in the final fold).
    /// <paramref name="logLoss"/> adds <c>fold_multi_logloss</c>. <paramref name="reaching"/> adds a histogram of 100 windows over 100 bins, <paramref name="reaching"/> of them
    /// in the bin of probability 0.55 and the rest in the bin of 0.36 (so that exactly that many windows reach a threshold of 0.5).
    /// </summary>
    public static string Create(string root, string name, double score, string revision, string? fixture = null, double? logLoss = null, int? reaching = null)
    {
        var directory = Path.Combine(root, "source_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var model = Path.Combine(directory, name);
        File.Copy(fixture ?? DefaultFixture, model);
        if (fixture is not null && File.Exists(fixture + ".scaler.json"))
            File.Copy(fixture + ".scaler.json", model + ".scaler.json");
        using var session = new InferenceSession(model);
        var metadata = session.ModelMetadata.CustomMetadataMap;
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(model))).ToLowerInvariant();
        var report = JsonNode.Parse(JsonSerializer.Serialize(new
        {
            evaluation_status = "independent_outer",
            evaluation_revision = revision,
            scored_model_sha256 = hash,
            target_type = metadata[PredictionModelMetadata.TargetTypeKey],
            timeframe = "daily",
            horizon = int.Parse(metadata[PredictionModelMetadata.HorizonKey]),
            class_order = metadata[PredictionModelMetadata.ClassOrderKey],
            fold_macro_f1 = score,
            fold_is_holdout = 1.0,
            fold = 2.0,
            fold_n = 20.0,
            n_splits = 3.0,
            outer_folds = new[]
            {
                new { fold = 0.0, evaluation_revision = revision, scored_model_sha256 = hash, fold_macro_f1 = 0.2 },
                new { fold = 1.0, evaluation_revision = revision, scored_model_sha256 = hash, fold_macro_f1 = 0.3 },
                new { fold = 2.0, evaluation_revision = revision, scored_model_sha256 = hash, fold_macro_f1 = score },
            },
        }))!.AsObject();
        if (logLoss is { } value) report[ModelQualityCheck.LogLossKey] = value;
        if (reaching is { } reach)
        {
            var counts = new int[HistogramBins];
            counts[BelowThresholdBin] = HistogramBins - reach;
            counts[AtThresholdBin] = reach;
            report[ModelConfidenceReach.Key] = new JsonObject
            {
                [ModelConfidenceReach.BinsKey] = HistogramBins,
                [ModelConfidenceReach.CountKey] = HistogramBins,
                [ModelConfidenceReach.HistogramKey] = new JsonArray(counts.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray()),
            };
        }
        File.WriteAllText(model + ".metrics.json", report.ToJsonString());
        return model;
    }
}
