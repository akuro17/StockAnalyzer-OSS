using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Models.Training;

namespace StockAnalyzer.Core.Services.ReinforcementLearning;

/// <summary>All-block descriptive evaluation and preflight; Core remains the only financial owner.</summary>
public static class RlAllBlockEvaluation
{
    public static RlReturnDistribution Distribution(IEnumerable<decimal> values)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        if (sorted.Length == 0) throw new InvalidDataException("An empty return distribution has no statistics.");
        int middle = sorted.Length / 2;
        return new(sorted.Length, sorted.Sum() / sorted.Length,
            sorted.Length % 2 == 0 ? (sorted[middle - 1] + sorted[middle]) / 2m : sorted[middle],
            sorted[0], sorted[^1], (decimal)sorted.Count(v => v > 0m) / sorted.Length);
    }

    public static RlEvaluationResourceEstimate Estimate(int validationBlocks, int oosBlocks, int sourceBars, long sourceArtifactBytes, bool smoke)
    {
        if (validationBlocks < 1 || oosBlocks < 0 || sourceBars < 1 || sourceArtifactBytes < 0) throw new ArgumentOutOfRangeException(nameof(validationBlocks));
        checked
        {
            long seeds = smoke ? 1 : RlContract.CandidateSeeds.Length;
            long steps = smoke ? RlContract.SmokeTrainingTransitions : RlContinuousContract.TrainingTransitionsPerSeed;
            long interval = smoke ? RlContract.SmokeCheckpointInterval : RlContinuousContract.ValidationCheckpointInterval;
            long candidates = seeds * (steps / interval) * validationBlocks;
            long baselineRuns = RlBaselinePolicies.EvaluationPolicyIds(true).Sum(p => p == RlContract.RandomBaselineId ? RlContract.CandidateSeeds.Length : 1);
            long baselineEpisodes = baselineRuns * (validationBlocks + (long)oosBlocks);
            long coreEpisodes = validationBlocks * (long)RlFinancialParityContract.ReplayLayers * (smoke ? 1 : 2);
            long training = seeds * steps;
            long validation = candidates * RlContract.EpisodeHorizon, baselines = baselineEpisodes * RlContract.EpisodeHorizon;
            long oos = (smoke ? 0 : oosBlocks) * (long)RlContract.EpisodeHorizon;
            long native = validationBlocks * (long)RlContract.EpisodeHorizon, core = coreEpisodes * RlContract.EpisodeHorizon;
            long starts = seeds * (steps / RlContract.EpisodeHorizon + 1) + candidates + baselineEpisodes
                + (smoke ? 0 : oosBlocks) + validationBlocks + coreEpisodes;
            long source = sourceArtifactBytes * sizeof(char) + sourceBars * (2L * Unsafe.SizeOf<CandleData>() + RlContract.MarketChannelCount * (long)sizeof(double));
            return new(training, validation, baselines, oos, native, core, training + validation + baselines + oos + native + core, starts, source);
        }
    }
    public static RlEvaluationResourceEstimate Estimate(RlSourceSnapshot source, bool smoke)
        => Estimate(source.Catalog.Validation.Length, source.Catalog.Oos.Length, source.Bars.Length,
            new[] { RlFiles.SourceManifest, RlFiles.Bars, RlFiles.Actions, RlFiles.RawParquet, RlFiles.MetadataParquet }
                .Sum(f => new FileInfo(Path.Combine(source.Directory, f)).Length), smoke);
    public static void RequireCapacity(RlEvaluationResourceEstimate estimate, TrainingResourceLimits limits)
    {
        if (estimate.MaximumTransitions > RlContract.ProcessedTransitionLimit || estimate.NominalEpisodeStarts > RlContract.EpisodeStartLimit
            || estimate.SourceBytes > Math.Min(RlContract.BufferByteLimit, checked((long)limits.MaxTensorSizeMiB * TrainingResourceOverrides.MiB)))
            throw new InvalidDataException("The complete requested evaluation cannot fit captured whole-job resources; no seeds, blocks or steps were reduced.");
    }
    public static RlAllBlockEvaluationReport Create(string directory, RlSourceSnapshot source, BacktestConfiguration costs,
        IReadOnlyList<RlEpisodeEvaluation> validation, IReadOnlyList<RlEpisodeEvaluation> oos, RlConstraintReport constraints, string directionId, bool smoke)
        => new(RlAllBlockEvaluationContract.SpecificationId, RlAllBlockEvaluationContract.SchemaVersion, source.Revision,
            ContentHash.OfFile(Path.Combine(directory, RlFiles.Model)), directionId, costs, constraints.Coverage,
            Distribution(validation.Select(e => RlEvaluation.Parse(e.TerminalReturn))),
            oos.Count == 0 ? null : Distribution(oos.Select(e => RlEvaluation.Parse(e.TerminalReturn))),
            Distribution(constraints.PairedDeltas.Where(d => d.Partition == "validation").Select(d => d.Delta)),
            oos.Count == 0 ? null : Distribution(constraints.PairedDeltas.Where(d => d.Partition == "oos").Select(d => d.Delta)),
            Estimate(source, smoke), RlConstraintContract.SelectionMetric, RlSourceContract.Unverified, RlConstraintContract.InvestmentEfficacy);

    public static void Validate(string directory, JsonElement job, JsonElement report, IReadOnlyDictionary<string, string> metadata,
        RlSourceSnapshot source, BacktestConfiguration costs)
    {
        string? specification = RlAllBlockEvaluationContract.Read(job);
        bool expanded = specification is not null;
        if (RlAllBlockEvaluationContract.Read(report) != specification)
            throw new InvalidDataException("Evaluation report and job specification differ.");
        if (expanded != source.ExpandedEvaluation || expanded != metadata.ContainsKey(RlAllBlockEvaluationContract.MetadataKey)
            || expanded != report.TryGetProperty(RlAllBlockEvaluationContract.ReportKey, out var saved)
            || report.GetProperty("schema_version").GetInt32() != (expanded ? RlAllBlockEvaluationContract.SchemaVersion : RlContract.SchemaVersion))
            throw new InvalidDataException("Independent evaluation job/model/catalog/report version was downgraded or changed.");
        if (!expanded) return;
        if (!job.TryGetProperty("continuous_defaults", out _) || metadata[RlAllBlockEvaluationContract.MetadataKey] != specification
            || RlAllBlockEvaluationContract.Read(report) != specification)
            throw new InvalidDataException("All-block evaluation requires consistent continuous-model specification bindings.");
        var contract = job.GetProperty("evaluation_contract");
        if (contract.GetProperty("specification_id").GetString() != specification
            || contract.GetProperty("schema_version").GetInt32() != RlAllBlockEvaluationContract.SchemaVersion)
            throw new InvalidDataException("Saved independent evaluation contract changed.");
        bool smoke = job.GetProperty("smoke_only").GetBoolean();
        var limits = job.GetProperty("resource_limits").Deserialize<TrainingResourceLimits>(RlFiles.Json)!;
        var estimate = Estimate(source, smoke); RequireCapacity(estimate, limits);
        RlContinuousParity.RequireExactJson(RlConstraintEvaluation.ToWire(estimate), job.GetProperty("evaluation_resource_estimate"), "Preflight evaluation resource estimate");
        if (report.GetProperty("resources").GetProperty("processed_transitions").GetInt64() > estimate.MaximumTransitions)
            throw new InvalidDataException("Actual evaluation transitions exceed the checked complete-block estimate.");
        void Summary(JsonElement summary)
        {
            var rows = summary.GetProperty("episodes").Deserialize<RlEpisodeEvaluation[]>(RlFiles.Json)!;
            RlContinuousParity.RequireExactJson(RlConstraintEvaluation.ToWire(Distribution(rows.Select(r => RlEvaluation.Parse(r.TerminalReturn)))),
                summary.GetProperty("return_distribution"), "All-block return distribution");
        }
        foreach (var candidate in report.GetProperty("candidate_evaluations").EnumerateArray()) Summary(candidate.GetProperty("summary"));
        foreach (var partition in report.GetProperty("baselines").EnumerateObject())
            foreach (var policy in partition.Value.EnumerateObject())
            {
                if (policy.Value.TryGetProperty("seeds", out var seeds))
                {
                    foreach (var seed in seeds.EnumerateArray()) Summary(seed);
                    var rows = seeds.EnumerateArray().SelectMany(s => s.GetProperty("episodes").Deserialize<RlEpisodeEvaluation[]>(RlFiles.Json)!);
                    RlContinuousParity.RequireExactJson(RlConstraintEvaluation.ToWire(Distribution(rows.Select(r => RlEvaluation.Parse(r.TerminalReturn)))),
                        policy.Value.GetProperty("return_distribution"), "All-seed random baseline distribution");
                }
                else Summary(policy.Value);
            }
        var constraints = report.GetProperty("constraint_diagnostics").Deserialize<RlConstraintReport>(RlFinancialParityContract.TraceJson)!;
        var expected = Create(directory, source, costs, report.GetProperty("validation_episodes").Deserialize<RlEpisodeEvaluation[]>(RlFiles.Json)!,
            report.GetProperty("oos_episodes").Deserialize<RlEpisodeEvaluation[]>(RlFiles.Json)!, constraints, metadata["frozen_direction_model_id"], smoke);
        RlContinuousParity.RequireExactJson(RlConstraintEvaluation.ToWire(expected), saved, "All-block evaluation identity/coverage/statistics");
    }
}
