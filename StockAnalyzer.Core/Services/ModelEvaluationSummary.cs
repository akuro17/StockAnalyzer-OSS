using System;
using System.IO;
using System.Text.Json;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// What the application reads from the held-out evaluation beside a model (<c>.onnx.metrics.json</c>), read ONCE: the held-out log-loss
/// (<see cref="ModelQualityCheck"/>) and the confidence histogram (<see cref="ModelConfidenceReach"/>). A missing file, an unreadable file or a part that is absent
/// or damaged gives null for that part only; nothing here throws for such a file.
/// </summary>
/// <param name="LogLoss">The finite held-out multi-class log-loss, or null.</param>
/// <param name="Reach">The valid confidence histogram, or null.</param>
public sealed record ModelEvaluationSummary(double? LogLoss, ConfidenceHistogram? Reach)
{
    /// <summary>Nothing is known (no file, or not a JSON object).</summary>
    public static ModelEvaluationSummary Unknown { get; } = new(null, null);

    public static ModelEvaluationSummary Read(string metricsPath)
    {
        if (!File.Exists(metricsPath)) return Unknown;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(metricsPath));
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object ? new ModelEvaluationSummary(ModelQualityCheck.ParseLogLoss(root), ModelConfidenceReach.Parse(root)) : Unknown;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { return Unknown; }
    }

    /// <summary>The evaluation of a registered generation (its metrics file is read through a lease, so the files cannot go away meanwhile).</summary>
    public static ModelEvaluationSummary Read(ModelGenerationManifest manifest, IModelGenerationRegistry registry)
    {
        try
        {
            using var lease = registry.Acquire(manifest.ModelId);
            return lease is null ? Unknown : Read(lease.ModelPath + ".metrics.json");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException) { return Unknown; }
    }
}
