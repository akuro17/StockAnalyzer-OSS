#nullable enable
using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Threading;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

public sealed class RlTemporalQualificationTests
{
    public const string EnabledVariable = "SA_RL_R01_QUALIFICATION";
    public const string OutputVariable = "SA_RL_R01_OUTPUT_DIRECTORY";
    public const string LegacyDirectionVariable = "SA_RL_R01_LEGACY_DIRECTION_DIRECTORY";
    public const string LegacyActorVariable = "SA_RL_R01_LEGACY_ACTOR_DIRECTORY";
    private const string FixtureHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private static RlTemporalEvidence Evidence(DateOnly train, DateOnly scaler, DateOnly selection)
    {
        var files = new[] { RlFiles.Model, RlFiles.Model + RlFiles.ScalerSuffix, RlFiles.Native,
            RlFiles.SourceManifest, RlFiles.Bars, RlFiles.Catalog, RlFiles.Job, RlFiles.Model + RlFiles.EvaluationSuffix, RlFiles.Dependencies };
        RlInformationBound Bound(DateOnly date) => new(RlTemporalBoundStatus.ExactDerived, 0, date, "boundary-fixture");
        return new(RlTemporalContract.SchemaVersion, RlTemporalContract.SpecificationId, RlTemporalContract.ProducerRevision,
            "AAPL", FixtureHash, FixtureHash, files.ToImmutableSortedDictionary(f => f, _ => FixtureHash),
            Bound(train), Bound(scaler), Bound(selection), new[] { train, scaler, selection }.Max(), RlTemporalContract.ExternalSelectionStatus, RlSourceContract.Unverified);
    }

    [Theory]
    [InlineData(0, 0, 0, true)] // exclusive upper equality is permitted
    [InlineData(-1, -1, 1, false)] // model selection can carry future information
    [InlineData(-1, 1, -1, false)] // future scaler alone is enough to reject
    [InlineData(1, -1, -1, false)] // a same-date USED bar has the following-day upper bound
    public void StrictRuleIncludesEveryInformationOwner(int trainingOffset, int scalerOffset, int selectionOffset, bool admitted)
    {
        var decision = new DateOnly(2004, 4, 1);
        var evidence = Evidence(decision.AddDays(trainingOffset), decision.AddDays(scalerOffset), decision.AddDays(selectionOffset));
        if (admitted) RlTemporalValidator.RequireDecision(evidence, decision);
        else
        {
            var error = Assert.Throws<RlTemporalException>(() => RlTemporalValidator.RequireDecision(evidence, decision));
            Assert.Equal(RlTemporalRejection.FutureInformation, error.Reason);
            Assert.Equal(decision, error.DecisionExchangeDate);
        }
    }

    [Fact] public void MissingBoundUnknownVersionAndMismatchedDatesCannotQualify()
    {
        var day = new DateOnly(2000, 1, 1);
        var evidence = Evidence(day, day, day);
        Assert.Equal(RlTemporalRejection.MissingEvidence, Assert.Throws<RlTemporalException>(() =>
            RlTemporalValidator.Validate(evidence with { Selection = evidence.Selection with { Status = RlTemporalBoundStatus.Unverified } })).Reason);
        Assert.Equal(RlTemporalRejection.UnsupportedVersion, Assert.Throws<RlTemporalException>(() =>
            RlTemporalValidator.Validate(evidence with { SchemaVersion = RlTemporalContract.SchemaVersion + 1 })).Reason);
        Assert.Throws<RlTemporalException>(() => RlTemporalValidator.Validate(evidence with { InformationUpperExclusive = day.AddDays(-1) }));
        Assert.Throws<RlTemporalException>(() => RlTemporalValidator.Compare(evidence with { SourceRevision = new string('b', FixtureHash.Length) }, evidence));
        Assert.Throws<RlTemporalException>(() => RlTemporalValidator.Compare(evidence with { Selection = evidence.Selection with { LastUsedIndex = 1 } }, evidence));
        Assert.Throws<RlTemporalException>(() => RlTemporalValidator.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))));
    }

    [Fact] public void ExplicitVersionDispatchRejectsUnknownAndRetainsLegacy()
    {
        using var legacy = JsonDocument.Parse("{}");
        Assert.False(RlTemporalValidator.IsVersioned(legacy.RootElement));
        using var unknown = JsonDocument.Parse("{\"temporal_specification_id\":\"unknown\"}");
        Assert.Equal(RlTemporalRejection.UnsupportedVersion,
            Assert.Throws<RlTemporalException>(() => RlTemporalValidator.IsVersioned(unknown.RootElement)).Reason);
    }

    public sealed class TemporalQualificationAttribute : FactAttribute
    {
        public TemporalQualificationAttribute()
        {
            if (Environment.GetEnvironmentVariable(EnabledVariable) != "1")
                Skip = "Opt-in full earlier-T15/later-T16 stored Daily qualification, with owned registry and unchanged resource caps.";
        }
    }

    [TemporalQualification] public async Task FullEarlierDirectionLaterActorQualification()
    {
        string output = Environment.GetEnvironmentVariable(OutputVariable) ?? Path.GetTempPath();
        string root = Path.Combine(output, "owned_qualification_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var registry = new ModelGenerationRegistry(Path.Combine(root, "Generations"));
        var costs = new BacktestConfiguration { InitialCapital = BacktestDefaults.InitialCapital,
            SizingModel = BacktestDefaults.SizingModel, SizingParameter = BacktestDefaults.SizingParameter,
            CommissionFlat = 0m, CommissionPerUnit = 0m, SlippageRatio = 0m,
            LiquidationPenaltyRatio = BacktestDefaults.LiquidationPenaltyRatio };
        var service = new RlTrainingService(registry, new TrainingJobGate(Path.Combine(root, "job-locks")), dataRoot: root);
        var progress = new Progress<RlTrainingProgress>(p => File.AppendAllText(Path.Combine(root, "progress.log"),
            $"{DateTimeOffset.UtcNow:O} {p.RunId} {p.State} {p.CompletedSteps} {p.Message}{Environment.NewLine}"));
        var direction = await service.RunAsync(new("AAPL", new(1980, 1, 1), new(2004, 1, 1), costs), progress);
        Assert.NotNull(direction.ModelId);
        var proof = registry.CaptureRlTemporalEvidence(direction.ModelId!)!;
        Assert.Equal(RlTemporalBoundStatus.ExactDerived, proof.Training.Status);
        Assert.Equal(RlTemporalBoundStatus.ExactDerived, proof.Scaler.Status);
        Assert.Equal(RlTemporalBoundStatus.ExactDerived, proof.Selection.Status);
        Assert.Equal(proof.Training.UpperExclusive, DateOnly.FromDateTime(
            RlSourceSnapshot.Load(direction.Directory, RlTrainingValidation.FreezeLimits()).Bars[proof.Training.LastUsedIndex].Timestamp).AddDays(1));
        Assert.Equal(RlSourceContract.Unverified, proof.PricePitStatus); Assert.Equal(RlTemporalContract.ExternalSelectionStatus, proof.ExternalSelectionStatus);
        var overlap = new RlTrainingJob("AAPL", new(1980, 1, 1), new(2004, 1, 1), costs) { FrozenDirectionModelId = direction.ModelId };
        Assert.Equal(RlTemporalRejection.FutureInformation,
            (await Assert.ThrowsAsync<RlTemporalException>(() => service.RunAsync(overlap))).Reason);
        var actor = await service.RunAsync(new RlTrainingJob("AAPL", new(2004, 1, 1), new(2026, 10, 4), costs)
            { FrozenDirectionModelId = direction.ModelId }, progress);
        Assert.NotNull(actor.ModelId);
        Assert.Null(registry.RlActiveId); Assert.Null(registry.RlContinuousActiveId);
        var generation = registry.GetRlGeneration(actor.ModelId!)!;
        Assert.Equal(RlTemporalContract.SpecificationId, generation.TemporalSpecificationId);
        Assert.Equal(RlTemporalBoundStatus.ExactDerived, generation.TemporalStatus);
        var source = RlSourceSnapshot.Load(actor.Directory, RlTrainingValidation.FreezeLimits(continuous: true));
        var first = DateOnly.FromDateTime(source.Bars[source.Catalog.Train[0].StartIndex + RlContract.WindowLength - 1].Timestamp);
        Assert.True(proof.InformationUpperExclusive <= first);
        Assert.True(proof.Scaler.LastUsedIndex < proof.Training.LastUsedIndex);
        // Warmup may predate the information upper bound; admission is based on actual decision dates.
        RlTemporalValidator.RequireSource(Evidence(first, first, first), source);
        Assert.Equal(ContentHash.OfFile(Path.Combine(direction.Directory, RlFiles.Model + RlFiles.ScalerSuffix)),
            ContentHash.OfFile(Path.Combine(actor.Directory, RlContinuousContract.DirectionScaler)));
        foreach (var block in new[] { source.Catalog.Train[0], source.Catalog.Validation[0], source.Catalog.Oos[0] })
            foreach (string policy in new[] { RlContinuousContract.Target, RlContinuousContract.PairedBaselineId })
            {
                var replay = await new QValueService(registry).ReplayAsync(new(actor.ModelId!, actor.Directory, block.Id, 0, policy));
                Assert.Equal(RlTemporalContract.SpecificationId, replay.TemporalSpecificationId);
                Assert.Equal(RlTemporalBoundStatus.ExactDerived, replay.TemporalStatus);
            }
        using var metrics = JsonDocument.Parse(await File.ReadAllTextAsync(actor.EvaluationPath));
        Assert.True(metrics.RootElement.GetProperty("parity_verified").GetBoolean());
        Assert.Equal(RlContract.CandidateSeeds.Length * 2, metrics.RootElement.GetProperty("candidate_evaluations").GetArrayLength());
        Assert.Equal(RlContract.EvaluationEpisodeCount, metrics.RootElement.GetProperty("oos_episodes").GetArrayLength());

        var directionSource = RlSourceSnapshot.Load(direction.Directory, RlTrainingValidation.FreezeLimits());
        string producerProofPath = Path.Combine(direction.Directory, RlTemporalContract.EvidenceFile);
        string producerProof = await File.ReadAllTextAsync(producerProofPath);
        var changedSelection = proof.Selection with { UpperExclusive = proof.Selection.UpperExclusive.AddDays(-1) };
        var changedEvidence = proof with { Selection = changedSelection,
            InformationUpperExclusive = new[] { proof.Training.UpperExclusive, proof.Scaler.UpperExclusive, changedSelection.UpperExclusive }.Max() };
        await File.WriteAllTextAsync(producerProofPath, JsonSerializer.Serialize(changedEvidence, RlFiles.Json));
        try { Assert.Equal(RlTemporalRejection.IdentityMismatch, Assert.Throws<RlTemporalException>(() =>
            RlTemporalValidator.LoadAndValidateProducer(direction.Directory, directionSource)).Reason); }
        finally { await File.WriteAllTextAsync(producerProofPath, producerProof); }
        string dependencyPath = Path.Combine(direction.Directory, RlFiles.Dependencies);
        string producerDependencies = await File.ReadAllTextAsync(dependencyPath);
        var unknownProducer = JsonNode.Parse(producerDependencies)!; unknownProducer["source_script_sha256"] = FixtureHash;
        await File.WriteAllTextAsync(dependencyPath, unknownProducer.ToJsonString(RlFiles.Json));
        try { Assert.Equal(RlTemporalRejection.UnknownProducer, Assert.Throws<RlTemporalException>(() =>
            RlTemporalValidator.Reconstruct(direction.Directory, directionSource)).Reason); }
        finally { await File.WriteAllTextAsync(dependencyPath, producerDependencies); }
        string metricsPath = direction.EvaluationPath;
        string producerMetrics = await File.ReadAllTextAsync(metricsPath);
        var alteredUsage = JsonNode.Parse(producerMetrics)!;
        var selectedUsage = alteredUsage["information_usage"]!.AsArray().First(u => u!["role"]!.GetValue<string>() == "validation")!;
        selectedUsage["last_observed_index"] = selectedUsage["last_observed_index"]!.GetValue<int>() - 1;
        await File.WriteAllTextAsync(metricsPath, alteredUsage.ToJsonString(RlFiles.Json));
        try { Assert.Equal(RlTemporalRejection.InconsistentUsage, Assert.Throws<RlTemporalException>(() =>
            RlTemporalValidator.Reconstruct(direction.Directory, directionSource)).Reason); }
        finally { await File.WriteAllTextAsync(metricsPath, producerMetrics); }

        // Direct registry callers cannot downgrade a newly produced actor into the legacy path.
        string jobPath = Path.Combine(actor.Directory, RlFiles.Job);
        string originalJob = await File.ReadAllTextAsync(jobPath);
        var node = JsonNode.Parse(originalJob)!; node.AsObject().Remove("temporal_specification_id");
        await File.WriteAllTextAsync(jobPath, node.ToJsonString(RlFiles.Json));
        try { Assert.Equal(RlTemporalRejection.MissingEvidence, (await Assert.ThrowsAsync<RlTemporalException>(() => registry.RegisterRlAsync(actor.Directory))).Reason); }
        finally { await File.WriteAllTextAsync(jobPath, originalJob); }
        string proofPath = Path.Combine(actor.Directory, RlTemporalContract.DirectionEvidenceFile);
        string originalProof = await File.ReadAllTextAsync(proofPath);
        node = JsonNode.Parse(originalProof)!; node["information_upper_exclusive"] = "1900-01-01";
        await File.WriteAllTextAsync(proofPath, node.ToJsonString(RlFiles.Json));
        try { await Assert.ThrowsAsync<RlTemporalException>(() => registry.RegisterRlAsync(actor.Directory)); }
        finally { await File.WriteAllTextAsync(proofPath, originalProof); }
        // Even a registry adapter bypassing byte validation cannot bypass the replay date/reconstruction guard.
        var premature = proof with { Training = proof.Training with { UpperExclusive = first.AddDays(1) },
            Scaler = proof.Scaler with { UpperExclusive = first.AddDays(1) },
            Selection = proof.Selection with { UpperExclusive = first.AddDays(1) }, InformationUpperExclusive = first.AddDays(1) };
        await File.WriteAllTextAsync(proofPath, JsonSerializer.Serialize(premature, RlFiles.Json));
        node = JsonNode.Parse(originalJob)!; node["direction"]!["temporal_evidence_sha256"] = ContentHash.OfFile(proofPath);
        await File.WriteAllTextAsync(jobPath, node.ToJsonString(RlFiles.Json));
        try
        {
            var bypass = new ReplayRegistryAdapter(registry, generation, actor.Directory);
            Assert.Equal(RlTemporalRejection.IdentityMismatch, (await Assert.ThrowsAsync<RlTemporalException>(() =>
                new QValueService(bypass).ReplayAsync(new(actor.ModelId!, actor.Directory, source.Catalog.Train[0].Id, 0, RlContinuousContract.Target)))).Reason);
        }
        finally { await File.WriteAllTextAsync(proofPath, originalProof); await File.WriteAllTextAsync(jobPath, originalJob); }
        Assert.Null(registry.RlActiveId); Assert.Null(registry.RlContinuousActiveId);
        await AtomicJsonFile.SaveAsync(Path.Combine(output, "full_qualification_result.json"), new
        {
            direction, actor, evidence = proof, first_train_decision = first,
            registry_root = Path.Combine(root, "Generations"), costs,
            source_price_pit = "Unverified", external_selection = "Unverified", profitability = "NotEstablished",
        }, RlFiles.Json);
    }

    [TemporalQualification] public async Task ExistingLegacyBytesPointersAndResearchReplayRemainUnchanged()
    {
        string legacyActor = Environment.GetEnvironmentVariable(LegacyActorVariable)
            ?? throw new InvalidOperationException("Explicit legacy actor artifact directory required.");
        string legacyDirection = Environment.GetEnvironmentVariable(LegacyDirectionVariable)
            ?? throw new InvalidOperationException("Explicit legacy direction artifact directory required.");
        string existing = Path.GetFullPath(Path.Combine(legacyActor, "..", "..", "..", "Generations", RlFiles.RlFolder));
        var originalHashes = Directory.GetFiles(existing, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, ContentHash.OfFile);
        string output = Environment.GetEnvironmentVariable(OutputVariable) ?? Path.GetTempPath();
        string root = Path.Combine(output, "legacy_owned_" + Guid.NewGuid().ToString("N"));
        string destination = Path.Combine(root, RlFiles.RlFolder);
        foreach (string path in originalHashes.Keys)
        {
            string target = Path.Combine(destination, Path.GetRelativePath(existing, path));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(path, target);
        }
        var pointers = new[] { RlFiles.Active, RlContinuousContract.ActiveFile }
            .Where(file => File.Exists(Path.Combine(destination, file))).ToDictionary(file => file, file => ContentHash.OfFile(Path.Combine(destination, file)));
        using var registry = new ModelGenerationRegistry(root);
        var actor = Assert.Single(registry.ListRl().Where(g => g.Target == RlContinuousContract.Target));
        var direction = registry.GetRlGeneration(actor.FrozenDirectionModelId!)!;
        Assert.Null(actor.TemporalSpecificationId); Assert.Equal(RlTemporalBoundStatus.Unverified, actor.TemporalStatus);
        Assert.Equal(actor.ModelId, registry.RlContinuousActiveId); Assert.Equal(direction.ModelId, registry.RlActiveId);
        var evidence = registry.CaptureRlTemporalEvidence(direction.ModelId)!;
        Assert.Equal(RlTemporalBoundStatus.ConservativeUpperBound, evidence.Status);
        var source = RlSourceSnapshot.Load(legacyActor, RlTrainingValidation.FreezeLimits(continuous: true));
        Assert.Equal(RlTemporalRejection.FutureInformation,
            Assert.Throws<RlTemporalException>(() => RlTemporalValidator.RequireSource(evidence, source)).Reason);
        var snapshot = await new QValueService(registry).ReplayAsync(new(actor.ModelId, legacyActor,
            source.Catalog.Validation[0].Id, 0, RlContinuousContract.Target));
        Assert.Null(snapshot.TemporalSpecificationId); Assert.Contains(RlTemporalContract.LegacyDisclosure, snapshot.Disclosure);
        Assert.Equal(RlTemporalRejection.MissingEvidence,
            (await Assert.ThrowsAsync<RlTemporalException>(() => registry.RegisterRlAsync(legacyActor))).Reason);
        var service = new RlTrainingService(registry, new TrainingJobGate(Path.Combine(root, "job-locks")), dataRoot: root);
        using var savedJob = JsonDocument.Parse(File.ReadAllText(Path.Combine(legacyDirection, RlFiles.Job)));
        var costs = savedJob.RootElement.GetProperty("costs").Deserialize<BacktestConfiguration>(RlFiles.Json)!;
        Assert.Equal(RlTemporalRejection.FutureInformation, (await Assert.ThrowsAsync<RlTemporalException>(() => service.RunAsync(
            new RlTrainingJob("AAPL", new(1990, 1, 1), new(2026, 10, 4), costs) { FrozenDirectionModelId = direction.ModelId }))).Reason);
        foreach (var pointer in pointers) Assert.Equal(pointer.Value, ContentHash.OfFile(Path.Combine(destination, pointer.Key)));
        foreach (var original in originalHashes) Assert.Equal(original.Value, ContentHash.OfFile(original.Key));
        await AtomicJsonFile.SaveAsync(Path.Combine(output, "legacy_compatibility_result.json"), new
        {
            actor.ModelId, direction_id = direction.ModelId, dependency_evidence = evidence,
            original_bytes_unchanged = true, active_pointers_unchanged = true, research_replay = "passed",
            same_period_new_qualification = "rejected", output_root = root,
        }, RlFiles.Json);
    }

    private sealed class ReplayRegistryAdapter(IRlModelGenerationRegistry inner, RlGeneration actor, string artifactDirectory) : IRlModelGenerationRegistry
    {
        public string? RlActiveId => inner.RlActiveId;
        public IReadOnlyList<RlGeneration> ListRl() => inner.ListRl();
        public Task<RlGeneration> RegisterRlAsync(string directory, CancellationToken ct = default) => inner.RegisterRlAsync(directory, ct);
        public Task<bool> ActivateRlAsync(string id, CancellationToken ct = default) => inner.ActivateRlAsync(id, ct);
        public ModelGenerationLease? AcquireRl(string id) => id == actor.ModelId
            ? new ModelGenerationLease(id, Path.Combine(artifactDirectory, RlFiles.Model), () => { }) : inner.AcquireRl(id);
        public ModelGenerationLease? AcquireRlActive() => inner.AcquireRlActive();
        public RlGeneration? GetRlGeneration(string id) => inner.GetRlGeneration(id);
        public RlTemporalEvidence? CaptureRlTemporalEvidence(string id) => inner.CaptureRlTemporalEvidence(id);
    }
}
