#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

[Collection("ChildProcess")]
public sealed class RlDiagnosticsTests
{
    [Fact]
    public async Task PinnedPpoGuardPreservesUpdatesAndStopsInvalidArithmetic()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "StockAnalyzer.sln"))) root = root.Parent;
        Assert.NotNull(root);
        string python = Path.Combine(root!.FullName, "StockAnalyzer.Python", ".venv", OperatingSystem.IsWindows() ? "Scripts/python.exe" : "bin/python");
        var start = new ProcessStartInfo(python) { WorkingDirectory = root.FullName, UseShellExecute = false };
        start.ArgumentList.Add(Path.Combine(root.FullName, "Tests", "StockAnalyzer.Core.Tests", "Assets", "check_rl_ppo_guard.py"));
        Assert.Contains("R02 PPO guard equivalence, injection, GAE and entropy: PASS",
            await TrainingPythonTestRunner.RunAsync(start, TimeSpan.FromMinutes(2)));
    }

    [Fact]
    public void EmptyDenominatorIsNotApplicableAndExactEndpointsAreNotOutside()
    {
        var empty = new RlActionDiagnosticCounter().Snapshot();
        Assert.Equal(0, empty.Denominator);
        Assert.All(empty.Rates.Values, r => { Assert.Null(r.Rate); Assert.Equal("NotApplicable", r.Status); });
        RlDiagnosticsValidator.ValidateCounts(empty);
        var all = new RlActionDiagnosticCounter();
        foreach (var raw in new[] { (-3f, 3f), (-1f, -1f), (0f, 0f), (1f, 1f), (.125f, -.125f) })
            all.Add(RlContinuousContract.Map(raw.Item1, raw.Item2));
        var counts = all.Snapshot();
        Assert.Equal(5, counts.Denominator);
        Assert.Equal(.2m, counts.Rates["raw_outside"].Rate);
        Assert.Equal(.4m, counts.Rates["size_lower"].Rate);
        Assert.Equal(.2m, counts.Rates["size_upper"].Rate);
        Assert.Equal(.2m, counts.Rates["stop_lower"].Rate);
        Assert.Equal(.4m, counts.Rates["stop_upper"].Rate);
        RlDiagnosticsValidator.ValidateCounts(counts);
        counts.Rates["raw_outside"] = counts.Rates["raw_outside"] with { Rate = 0m };
        Assert.Throws<InvalidDataException>(() => RlDiagnosticsValidator.ValidateCounts(counts));
        empty.Rates["was_clipped"] = empty.Rates["was_clipped"] with { Rate = 0m };
        Assert.Throws<InvalidDataException>(() => RlDiagnosticsValidator.ValidateCounts(empty));
    }

    [Fact]
    public void InvalidUpdateIdentityAggregateAndCountsCannotQualify()
    {
        const string identity = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        int minibatches = RlDiagnosticsContract.MinibatchLimit;
        var metrics = RlDiagnosticsContract.MetricNames.ToDictionary(name => name,
            name => new RlDiagnosticMetric(name == "log_std" ? minibatches * RlContinuousContract.ActionCount : minibatches, 0d, 1d, .5));
        var row = new RlUpdateDiagnostics(RlDiagnosticsContract.SpecificationId, RlContract.CandidateSeeds[0], RlContinuousContract.RolloutSteps,
            identity, RlContinuousContract.ContractHash, identity, minibatches, minibatches, false, metrics);
        RlDiagnosticsValidator.ValidateUpdate(row, identity, identity);
        Assert.Throws<InvalidDataException>(() => RlDiagnosticsValidator.ValidateUpdate(row with { ContractHash = identity }, identity, identity));
        Assert.Throws<InvalidDataException>(() => RlDiagnosticsValidator.ValidateUpdate(row with { CheckpointStep = 1 }, identity, identity));
        Assert.Throws<InvalidDataException>(() => RlDiagnosticsValidator.ValidateUpdate(row with { OptimizerSteps = 0 }, identity, identity));
        Assert.Throws<InvalidDataException>(() => RlDiagnosticsValidator.ValidateUpdate(row with { Minibatches = minibatches - 1, OptimizerSteps = minibatches - 1 }, identity, identity));
        metrics["gradient_norm"] = metrics["gradient_norm"] with { Mean = double.PositiveInfinity };
        Assert.Throws<InvalidDataException>(() => RlDiagnosticsValidator.ValidateUpdate(row, identity, identity));
        metrics.Remove("gradient_norm");
        Assert.Throws<InvalidDataException>(() => RlDiagnosticsValidator.ValidateUpdate(row, identity, identity));
        // Legitimate target-KL stopping before the first step has no fabricated gradient statistic.
        metrics = RlDiagnosticsContract.MetricNames.Where(n => n != "gradient_norm").ToDictionary(name => name,
            name => new RlDiagnosticMetric(name == "log_std" ? RlContinuousContract.ActionCount : 1, 0d, 1d, .5));
        RlDiagnosticsValidator.ValidateUpdate(row with { Minibatches = 1, OptimizerSteps = 0, EarlyStopped = true, Metrics = metrics }, identity, identity);
    }
}
