using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Core.Models.Training;

/// <summary>One member of a bagging / stacking run: its published files and its mixture weight.</summary>
public sealed record TrainingEnsembleMember(
    int Index, string OnnxPath, string MetricsPath, string Sha256, double Weight, string Framework, string Architecture);

/// <summary>
/// The verified <c>&lt;stem&gt;.ensemble.json</c> a bagging / stacking run publishes beside its member ONNX files (schema
/// <c>ensemble_manifest</c> v1, written by <c>ensemble.build_manifest</c>). It only describes the group: every member is an ordinary,
/// independently evaluated generation, and the mixture is the existing T06 linear rule (<see cref="EnsembleSpec"/>).
/// </summary>
public sealed record TrainingEnsembleManifest(
    TrainingEnsembleMethod Method, string RunId, string Timeframe, int Horizon, string ClassOrder,
    IReadOnlyList<TrainingEnsembleMember> Members)
{
    public const int SchemaVersion = 1;
    public const string Kind = "ensemble_manifest";
    public const string MixtureRule = "linear_normalized";
    public const string FileSuffix = ".ensemble.json";

    /// <summary>
    /// Loads and verifies a manifest. Every rule below fails closed with <see cref="InvalidDataException"/>: schema and kind, method,
    /// classification target and mixture rule, member count, plain member file names inside the manifest's own directory, member
    /// file hashes, nonnegative weights summing to one, and weights that agree between the member rows and the weight list.
    /// </summary>
    public static TrainingEnsembleManifest Load(string manifestPath)
    {
        if (string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath))
            throw new FileNotFoundException("Ensemble manifest is missing.", manifestPath);
        var directory = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        JsonDocument document;
        try { document = JsonDocument.Parse(File.ReadAllText(manifestPath)); }
        catch (JsonException ex) { throw new InvalidDataException("Ensemble manifest is not valid JSON.", ex); }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || Integer(root, "schema_version") != SchemaVersion || Text(root, "kind") != Kind)
                throw new InvalidDataException("Unsupported ensemble manifest schema.");
            var method = Text(root, "method") switch
            {
                "bagging" => TrainingEnsembleMethod.Bagging,
                "stacking" => TrainingEnsembleMethod.Stacking,
                _ => throw new InvalidDataException("Ensemble manifest method must be bagging or stacking."),
            };
            if (Text(root, "target_type") != PredictionModelMetadata.TargetTypeClassification
                || Text(root, "mixture") != MixtureRule)
                throw new InvalidDataException("Ensemble manifest must describe a linear mixture of classification generations.");
            var classOrder = Text(root, "class_order");
            if (!PredictionModelMetadata.HasValidClassOrder(classOrder))
                throw new InvalidDataException("Ensemble manifest class order is invalid.");
            if (!root.TryGetProperty("members", out var rows) || rows.ValueKind != JsonValueKind.Array
                || rows.GetArrayLength() is < TrainingEnsembleContract.MinimumMembers or > EnsembleSpec.MaximumMembers)
                throw new InvalidDataException("Ensemble manifest member count is out of range.");

            var members = new List<TrainingEnsembleMember>();
            foreach (var row in rows.EnumerateArray())
            {
                var file = Text(row, "file");
                var metricsFile = Text(row, "metrics_file");
                var hash = Text(row, "sha256");
                if (!PlainFileName.IsValid(file) || !file.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase)
                    || metricsFile != file + ".metrics.json" || !ContentHash.IsLowerHex64(hash))
                    throw new InvalidDataException("Ensemble member files must be plain names with a valid SHA-256.");
                var onnxPath = Path.Combine(directory, file);
                var metricsPath = Path.Combine(directory, metricsFile);
                if (!File.Exists(onnxPath) || !File.Exists(metricsPath))
                    throw new InvalidDataException($"Ensemble member '{file}' or its metrics file is missing.");
                if (!string.Equals(ContentHash.OfFile(onnxPath), hash, StringComparison.Ordinal))
                    throw new InvalidDataException($"Ensemble member '{file}' differs from the file the run scored.");
                members.Add(new TrainingEnsembleMember(Integer(row, "index"), onnxPath, metricsPath, hash,
                    Number(row, "weight"), Text(row, "framework"), Text(row, "architecture")));
            }

            if (members.Select(m => m.Index).Where((index, position) => index != position).Any()
                || members.Select(m => m.OnnxPath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != members.Count)
                throw new InvalidDataException("Ensemble members must be distinct and in index order.");
            var weights = members.Select(m => m.Weight).ToArray();
            if (weights.Any(w => !double.IsFinite(w) || w < 0)
                || Math.Abs(weights.Sum() - 1.0) > EnsembleSpec.SumTolerance)
                throw new InvalidDataException("Ensemble weights must be nonnegative and sum to one.");
            if (!root.TryGetProperty("weights", out var listed) || listed.ValueKind != JsonValueKind.Array
                || listed.GetArrayLength() != weights.Length
                || listed.EnumerateArray().Select((w, i) => w.ValueKind == JsonValueKind.Number && w.GetDouble() == weights[i])
                    .Contains(false))
                throw new InvalidDataException("Ensemble weight list disagrees with the member rows.");
            return new TrainingEnsembleManifest(method, Text(root, "run_id"), Text(root, "timeframe"),
                Integer(root, "horizon"), classOrder, Array.AsReadOnly(members.ToArray()));
        }
    }

    private static string Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new InvalidDataException($"Ensemble manifest field '{name}' is missing or invalid.");

    private static int Integer(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : throw new InvalidDataException($"Ensemble manifest field '{name}' is missing or invalid.");

    private static double Number(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : throw new InvalidDataException($"Ensemble manifest field '{name}' is missing or invalid.");

}
