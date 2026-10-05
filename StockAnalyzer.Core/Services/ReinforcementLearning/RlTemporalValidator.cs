using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Models.Training;

namespace StockAnalyzer.Core.Services.ReinforcementLearning;

/// <summary>One fail-closed owner for producer reconstruction and strict pre-frozen dependency use.</summary>
public static class RlTemporalValidator
{
    // Reviewed immutable producer identities, not user-supplied declarations of a training end date.
    public const string ReviewedLegacyTrainerSha256 = "d7d417fcf8490612547d90381e660d4b8978591ea530c4b4d7e032c9bf7ea5d7";
    internal const string ReviewedTrainerSha256 = "072b942f36adfb71b46ddba658ab297990244b83ad941b1c1f7d43105aea22ae";
    // R06 adds only a frozen catalog/version guard; producer chronology and DQN learning are unchanged.
    internal const string ReviewedAllBlockGuardTrainerSha256 = "752f5d9bc99e00ce89bcefa8c42e8a162140c41c88294b94a853b24039ad3eea";
    private static readonly string[] BoundFiles =
    {
        RlFiles.Model, RlFiles.Model + RlFiles.ScalerSuffix, RlFiles.Native, RlFiles.SourceManifest,
        RlFiles.Bars, RlFiles.Catalog, RlFiles.Job, RlFiles.Model + RlFiles.EvaluationSuffix, RlFiles.Dependencies,
    };

    public static bool IsVersioned(JsonElement job)
    {
        if (!job.TryGetProperty("temporal_specification_id", out var revision)) return false;
        if (revision.GetString() != RlTemporalContract.SpecificationId)
            throw Error(RlTemporalRejection.UnsupportedVersion, "Unsupported temporal qualification specification.");
        return true;
    }

    public static string JobIdentity(JsonElement job) => ContentHash.OfText(JsonSerializer.Serialize(new
    {
        specification = job.GetProperty("temporal_specification_id"), symbol = job.GetProperty("symbol"),
        start = job.GetProperty("start_date"), end = job.GetProperty("end_date_exclusive"),
        source = job.GetProperty("source_revision"), catalog = job.GetProperty("catalog"),
        costs = job.GetProperty("costs"), limits = job.GetProperty("resource_limits"),
        direction = job.TryGetProperty("direction", out var direction) ? direction : (JsonElement?)null,
        dependencies = job.GetProperty("dependencies_sha256"), contract = job.GetProperty("contract_hash"),
    }, RlFiles.Json));

    public static RlTemporalEvidence Reconstruct(string directory, RlSourceSnapshot source)
    {
        using var dependencies = Read(directory, RlFiles.Dependencies);
        using var job = Read(directory, RlFiles.Job);
        using var metrics = Read(directory, RlFiles.Model + RlFiles.EvaluationSuffix);
        var trainer = dependencies.RootElement.GetProperty("source_script_sha256").GetString();
        if (job.RootElement.TryGetProperty("continuous_defaults", out _))
            throw Error(RlTemporalRejection.IdentityMismatch, "Temporal direction evidence must describe a T15 producer.");
        bool versioned = IsVersioned(job.RootElement);
        if (versioned ? trainer != ReviewedTrainerSha256 && trainer != ReviewedAllBlockGuardTrainerSha256 : trainer != ReviewedLegacyTrainerSha256)
            throw Error(RlTemporalRejection.UnknownProducer, "The direction producer identity has no validated information-use contract.");
        if (metrics.RootElement.GetProperty("source_revision").GetString() != source.Revision
            || metrics.RootElement.GetProperty("symbol").GetString() != source.Manifest.Symbol
            || metrics.RootElement.GetProperty("model_sha256").GetString() != ContentHash.OfFile(Path.Combine(directory, RlFiles.Model))
            || job.RootElement.GetProperty("start_date").Deserialize<DateOnly>() != source.Manifest.PeriodStartInclusive
            || job.RootElement.GetProperty("end_date_exclusive").Deserialize<DateOnly>() != source.Manifest.PeriodEndExclusive
            || job.RootElement.GetProperty("dependencies_sha256").GetString() != ContentHash.OfFile(Path.Combine(directory, RlFiles.Dependencies)))
            throw Error(RlTemporalRejection.IdentityMismatch, "Direction source, producer, job and model evidence disagree.");
        var hashes = BoundFiles.ToImmutableSortedDictionary(name => name,
            name => ContentHash.OfFile(Path.Combine(directory, name)), StringComparer.Ordinal);
        RlInformationBound Bound(int index, RlTemporalBoundStatus status, string derivation)
            => new(status, index, DateOnly.FromDateTime(source.Bars[index].Timestamp).AddDays(1), derivation);
        RlInformationBound training, scaler, selection;
        if (!versioned)
        {
            // The reviewed historical trainer only reads selected bars; precise runtime visits were not retained.
            // This deliberately includes unused selected rows/OOS, without asserting OOS-based model selection.
            training = scaler = selection = Bound(source.Bars.Length - 1, RlTemporalBoundStatus.ConservativeUpperBound,
                "reviewed-legacy-producer:selected-source-envelope;precise-visits-unavailable");
        }
        else
        {
            if (job.RootElement.GetProperty("temporal_producer_revision").GetString() != RlTemporalContract.ProducerRevision)
                throw Error(RlTemporalRejection.UnknownProducer, "Unknown information-use producer revision.");
            var usage = metrics.RootElement.GetProperty("information_usage").Deserialize<RlInformationUsage[]>(RlFiles.Json);
            ValidateUsage(usage, source, metrics.RootElement, job.RootElement.GetProperty("smoke_only").GetBoolean());
            training = Bound(usage!.Where(u => u.Role == "train" && u.Transitions > 0).Max(u => u.LastObservedIndex),
                RlTemporalBoundStatus.ExactDerived, "core-ipc:reset-observations-and-transition-result-support");
            var blocks = job.RootElement.GetProperty("smoke_only").GetBoolean() ? source.Catalog.Train.Take(1) : source.Catalog.Train;
            scaler = Bound(blocks.Max(b => b.EndExclusive - 2), RlTemporalBoundStatus.ExactDerived,
                "overlap-window-fit:all-128-decision-windows-of-64-bars;final-result-bar-excluded");
            selection = Bound(usage!.Where(u => u.Role == "validation").Max(u => u.LastObservedIndex),
                RlTemporalBoundStatus.ExactDerived, "core-ipc:all-seed-checkpoint-validation-selection-support");
        }
        return new(RlTemporalContract.SchemaVersion, RlTemporalContract.SpecificationId,
            versioned ? RlTemporalContract.ProducerRevision : "reviewed-legacy-selected-source-envelope",
            source.Manifest.Symbol, source.Revision, trainer!, hashes, training, scaler, selection,
            new[] { training.UpperExclusive, scaler.UpperExclusive, selection.UpperExclusive }.Max(),
            RlTemporalContract.ExternalSelectionStatus, RlSourceContract.Unverified);
    }

    public static RlTemporalEvidence LoadAndValidateProducer(string directory, RlSourceSnapshot source)
    {
        var actual = Reconstruct(directory, source);
        using var job = Read(directory, RlFiles.Job);
        if (IsVersioned(job.RootElement)) Compare(Load(Path.Combine(directory, RlTemporalContract.EvidenceFile)), actual);
        return actual;
    }

    public static RlTemporalEvidence Load(string path)
    {
        if (!File.Exists(path)) throw Error(RlTemporalRejection.MissingEvidence, "Required frozen dependency temporal evidence is missing.");
        if (new FileInfo(path).Length > RlContract.IpcFrameByteLimit || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw Error(RlTemporalRejection.IdentityMismatch, "Temporal evidence is oversized or linked outside its owned artifact.");
        return JsonSerializer.Deserialize<RlTemporalEvidence>(File.ReadAllText(path), RlFiles.Json)
            ?? throw Error(RlTemporalRejection.MissingEvidence, "Temporal evidence is empty.");
    }

    public static void Compare(RlTemporalEvidence declared, RlTemporalEvidence reconstructed)
    {
        Validate(declared);
        if (JsonSerializer.Serialize(declared, RlFiles.Json) != JsonSerializer.Serialize(reconstructed, RlFiles.Json))
            throw Error(RlTemporalRejection.IdentityMismatch, "Declared dates, usage, hashes or source differ from reconstructed temporal evidence.");
    }

    public static void Validate(RlTemporalEvidence evidence)
    {
        if (evidence.SchemaVersion != RlTemporalContract.SchemaVersion || evidence.SpecificationId != RlTemporalContract.SpecificationId)
            throw Error(RlTemporalRejection.UnsupportedVersion, "Unsupported temporal evidence schema/specification.");
        if (evidence.ArtifactHashes is null || evidence.ArtifactHashes.Count != BoundFiles.Length
            || BoundFiles.Any(f => !evidence.ArtifactHashes.TryGetValue(f, out var hash) || !ContentHash.IsLowerHex64(hash))
            || !ContentHash.IsLowerHex64(evidence.TrainerSha256) || !ContentHash.IsLowerHex64(evidence.SourceRevision)
            || evidence.ArtifactHashes[RlFiles.SourceManifest] != evidence.SourceRevision || string.IsNullOrWhiteSpace(evidence.Symbol)
            || evidence.ExternalSelectionStatus != RlTemporalContract.ExternalSelectionStatus || evidence.PricePitStatus != RlSourceContract.Unverified
            || evidence.OtherInformationUse != RlTemporalContract.ReportOnlyInformationUse || evidence.DateBasis != RlSourceContract.DateLabelSemantics)
            throw Error(RlTemporalRejection.IdentityMismatch, "Temporal evidence identity or provenance is invalid.");
        var bounds = new[] { evidence.Training, evidence.Scaler, evidence.Selection };
        if (bounds.Any(b => b is null || b.Status is not (RlTemporalBoundStatus.ExactDerived or RlTemporalBoundStatus.ConservativeUpperBound)
            || b.LastUsedIndex < 0 || b.UpperExclusive == default || string.IsNullOrWhiteSpace(b.Derivation))
            || bounds.Max(b => b.UpperExclusive) != evidence.InformationUpperExclusive)
            throw Error(RlTemporalRejection.MissingEvidence, "Every applicable information bound must be evidenced, including model selection.");
    }

    public static void RequireDecision(RlTemporalEvidence evidence, DateOnly decisionDate)
    {
        Validate(evidence);
        if (evidence.InformationUpperExclusive > decisionDate)
            throw new RlTemporalException(RlTemporalRejection.FutureInformation,
                $"Frozen T15 information upper bound {evidence.InformationUpperExclusive:yyyy-MM-dd} exceeds T16 decision exchange date {decisionDate:yyyy-MM-dd}.",
                evidence.InformationUpperExclusive, decisionDate);
    }

    public static void RequireSource(RlTemporalEvidence evidence, RlSourceSnapshot source, IEnumerable<RlEpisodeBlock>? selectedBlocks = null)
    {
        Validate(evidence);
        if (evidence.Symbol != source.Manifest.Symbol)
            throw Error(RlTemporalRejection.IdentityMismatch, "Frozen direction and T16 symbols differ.");
        foreach (var block in selectedBlocks ?? source.Catalog.Train.Concat(source.Catalog.Validation).Concat(source.Catalog.Oos))
            for (int i = 0; i < RlContract.EpisodeHorizon; i++)
                RequireDecision(evidence, DateOnly.FromDateTime(source.Bars[block.StartIndex + RlContract.WindowLength - 1 + i].Timestamp));
    }

    public static RlTemporalEvidence ValidateDependency(string directory, JsonElement job, RlDirectionDependency direction,
        RlSourceSnapshot source, RlTemporalEvidence? reconstructed = null, IEnumerable<RlEpisodeBlock>? blocks = null)
    {
        if (!IsVersioned(job)) throw Error(RlTemporalRejection.MissingEvidence, "New T16 qualification requires versioned frozen dependency evidence.");
        string path = Path.Combine(directory, RlTemporalContract.DirectionEvidenceFile);
        if (!ContentHash.IsLowerHex64(direction.TemporalEvidenceSha256) || !File.Exists(path)
            || ContentHash.OfFile(path) != direction.TemporalEvidenceSha256)
            throw Error(RlTemporalRejection.IdentityMismatch, "Frozen dependency temporal evidence hash differs from the captured job.");
        var evidence = Load(path);
        Validate(evidence);
        if (evidence.ArtifactHashes[RlFiles.Model] != direction.ModelSha256
            || evidence.ArtifactHashes[RlFiles.Model + RlFiles.ScalerSuffix] != direction.ScalerSha256)
            throw Error(RlTemporalRejection.IdentityMismatch, "Temporal evidence does not bind the frozen model/scaler bytes.");
        if (reconstructed is not null) Compare(evidence, reconstructed);
        RequireSource(evidence, source, blocks);
        return evidence;
    }

    private static void ValidateUsage(RlInformationUsage[]? usage, RlSourceSnapshot source, JsonElement report, bool smoke)
    {
        if (usage is null || usage.Length == 0 || usage.Length > RlContract.EpisodeStartLimit
            || !usage.Any(u => u.Role == "train" && u.Transitions > 0))
            throw Error(RlTemporalRejection.InconsistentUsage, "Observed producer information use is missing or oversized.");
        var identities = new HashSet<(string, int, int, string)>();
        var seeds = smoke ? RlContract.CandidateSeeds.Take(1) : RlContract.CandidateSeeds;
        foreach (var row in usage)
        {
            var blocks = row.Role switch { "train" => source.Catalog.Train, "validation" => source.Catalog.Validation,
                _ => throw Error(RlTemporalRejection.InconsistentUsage, "Unknown information-use role.") };
            var block = blocks.SingleOrDefault(b => b.Id == row.BlockId);
            if (block is null || !seeds.Contains(row.Seed) || row.Resets < 1 || row.Transitions < 0
                || row.Transitions > checked(row.Resets * RlContract.EpisodeHorizon)
                || row.LastObservedIndex < block.StartIndex + RlContract.WindowLength - 1 || row.LastObservedIndex >= block.EndExclusive
                || row.LastObservedIndex - (block.StartIndex + RlContract.WindowLength - 1) > row.Transitions
                || !identities.Add((row.Role, row.Seed, row.Checkpoint, row.BlockId))
                || (row.Role == "train" && row.Checkpoint != 0))
                throw Error(RlTemporalRejection.InconsistentUsage, "Producer support index, transition count or identity is inconsistent.");
        }
        foreach (int seed in seeds)
            if (usage.Where(u => u.Role == "train" && u.Seed == seed).Sum(u => u.Transitions)
                != report.GetProperty("training_transitions").GetProperty(seed.ToString(CultureInfo.InvariantCulture)).GetInt32())
                throw Error(RlTemporalRejection.InconsistentUsage, "Training support does not account for all reported transitions.");
        int expectedSelection = 0;
        foreach (var candidate in report.GetProperty("candidate_evaluations").EnumerateArray())
            foreach (var episode in candidate.GetProperty("summary").GetProperty("episodes").EnumerateArray())
            {
                expectedSelection++;
                var block = source.Catalog.Validation.Single(b => b.Id == episode.GetProperty("block_id").GetString());
                var row = usage.SingleOrDefault(u => u.Role == "validation" && u.Seed == candidate.GetProperty("seed").GetInt32()
                    && u.Checkpoint == candidate.GetProperty("checkpoint_step").GetInt32() && u.BlockId == block.Id);
                int decisions = episode.GetProperty("completed_decisions").GetInt32();
                if (row is null || row.Resets != 1 || row.Transitions != decisions
                    || row.LastObservedIndex != block.StartIndex + RlContract.WindowLength - 1 + decisions)
                    throw Error(RlTemporalRejection.InconsistentUsage, "Selection support disagrees with seed/checkpoint evaluation result bars.");
            }
        if (usage.Count(u => u.Role == "validation") != expectedSelection)
            throw Error(RlTemporalRejection.InconsistentUsage, "Selection support contains unreported information use.");
    }

    private static JsonDocument Read(string directory, string file) => JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, file)));
    private static RlTemporalException Error(RlTemporalRejection reason, string message) => new(reason, message);
}
