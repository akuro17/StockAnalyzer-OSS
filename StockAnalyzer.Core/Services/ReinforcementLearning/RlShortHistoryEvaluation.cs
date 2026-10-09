using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Models.Training;

namespace StockAnalyzer.Core.Services.ReinforcementLearning;

/// <summary>Short-history evaluation of the discrete T15 target: whole-job preflight, descriptive report and consistency validation.</summary>
public static class RlShortHistoryEvaluation
{
    /// <summary>
    /// Whole-job work of one T15 run over the given complete blocks: training transitions, every validation checkpoint of every seed,
    /// every baseline episode and the selected OOS pass; the same counting units as <see cref="RlWorkLedger"/>. No native-parity or Core-qualification replay exists for T15.
    /// </summary>
    public static RlEvaluationResourceEstimate Estimate(int validationBlocks, int oosBlocks, long sourceBytes, bool smoke)
    {
        if (validationBlocks < 1 || oosBlocks < 0 || sourceBytes < 0) throw new ArgumentOutOfRangeException(nameof(validationBlocks));
        checked
        {
            long seeds = smoke ? 1 : RlContract.CandidateSeeds.Length;
            long steps = smoke ? RlContract.SmokeTrainingTransitions : RlContract.TrainingTransitionsPerSeed;
            long interval = smoke ? RlContract.SmokeCheckpointInterval : RlContract.ValidationCheckpointInterval;
            long candidates = seeds * (steps / interval) * validationBlocks;
            long baselineRuns = RlBaselinePolicies.EvaluationPolicyIds(false).Sum(p => p == RlContract.RandomBaselineId ? RlContract.CandidateSeeds.Length : 1);
            long baselineEpisodes = baselineRuns * (validationBlocks + (long)oosBlocks);
            long training = seeds * steps;
            long validation = candidates * RlContract.EpisodeHorizon, baselines = baselineEpisodes * RlContract.EpisodeHorizon;
            long oos = oosBlocks * (long)RlContract.EpisodeHorizon;
            long starts = seeds * (steps / RlContract.EpisodeHorizon + 1) + candidates + baselineEpisodes + oosBlocks;
            return new(training, validation, baselines, oos, 0, 0, training + validation + baselines + oos, starts, sourceBytes);
        }
    }

    /// <summary>Source bytes come from the single owner of that rule, <see cref="RlAllBlockEvaluation.Estimate(RlSourceSnapshot, bool)"/>.</summary>
    public static RlEvaluationResourceEstimate Estimate(RlSourceSnapshot source, bool smoke)
        => Estimate(source.Catalog.Validation.Length, source.Catalog.Oos.Length, RlAllBlockEvaluation.Estimate(source, smoke).SourceBytes, smoke);

    public static RlShortHistoryEvaluationReport Create(RlSourceSnapshot source, IReadOnlyList<RlEpisodeEvaluation> validation,
        IReadOnlyList<RlEpisodeEvaluation> oos, bool smoke)
        => new(RlShortHistoryEvaluationContract.SpecificationId, RlShortHistoryEvaluationContract.SchemaVersion,
            RlShortHistoryEvaluationContract.ContractHash, source.Revision,
            new(source.Catalog.Train.Length, source.Catalog.Validation.Length, source.Catalog.Oos.Length),
            RlAllBlockEvaluation.Distribution(validation.Select(e => RlEvaluation.Parse(e.TerminalReturn))),
            oos.Count == 0 ? null : RlAllBlockEvaluation.Distribution(oos.Select(e => RlEvaluation.Parse(e.TerminalReturn))),
            Estimate(source, smoke), RlSourceContract.Unverified, RlConstraintContract.InvestmentEfficacy,
            RlShortHistoryEvaluationContract.Statement);

    /// <summary>Job, catalog, model metadata and report must agree on the short-history id; a missing or extra id is a downgrade/upgrade.</summary>
    public static void Validate(JsonElement job, JsonElement report, IReadOnlyDictionary<string, string> metadata, RlSourceSnapshot source)
    {
        string? specification = RlShortHistoryEvaluationContract.Read(job);
        bool enabled = specification is not null;
        if (RlShortHistoryEvaluationContract.Read(report) != specification)
            throw new InvalidDataException("Short-history evaluation report and job specification differ.");
        if (enabled != source.ShortHistoryEvaluation || enabled != metadata.ContainsKey(RlShortHistoryEvaluationContract.MetadataKey)
            || enabled != report.TryGetProperty(RlShortHistoryEvaluationContract.ReportKey, out var saved)
            || enabled != job.TryGetProperty(RlShortHistoryEvaluationContract.ContractKey, out var contract))
            throw new InvalidDataException("Short-history evaluation job/model/catalog/report version was downgraded or changed.");
        if (!enabled) return;
        if (metadata[RlShortHistoryEvaluationContract.MetadataKey] != specification
            || job.TryGetProperty(RlAllBlockEvaluationContract.MetadataKey, out _) || job.TryGetProperty("continuous_defaults", out _))
            throw new InvalidDataException("Short-history evaluation applies to the discrete T15 target only and excludes the all-block specification.");
        if (contract.GetProperty("specification_id").GetString() != specification
            || contract.GetProperty("schema_version").GetInt32() != RlShortHistoryEvaluationContract.SchemaVersion
            || contract.GetProperty("contract_hash").GetString() != RlShortHistoryEvaluationContract.ContractHash)
            throw new InvalidDataException("Saved short-history evaluation contract changed.");
        bool smoke = job.GetProperty("smoke_only").GetBoolean();
        var limits = job.GetProperty("resource_limits").Deserialize<TrainingResourceLimits>(RlFiles.Json)!;
        var estimate = Estimate(source, smoke);
        RlAllBlockEvaluation.RequireCapacity(estimate, limits);
        RlContinuousParity.RequireExactJson(RlConstraintEvaluation.ToWire(estimate), job.GetProperty("evaluation_resource_estimate"),
            "Short-history preflight evaluation resource estimate");
        if (report.GetProperty("resources").GetProperty("processed_transitions").GetInt64() > estimate.MaximumTransitions)
            throw new InvalidDataException("Actual evaluation transitions exceed the checked short-history estimate.");
        var expected = Create(source, report.GetProperty("validation_episodes").Deserialize<RlEpisodeEvaluation[]>(RlFiles.Json)!,
            report.GetProperty("oos_episodes").Deserialize<RlEpisodeEvaluation[]>(RlFiles.Json)!, smoke);
        RlContinuousParity.RequireExactJson(RlConstraintEvaluation.ToWire(expected), saved, "Short-history evaluation identity/statistics");
    }
}
