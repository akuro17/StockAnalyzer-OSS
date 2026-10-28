using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using StockAnalyzer.Core.Execution;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Models.Training;

namespace StockAnalyzer.Core.Services.ReinforcementLearning;

public sealed record RlDialogBlocks(ImmutableArray<RlEpisodeBlock> Blocks,
    RlConstraintReport? Constraints, RlAllBlockEvaluationReport? Expanded)
{
    public RlShortHistoryEvaluationReport? ShortHistory { get; init; }

    /// <summary>The measured mean return and worst drawdown of the final (else validation) partition; null when the report has no episode.</summary>
    public RlMeasuredEvaluation? Measured { get; init; }
}

/// <summary>Background artifact reads and replay with one lease covering their full dependent lifetime.</summary>
/// <param name="dataRoot">The <c>Data</c> folder that receives temporary calendar reads; null resolves the application data folder.</param>
/// <param name="sourceFolders">The daily and metadata folders the anchor ranges are read from; null keeps the fixed <c>Data/Daily</c> and <c>Data/Metadata</c>.</param>
public class RlDialogDataService(IRlModelGenerationRegistry registry, IQValueService replay, string? dataRoot = null, RlLocalSourceFolders? sourceFolders = null)
{
    public virtual Task<RlDialogBlocks> LoadBlocksAsync(string modelId, CancellationToken ct) => Task.Run(() =>
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
        var measured = RlMeasuredEvaluation.FromEpisodes(ReadEpisodes(metrics.RootElement, "oos_episodes"))
            ?? RlMeasuredEvaluation.FromEpisodes(ReadEpisodes(metrics.RootElement, "validation_episodes"));
        ct.ThrowIfCancellationRequested();
        return new RlDialogBlocks(source.Catalog.Validation.Concat(source.Catalog.Oos).ToImmutableArray(), constraints, expanded)
            { ShortHistory = shortHistory, Measured = measured };
    }, ct);

    private static RlEpisodeEvaluation[] ReadEpisodes(JsonElement report, string property)
        => report.TryGetProperty(property, out var rows) ? rows.Deserialize<RlEpisodeEvaluation[]>(RlFiles.Json) ?? [] : [];

    public virtual Task<RlQSnapshot> ReplayAsync(string modelId, string blockId, int decision, string policyId, CancellationToken ct)
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

    /// <summary>Replay on the stored bars of any ticker and date (see <see cref="QValueService.ReplayExternalAsync"/>).</summary>
    public virtual Task<RlExternalReplayResult> ReplayExternalAsync(RlExternalReplayRequest request, CancellationToken ct)
        => Task.Run(async () =>
        {
            ArgumentNullException.ThrowIfNull(request);
            using var lease = registry.AcquireRl(request.ModelId) ?? throw new InvalidDataException("Generation is unavailable.");
            ct.ThrowIfCancellationRequested();
            var result = await replay.ReplayExternalAsync(request, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return result;
        }, ct);

    /// <summary>
    /// Replays an ephemeral uncommitted ONNX policy model located in the OS temporary sandbox.
    /// Precondition: EphemeralSandbox.IsStrictlyInside(onnxModelPath) == true
    /// Postcondition: Output action is deterministically computed via finite-Q greedy selection.
    /// </summary>
    public virtual Task<RlExternalReplayResult> ReplayEphemeralModelAsync(
        string onnxModelPath,
        string symbol,
        DateOnly asOfDate,
        int decisionPosition,
        string? policyId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(onnxModelPath))
            throw new ArgumentException("ONNX model path must not be null or empty.", nameof(onnxModelPath));
        if (!EphemeralSandbox.IsStrictlyInside(onnxModelPath))
            throw new ArgumentException("The ephemeral model path must be strictly inside the ephemeral sandbox.", nameof(onnxModelPath));
        if (!File.Exists(onnxModelPath))
            throw new FileNotFoundException("The ephemeral ONNX model file does not exist.", onnxModelPath);
        if (decisionPosition < 0 || decisionPosition >= RlContract.EpisodeHorizon)
            throw new ArgumentOutOfRangeException(nameof(decisionPosition), "Decision position must be between 0 and 127.");
        if (string.IsNullOrWhiteSpace(symbol))
            throw new ArgumentException("Symbol must not be null or empty.", nameof(symbol));

        return replay.ReplayEphemeralAsync(onnxModelPath, symbol, asOfDate, decisionPosition, policyId, cancellationToken);
    }

    /// <summary>
    /// Reads the canonical trained symbol stored in the custom metadata of an ephemeral ONNX model.
    /// Returns null if the model cannot be opened or does not contain a symbol entry.
    /// </summary>
    public virtual string? ReadEphemeralModelSymbol(string onnxModelPath)
    {
        if (string.IsNullOrWhiteSpace(onnxModelPath) || !File.Exists(onnxModelPath) || !EphemeralSandbox.IsStrictlyInside(onnxModelPath))
            return null;

        try
        {
            using var session = new InferenceSession(onnxModelPath);
            return session.ModelMetadata.CustomMetadataMap.TryGetValue("symbol", out var s) && !string.IsNullOrWhiteSpace(s)
                ? s
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Resolves the earliest and latest valid anchor dates for an ephemeral replay on the stored bars of <paramref name="symbol"/>.
    /// </summary>
    public virtual Task<RlAnchorRange> GetEphemeralAnchorRangeAsync(string symbol, CancellationToken ct = default)
        => Task.Run(async () =>
        {
            if (string.IsNullOrWhiteSpace(symbol))
                throw new ArgumentException("Symbol must not be null or empty.", nameof(symbol));

            var limits = RlTrainingValidation.FreezeLimits(continuous: false);
            using var workspace = RlReplayWorkspace.Create(dataRoot);
            return await RlExternalEpisodeFactory.ReadAnchorRangeAsync(symbol, limits, workspace, ct, sourceFolders).ConfigureAwait(false);
        }, ct);

    /// <summary>The first and last anchor date the stored bars of <paramref name="symbol"/> allow; nothing is prepared or kept.</summary>
    public virtual Task<RlAnchorRange> GetAnchorRangeAsync(string modelId, string symbol, CancellationToken ct)
        => Task.Run(async () =>
        {
            using var lease = registry.AcquireRl(modelId) ?? throw new InvalidDataException("Generation is unavailable.");
            ct.ThrowIfCancellationRequested();
            var limits = QValueService.LoadLimits(Path.GetDirectoryName(lease.ModelPath)!);
            using var workspace = RlReplayWorkspace.Create(dataRoot);
            return await RlExternalEpisodeFactory.ReadAnchorRangeAsync(symbol, limits, workspace, ct, sourceFolders).ConfigureAwait(false);
        }, ct);

    /// <summary>The symbol and period of the source the generation was trained on.</summary>
    public virtual Task<RlSourceInfo> LoadSourceInfoAsync(string modelId, CancellationToken ct) => Task.Run(() =>
    {
        using var lease = registry.AcquireRl(modelId) ?? throw new InvalidDataException("Generation is unavailable.");
        ct.ThrowIfCancellationRequested();
        string directory = Path.GetDirectoryName(lease.ModelPath)!;
        var source = RlSourceSnapshot.ValidateSourceFiles(directory, QValueService.LoadLimits(directory), ct);
        return new RlSourceInfo(source.Manifest.Symbol, source.Manifest.PeriodStartInclusive, source.Manifest.PeriodEndExclusive,
            DateOnly.FromDateTime(source.Bars[0].Timestamp), DateOnly.FromDateTime(source.Bars[^1].Timestamp));
    }, ct);
}
