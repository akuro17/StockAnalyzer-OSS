using System;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Models.ReinforcementLearning;

/// <summary>Separate evidence version: does not change the immutable numerical contract hash.</summary>
public static class RlDiagnosticsContract
{
    public const string SpecificationId = "SA-RL-PPO-NUMERICAL-GUARD-R02-v1";
    public const string FailureEvidenceFile = "diagnostics_failed.json";
    public static IReadOnlyList<string> MetricNames { get; } = Array.AsReadOnly(new[]
    { "policy_loss", "value_loss", "entropy_loss", "entropy", "total_loss", "approximate_kl", "log_std", "gradient_norm" });
    public static int UpdateLimit => RlContract.CandidateSeeds.Length * RlContinuousContract.TrainingTransitionsPerSeed / RlContinuousContract.RolloutSteps;
    public static int MinibatchLimit => RlContinuousContract.Epochs * RlContinuousContract.RolloutSteps / RlContract.BatchSize;
}

public sealed record RlDiagnosticMetric(int Count, double Minimum, double Maximum, double Mean);
public sealed record RlUpdateDiagnostics(string SpecificationId, int Seed, int CheckpointStep, string SourceRevision,
    string ContractHash, string DependenciesSha256, int Minibatches, int OptimizerSteps, bool EarlyStopped,
    Dictionary<string, RlDiagnosticMetric> Metrics);

public sealed record RlActionRate(int Count, decimal? Rate, string Status);
public sealed record RlActionCounts(int Denominator, Dictionary<string, RlActionRate> Rates);
public sealed record RlActionDiagnostics(string Role, int Seed, int CheckpointStep, RlActionCounts AllActions, RlActionCounts EntryActions);

/// <summary>Counts Core's consumed float32 actions; never includes baselines, Map fixtures or failed steps.</summary>
public sealed class RlActionDiagnosticCounter
{
    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal)
    {
        ["raw_outside"] = 0, ["size_lower"] = 0, ["size_upper"] = 0,
        ["stop_lower"] = 0, ["stop_upper"] = 0, ["was_clipped"] = 0,
    };
    private int _denominator;
    public RlActionDiagnosticCounter() { }
    internal RlActionDiagnosticCounter(RlActionCounts saved)
    {
        if (saved.Denominator < 0 || saved.Rates.Count != _counts.Count) throw new ArgumentException("Invalid saved action counters.");
        _denominator = saved.Denominator;
        foreach (string key in _counts.Keys.ToArray())
        {
            if (!saved.Rates.TryGetValue(key, out var rate) || rate.Count < 0 || rate.Count > _denominator)
                throw new ArgumentException("Invalid saved action count.");
            _counts[key] = rate.Count;
        }
    }
    public void Add(RlContinuousAction action)
    {
        _denominator = checked(_denominator + 1);
        Count("raw_outside", action.RawSize < RlContinuousContract.ActionMinimum || action.RawSize > RlContinuousContract.ActionMaximum
            || action.RawStop < RlContinuousContract.ActionMinimum || action.RawStop > RlContinuousContract.ActionMaximum);
        Count("size_lower", action.ClippedSize == RlContinuousContract.ActionMinimum);
        Count("size_upper", action.ClippedSize == RlContinuousContract.ActionMaximum);
        Count("stop_lower", action.ClippedStop == RlContinuousContract.ActionMinimum);
        Count("stop_upper", action.ClippedStop == RlContinuousContract.ActionMaximum);
        Count("was_clipped", action.WasClipped);
    }
    private void Count(string name, bool condition) { if (condition) _counts[name] = checked(_counts[name] + 1); }
    public RlActionCounts Snapshot() => new(_denominator, _counts.ToDictionary(p => p.Key,
        p => new RlActionRate(p.Value, _denominator == 0 ? null : (decimal)p.Value / _denominator,
            _denominator == 0 ? "NotApplicable" : "Available"), StringComparer.Ordinal));
}
