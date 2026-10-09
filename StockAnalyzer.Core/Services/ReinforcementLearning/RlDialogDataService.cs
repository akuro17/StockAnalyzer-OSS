using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Models.Training;

namespace StockAnalyzer.Core.Services.ReinforcementLearning;

public sealed record RlDialogBlocks(ImmutableArray<RlEpisodeBlock> Blocks,
    RlConstraintReport? Constraints, RlAllBlockEvaluationReport? Expanded)
{
    public RlShortHistoryEvaluationReport? ShortHistory { get; init; }
}

/// <summary>Background artifact reads and replay with one lease covering their full dependent lifetime.</summary>
public sealed class RlDialogDataService(IRlModelGenerationRegistry registry, IQValueService replay)
{
    public Task<RlDialogBlocks> LoadBlocksAsync(string modelId, CancellationToken ct) => Task.Run(() =>
    {
        using var lease = registry.AcquireRl(modelId) ?? throw new InvalidDataException("Generation is unavailable.");
        ct.ThrowIfCancellationRequested();
        string directory = Path.GetDirectoryName(lease.ModelPath)!;
        using var job = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, RlFiles.Job)));
        var limits = job.RootElement.GetProperty("resource_limits").Deserialize<TrainingResourceLimits>(RlFiles.Json)
            ?? throw new InvalidDataException("Resource limits are missing.");
        var source = RlSourceSnapshot.Load(directory, limits);
        using var metrics = JsonDocument.Parse(File.ReadAllText(lease.ModelPath + RlFiles.EvaluationSuffix));
        var constraints = metrics.RootElement.TryGetProperty("constraint_diagnostics", out var evidence)
            ? evidence.Deserialize<RlConstraintReport>(RlFinancialParityContract.TraceJson) : null;
        var expanded = metrics.RootElement.TryGetProperty(RlAllBlockEvaluationContract.ReportKey, out var all)
            ? all.Deserialize<RlAllBlockEvaluationReport>(RlFinancialParityContract.TraceJson) : null;
        var shortHistory = metrics.RootElement.TryGetProperty(RlShortHistoryEvaluationContract.ReportKey, out var brief)
            ? brief.Deserialize<RlShortHistoryEvaluationReport>(RlFinancialParityContract.TraceJson) : null;
        ct.ThrowIfCancellationRequested();
        return new RlDialogBlocks(source.Catalog.Validation.Concat(source.Catalog.Oos).ToImmutableArray(), constraints, expanded) { ShortHistory = shortHistory };
    }, ct);

    public Task<RlQSnapshot> ReplayAsync(string modelId, string blockId, int decision, string policyId, CancellationToken ct)
        => Task.Run(async () =>
        {
            using var lease = registry.AcquireRl(modelId) ?? throw new InvalidDataException("Generation is unavailable.");
            // Cancellation may arrive while the registry owner is acquiring the native/file lease.
            ct.ThrowIfCancellationRequested();
            var result = await replay.ReplayAsync(new(modelId, Path.GetDirectoryName(lease.ModelPath)!, blockId, decision, policyId), ct)
                .ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return result;
        }, ct);
}
