using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models.ReinforcementLearning;

namespace StockAnalyzer.Core.Services.ReinforcementLearning;

/// <summary>Validates bounded update evidence independently of Python and of registration success.</summary>
public static class RlDiagnosticsValidator
{
    public static void ValidateUpdate(RlUpdateDiagnostics row, string source, string dependencies)
    {
        if (row is null || row.SpecificationId != RlDiagnosticsContract.SpecificationId || row.SourceRevision != source
            || row.ContractHash != RlContinuousContract.ContractHash || row.DependenciesSha256 != dependencies
            || !RlContract.CandidateSeeds.Contains(row.Seed) || row.CheckpointStep <= 0
            || row.CheckpointStep > RlContinuousContract.TrainingTransitionsPerSeed || row.CheckpointStep % RlContinuousContract.RolloutSteps != 0
            || row.Minibatches < 1 || row.Minibatches > RlDiagnosticsContract.MinibatchLimit
            || (!row.EarlyStopped && row.Minibatches != RlDiagnosticsContract.MinibatchLimit)
            || row.OptimizerSteps < 0 || row.OptimizerSteps > row.Minibatches
            || row.Minibatches != row.OptimizerSteps + (row.EarlyStopped ? 1 : 0)
            || row.Metrics is null)
            throw new InvalidDataException("Invalid PPO guarded update identity/counts.");
        var expected = RlDiagnosticsContract.MetricNames.Where(n => n != "gradient_norm" || row.OptimizerSteps > 0).Order().ToArray();
        if (!row.Metrics.Keys.Order().SequenceEqual(expected)) throw new InvalidDataException("Incomplete/unbounded PPO diagnostic metrics.");
        foreach (var pair in row.Metrics)
        {
            var metric = pair.Value;
            int count = pair.Key == "log_std" ? row.Minibatches * RlContinuousContract.ActionCount
                : pair.Key == "gradient_norm" ? row.OptimizerSteps : row.Minibatches;
            if (metric is null || metric.Count != count || !double.IsFinite(metric.Minimum) || !double.IsFinite(metric.Maximum) || !double.IsFinite(metric.Mean)
                || metric.Minimum > metric.Mean || metric.Mean > metric.Maximum
                || ((pair.Key is "gradient_norm" or "value_loss") && metric.Minimum < 0))
                throw new InvalidDataException("PPO diagnostic aggregate is nonfinite/inconsistent.");
        }
    }

    public static void Validate(string directory, JsonElement job, JsonElement report, IReadOnlyDictionary<string, string> metadata)
    {
        bool versioned = job.TryGetProperty("diagnostics_specification_id", out var specification);
        if (versioned != metadata.ContainsKey("diagnostics_specification_id")) throw new InvalidDataException("PPO diagnostic version downgrade/mismatch.");
        if (!versioned) return; // Existing immutable actors keep their original qualification rules.
        if (specification.GetString() != RlDiagnosticsContract.SpecificationId
            || metadata["diagnostics_specification_id"] != RlDiagnosticsContract.SpecificationId
            || job.GetProperty("metadata").GetProperty("diagnostics_specification_id").GetString() != RlDiagnosticsContract.SpecificationId)
            throw new InvalidDataException("Unsupported PPO numerical guard specification.");
        string dependencies = ContentHash.OfFile(Path.Combine(directory, RlFiles.Dependencies));
        if (job.GetProperty("dependencies_sha256").GetString() != dependencies
            || metadata["diagnostics_dependencies_sha256"] != dependencies)
            throw new InvalidDataException("Diagnostic dependency snapshot changed.");
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, RlFiles.Dependencies)));
        foreach (string key in new[] { "ppo_guard_sha256", "ppo_train_sha256", "ppo_gae_sha256", "ppo_collector_sha256", "ppo_policy_sha256", "source_script_sha256" })
            if (!ContentHash.IsLowerHex64(manifest.RootElement.GetProperty(key).GetString()))
                throw new InvalidDataException("Missing PPO guard/upstream source identity.");
        var diagnostics = report.GetProperty("diagnostics");
        if (diagnostics.GetProperty("specification_id").GetString() != RlDiagnosticsContract.SpecificationId
            || diagnostics.GetProperty("source_revision").GetString() != job.GetProperty("source_revision").GetString()
            || diagnostics.GetProperty("contract_hash").GetString() != RlContinuousContract.ContractHash
            || diagnostics.GetProperty("dependencies_sha256").GetString() != dependencies
            || diagnostics.GetProperty("selected_seed").GetInt32() != report.GetProperty("candidate_seed").GetInt32()
            || diagnostics.GetProperty("selected_checkpoint").GetInt32() != report.GetProperty("checkpoint_step").GetInt32())
            throw new InvalidDataException("PPO diagnostic selection/source binding is invalid.");
        var updates = diagnostics.GetProperty("updates").Deserialize<RlUpdateDiagnostics[]>(RlFiles.Json)
            ?? throw new InvalidDataException("Missing PPO update diagnostics.");
        bool smoke = job.GetProperty("smoke_only").GetBoolean();
        int transitions = smoke ? RlContract.SmokeTrainingTransitions : RlContinuousContract.TrainingTransitionsPerSeed;
        var seeds = smoke ? RlContract.CandidateSeeds.Take(1) : RlContract.CandidateSeeds;
        var expected = seeds.SelectMany(seed => Enumerable.Range(1, transitions / RlContinuousContract.RolloutSteps)
            .Select(i => (seed, i * RlContinuousContract.RolloutSteps))).ToArray();
        if (updates.Length > RlDiagnosticsContract.UpdateLimit || updates.Any(r => r is null)
            || !updates.Select(r => (r.Seed, r.CheckpointStep)).SequenceEqual(expected))
            throw new InvalidDataException("Missing/duplicate PPO rollout diagnostics.");
        foreach (var update in updates) ValidateUpdate(update, job.GetProperty("source_revision").GetString()!, dependencies);
        var actions = diagnostics.GetProperty("actions").Deserialize<RlActionDiagnostics[]>(RlFiles.Json)
            ?? throw new InvalidDataException("Missing raw-action diagnostics.");
        if (actions.Length > RlDiagnosticsContract.UpdateLimit || actions.Any(a => a is null || a.AllActions is null || a.EntryActions is null)
            || actions.Select(a => (a.Role, a.Seed, a.CheckpointStep)).Distinct().Count() != actions.Length)
            throw new InvalidDataException("Unbounded/duplicate action diagnostics.");
        foreach (var row in actions)
        {
            if (row.Role is not ("train" or "validation" or "oos" or "parity") || !seeds.Contains(row.Seed)
                || row.AllActions.Denominator < 1 || row.EntryActions.Denominator > row.AllActions.Denominator)
                throw new InvalidDataException("Invalid action diagnostic denominator/role.");
            ValidateCounts(row.AllActions); ValidateCounts(row.EntryActions);
            if (row.EntryActions.Rates.Any(p => p.Value.Count > row.AllActions.Rates[p.Key].Count))
                throw new InvalidDataException("Entry diagnostics are not a subset of all actions.");
        }
        var candidateRows = report.GetProperty("candidate_evaluations").EnumerateArray().ToArray();
        var expectedGroups = seeds.Select(seed => ("train", seed, 0))
            .Concat(candidateRows.Select(candidate => ("validation", candidate.GetProperty("seed").GetInt32(), candidate.GetProperty("checkpoint_step").GetInt32())))
            .Append(("parity", report.GetProperty("candidate_seed").GetInt32(), report.GetProperty("checkpoint_step").GetInt32()));
        if (!smoke) expectedGroups = expectedGroups.Append(("oos", report.GetProperty("candidate_seed").GetInt32(), report.GetProperty("checkpoint_step").GetInt32()));
        if (!actions.Select(a => (a.Role, a.Seed, a.CheckpointStep)).Order().SequenceEqual(expectedGroups.Order()))
            throw new InvalidDataException("Action diagnostic group identities differ from the consumed actor roles/checkpoints.");
        foreach (int seed in seeds)
            if (actions.Where(a => a.Role == "train" && a.Seed == seed).Sum(a => a.AllActions.Denominator) != transitions)
                throw new InvalidDataException("Raw training action denominator differs from actual transitions.");
        foreach (var candidate in candidateRows)
        {
            int count = candidate.GetProperty("summary").GetProperty("episodes").EnumerateArray().Sum(e => e.GetProperty("completed_decisions").GetInt32());
            RequireActions("validation", candidate.GetProperty("seed").GetInt32(), candidate.GetProperty("checkpoint_step").GetInt32(), count);
        }
        int selectedSeed = report.GetProperty("candidate_seed").GetInt32(), selectedStep = report.GetProperty("checkpoint_step").GetInt32();
        if (!smoke) RequireActions("oos", selectedSeed, selectedStep, report.GetProperty("oos_episodes").EnumerateArray().Sum(e => e.GetProperty("completed_decisions").GetInt32()));
        using var parity = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, RlFiles.Parity)));
        RequireActions("parity", selectedSeed, selectedStep, parity.RootElement.GetProperty("paired_episodes").EnumerateArray().Sum(e => e.GetProperty("steps").GetArrayLength()));
        void RequireActions(string role, int seed, int checkpoint, int count)
        {
            var row = actions.SingleOrDefault(a => a.Role == role && a.Seed == seed && a.CheckpointStep == checkpoint);
            if (row?.AllActions.Denominator != count) throw new InvalidDataException("Raw output/evaluation diagnostic denominator mismatch.");
        }
    }

    public static void ValidateCounts(RlActionCounts counts)
    {
        var names = new RlActionDiagnosticCounter().Snapshot().Rates.Keys;
        if (counts is null || counts.Rates is null || counts.Denominator < 0 || !counts.Rates.Keys.Order().SequenceEqual(names.Order()))
            throw new InvalidDataException("Action count schema is invalid.");
        foreach (var metric in counts.Rates.Values)
            if (metric is null || metric.Count < 0 || metric.Count > counts.Denominator
                || metric.Rate != (counts.Denominator == 0 ? (decimal?)null : (decimal)metric.Count / counts.Denominator)
                || metric.Status != (counts.Denominator == 0 ? "NotApplicable" : "Available"))
                throw new InvalidDataException("Action rate differs from count/N or fabricates an empty denominator.");
        if (counts.Rates["raw_outside"].Count != counts.Rates["was_clipped"].Count)
            throw new InvalidDataException("Raw outside and Core clip counts disagree.");
        if (counts.Rates["size_lower"].Count + counts.Rates["size_upper"].Count > counts.Denominator
            || counts.Rates["stop_lower"].Count + counts.Rates["stop_upper"].Count > counts.Denominator)
            throw new InvalidDataException("Disjoint Core endpoints exceed the action denominator.");
    }
}
