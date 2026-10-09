using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>Short-history flow over numerical fixtures: lifecycle and consistency only, never trained-market evidence.</summary>
public sealed partial class RlGenerationAndQValueTests
{
    private const string EvaluationFile = RlFiles.Model + RlFiles.EvaluationSuffix;
    private const string SectionKey = RlShortHistoryEvaluationContract.ReportKey;

    private (string Directory, RlSourceSnapshot Source, FixedScaler Scaler) ShortHistoryCandidate() => Candidate(shortHistory: true, barCount: 1278);

    [Fact]
    public async Task ShortHistoryCandidateRegistersWithItsCompleteBlockCountsAndReloadsAsShortHistory()
    {
        var candidate = ShortHistoryCandidate();
        Assert.True(candidate.Source.ShortHistoryEvaluation); Assert.False(candidate.Source.ExpandedEvaluation);
        using var registry = new ModelGenerationRegistry(RegistryRoot);
        var generation = await registry.RegisterRlAsync(candidate.Directory);
        Assert.Equal(-0.01m, generation.ValidationReturn); Assert.Equal(RlFiles.Target, generation.Target);
        using var lease = registry.AcquireRl(generation.ModelId)!;
        string directory = Path.GetDirectoryName(lease.ModelPath)!;
        var reloaded = RlSourceSnapshot.Load(directory, Limits);
        Assert.True(reloaded.ShortHistoryEvaluation); Assert.False(reloaded.ExpandedEvaluation);
        using var report = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, EvaluationFile)));
        var section = report.RootElement.GetProperty(SectionKey);
        var counts = section.GetProperty("block_count");
        Assert.Equal((3, 1, 1), (counts.GetProperty("train").GetInt32(), counts.GetProperty("validation").GetInt32(), counts.GetProperty("oos").GetInt32()));
        Assert.Equal(RlShortHistoryEvaluationContract.Statement, section.GetProperty("statement").GetString());
        Assert.Equal(RlSourceContract.Unverified, section.GetProperty("price_pit").GetString());
        Assert.Equal(RlConstraintContract.InvestmentEfficacy, section.GetProperty("investment_efficacy").GetString());
        Assert.Equal("-0.01", section.GetProperty("validation").GetProperty("mean").GetString());
        Assert.Equal(RlContract.SchemaVersion, report.RootElement.GetProperty("schema_version").GetInt32());
        Assert.False(report.RootElement.TryGetProperty(RlAllBlockEvaluationContract.ReportKey, out _));
    }

    [Fact]
    public async Task ShortHistoryDowngradeAndTamperingAreRejectedBeforePublication()
    {
        using var registry = new ModelGenerationRegistry(RegistryRoot);
        var mutations = new (string File, Action<JsonNode> Change)[]
        {
            (RlFiles.Job, root => ((JsonObject)root).Remove(RlShortHistoryEvaluationContract.MetadataKey)),
            (RlFiles.Catalog, root => ((JsonObject)root).Remove(RlShortHistoryEvaluationContract.MetadataKey)),
            (EvaluationFile, root => ((JsonObject)root).Remove(SectionKey)),
            (EvaluationFile, root => ((JsonObject)root).Remove(RlShortHistoryEvaluationContract.MetadataKey)),
            (EvaluationFile, root => root[SectionKey]!["block_count"]!["train"] = 4),
            (EvaluationFile, root => root[SectionKey]!["statement"] = "efficacy established"),
            (EvaluationFile, root => root[SectionKey]!["validation"]!["mean"] = "0.5"),
            (EvaluationFile, root => root["resources"]!["processed_transitions"] = 99_999),
            (RlFiles.Job, root => root[RlShortHistoryEvaluationContract.ContractKey]!["contract_hash"] = new string('0', 64)),
            (RlFiles.Job, root => root[RlShortHistoryEvaluationContract.ContractKey]!["schema_version"] = 2),
            (RlFiles.Job, root => root["evaluation_resource_estimate"]!["maximum_transitions"] = 1),
            (RlFiles.Job, root => root[RlAllBlockEvaluationContract.MetadataKey] = RlAllBlockEvaluationContract.SpecificationId),
        };
        foreach (var (file, change) in mutations)
        {
            var candidate = ShortHistoryCandidate();
            Change(candidate.Directory, file, change);
            await Assert.ThrowsAsync<InvalidDataException>(() => registry.RegisterRlAsync(candidate.Directory));
        }
        Assert.Empty(registry.ListRl());
    }

    [Fact]
    public void StandardEvidenceCannotAcquireOrLoseTheShortHistoryContract()
    {
        var standard = Candidate(barCount: 5_200);
        using var job = JsonDocument.Parse(File.ReadAllText(Path.Combine(standard.Directory, RlFiles.Job)));
        using var report = JsonDocument.Parse(File.ReadAllText(Path.Combine(standard.Directory, EvaluationFile)));
        var metadata = RlObservationBuilder.CreateMetadata(Symbol, standard.Source.Revision, Revision);
        RlShortHistoryEvaluation.Validate(job.RootElement, report.RootElement, metadata, standard.Source);
        // Upgrade: a short-history id only in model metadata, or requested at load time, must not be adopted silently.
        metadata[RlShortHistoryEvaluationContract.MetadataKey] = RlShortHistoryEvaluationContract.SpecificationId;
        Assert.Throws<InvalidDataException>(() => RlShortHistoryEvaluation.Validate(job.RootElement, report.RootElement, metadata, standard.Source));
        var error = Assert.Throws<InvalidDataException>(() => RlSourceSnapshot.Load(standard.Directory, Limits, false, CancellationToken.None, null,
            RlShortHistoryEvaluationContract.SpecificationId));
        Assert.Contains("short-history", error.Message);
        // Downgrade: the saved short-history candidate evaluated without its metadata id is rejected.
        var shortHistory = ShortHistoryCandidate();
        using var shortJob = JsonDocument.Parse(File.ReadAllText(Path.Combine(shortHistory.Directory, RlFiles.Job)));
        using var shortReport = JsonDocument.Parse(File.ReadAllText(Path.Combine(shortHistory.Directory, EvaluationFile)));
        var plain = RlObservationBuilder.CreateMetadata(Symbol, shortHistory.Source.Revision, Revision);
        Assert.Throws<InvalidDataException>(() => RlShortHistoryEvaluation.Validate(shortJob.RootElement, shortReport.RootElement, plain, shortHistory.Source));
    }

    [Fact]
    public void ShortHistoryAppliesToTheDiscreteTargetOnly()
    {
        var candidate = ShortHistoryCandidate();
        var metadata = RlObservationBuilder.CreateMetadata(Symbol, candidate.Source.Revision, Revision);
        metadata[RlShortHistoryEvaluationContract.MetadataKey] = RlShortHistoryEvaluationContract.SpecificationId;
        using var report = JsonDocument.Parse(File.ReadAllText(Path.Combine(candidate.Directory, EvaluationFile)));
        var job = JsonNode.Parse(File.ReadAllText(Path.Combine(candidate.Directory, RlFiles.Job)))!;
        using (var plain = JsonDocument.Parse(job.ToJsonString()))
            RlShortHistoryEvaluation.Validate(plain.RootElement, report.RootElement, metadata, candidate.Source);
        job["continuous_defaults"] = new JsonObject();
        using var continuous = JsonDocument.Parse(job.ToJsonString());
        var error = Assert.Throws<InvalidDataException>(() => RlShortHistoryEvaluation.Validate(continuous.RootElement, report.RootElement, metadata, candidate.Source));
        Assert.Contains("discrete T15", error.Message);
    }

    [Fact]
    public void AllBlockAndShortHistoryIdsCannotBeCombinedAndNeitherLeaksIntoTheOther()
    {
        var candidate = ShortHistoryCandidate();
        File.Delete(Path.Combine(candidate.Directory, RlFiles.Catalog));
        Change(candidate.Directory, RlFiles.Job, root => root[RlAllBlockEvaluationContract.MetadataKey] = RlAllBlockEvaluationContract.SpecificationId);
        var error = Assert.Throws<InvalidDataException>(() => RlSourceSnapshot.Load(candidate.Directory, Limits));
        Assert.Contains("mutually exclusive", error.Message);
        // An all-block catalog and a short-history catalog are different artifacts for the same bars.
        var plain = Candidate(shortHistory: false, barCount: 1278 + 5_000);
        Assert.False(plain.Source.ShortHistoryEvaluation);
        Assert.Throws<InvalidDataException>(() => RlSourceSnapshot.Load(plain.Directory, Limits, false, CancellationToken.None, null, "other"));
    }

    [Fact]
    public void BaselineWorkEqualsTheShortHistoryEstimateWithIndependentHandCounts()
    {
        var candidate = ShortHistoryCandidate();
        using var jobDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(candidate.Directory, RlFiles.Job)));
        var costs = jobDocument.RootElement.GetProperty("costs").Deserialize<BacktestConfiguration>(RlFiles.Json)!;
        var metadata = RlObservationBuilder.CreateMetadata(Symbol, candidate.Source.Revision, Revision);
        using var host = new RlIpcHost(Guid.NewGuid(), candidate.Source, costs, candidate.Directory, metadata, null);
        host.EvaluateBaselines(CancellationToken.None);
        // 6 baseline runs (SMA, hold, buy-hold, 3 random seeds) x (1 validation + 1 OOS block) = 12 episodes of 128 decisions.
        Assert.Equal(12, host.EpisodeStarts); Assert.Equal(1536, host.Transitions);
        var estimate = RlShortHistoryEvaluation.Estimate(candidate.Source, false);
        Assert.Equal(host.Transitions, estimate.BaselineTransitions);
    }
}
