using System;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using StockAnalyzer.Core.Common;

namespace StockAnalyzer.Core.Models.Training;

public static class TrainingCheckpointContract
{
    public const int SchemaVersion = 1;
    public const string LineageKey = "com.stockanalyzer.training.lineage";

    /// <summary>Directory under the training artifacts that holds every run's native checkpoints (mirror of <c>checkpoints.DIRECTORY_NAME</c>).</summary>
    public const string DirectoryName = "checkpoints";
    public static bool IsHash(string? value) => ContentHash.IsLowerHex64(value);
    public static bool IsModulePath(string value) => Regex.IsMatch(value,
        @"\A[A-Za-z_][A-Za-z0-9_]*(?:\.(?:[A-Za-z_][A-Za-z0-9_]*|[0-9]+))*\z", RegexOptions.CultureInvariant);

    public static string ReadResumeRunId(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("schema_version", out var schema) || !schema.TryGetInt32(out var version)
            || version != SchemaVersion || !root.TryGetProperty("kind", out var kind)
            || kind.GetString() != "training_run" || !root.TryGetProperty("run_id", out var id)
            || id.ValueKind != JsonValueKind.String || id.GetString() is not { } runId
            || !Regex.IsMatch(runId, @"\A[A-Za-z0-9_-]+\z", RegexOptions.CultureInvariant))
            throw new InvalidDataException("Resume requires a versioned native training run manifest.");
        return runId;
    }
}
