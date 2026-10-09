using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// Opt-in harness of the observation ablation (FX1c): trains the missing runs of the control arm (v1) and of the v2-a arm on ONE stored panel with run-level seed offsets
/// 0, 3, 6, ..., evaluates the pre-registered rule (<see cref="RlPortfolioAblationProtocol"/>) and writes the result files. It asserts that the harness ran and that the
/// result files are consistent, never what the verdict is. Stored v1 runs can stand in for the first control runs (they must be on the same panel, plan and costs;
/// the comparison refuses anything else). Parameters come from environment variables because the run takes minutes to hours:
/// <c>SA_RL_ABLATION=1</c> enables it; <c>SA_RL_ABLATION_PANEL</c> the stored panel folder; <c>SA_RL_ABLATION_OUT</c> the output folder (default: a temp folder);
/// <c>SA_RL_ABLATION_RUNS</c> runs per arm (default: the protocol's 5; fewer gives an Incomplete verdict); <c>SA_RL_ABLATION_CONTROL</c> stored v1 run folders (';'
/// separated, REQUIRED: they are control runs 0, 1, ... and must have been trained with seed offset 0, 3, ...; the first one fixes the costs and the weight cap of the
/// whole experiment, so the harness has no default of its own); <c>SA_RL_ABLATION_PLAN</c> "train,validation,test" block counts of a
/// BlockCount plan (default: the ratio rule); <c>SA_RL_ABLATION_SMOKE=1</c> uses the smoke budget (everything, including the stored control, must then be smoke).
/// The class starts the real portfolio worker, so it runs alone like every test that starts a child process.
/// </summary>
[Collection("ChildProcess")]
public sealed class RlPortfolioAblationHarnessTests
{
    public const string EnabledVariable = "SA_RL_ABLATION";

    public sealed class AblationFactAttribute : FactAttribute
    {
        public AblationFactAttribute()
        {
            if (Environment.GetEnvironmentVariable(EnabledVariable) != "1")
                Skip = $"Opt-in observation ablation (real worker, minutes to hours); set {EnabledVariable}=1 and SA_RL_ABLATION_PANEL.";
        }
    }

    private static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;

    [AblationFact]
    public async Task TheObservationAblationRunsAndWritesItsResult()
    {
        string panelDirectory = Env("SA_RL_ABLATION_PANEL") ?? throw new InvalidOperationException("SA_RL_ABLATION_PANEL names the stored panel folder.");
        string output = Env("SA_RL_ABLATION_OUT") ?? Path.Combine(Path.GetTempPath(), "sa_rl_ablation_" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture));
        int runs = int.Parse(Env("SA_RL_ABLATION_RUNS") ?? RlPortfolioAblationContract.RunsPerArm.ToString(System.Globalization.CultureInfo.InvariantCulture), System.Globalization.CultureInfo.InvariantCulture);
        bool smoke = Env("SA_RL_ABLATION_SMOKE") == "1";
        string[] controlDirectories = (Env("SA_RL_ABLATION_CONTROL") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        RlPanelPartitionPlan? plan = null;
        if (Env("SA_RL_ABLATION_PLAN") is { } planText)
        {
            int[] counts = planText.Split(',').Select(t => int.Parse(t, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            plan = new RlPanelPartitionPlan(RlPortfolioPartitionMode.BlockCount, new RlPanelBlockRequirement(counts[0], counts[1], counts[2]));
        }
        Assert.InRange(runs, 1, RlPortfolioAblationContract.RunsPerArm);
        Assert.True(controlDirectories.Length > 0, "SA_RL_ABLATION_CONTROL names at least one stored v1 run: its costs and weight cap are the experiment's.");
        Assert.True(controlDirectories.Length <= runs);
        Directory.CreateDirectory(output);

        var settings = new RlPortfolioSettings();
        var limits = RlTrainingValidation.FreezeLimits(continuous: true);
        var panel = RlPanelSnapshot.Load(panelDirectory, limits, settings, RlPanelEvaluationMode.ShortHistory, plan, default);

        // Stored control runs: read, validated against the panel and their own model, and used as they are.
        var control = new List<RlPortfolioEvaluationReport>();
        var controlRuns = new List<string>();
        foreach (string directory in controlDirectories)
        {
            var report = RlPortfolioEvaluationReports.Read(Path.Combine(directory, RlPortfolioEvaluationContract.ReportFile));
            RlPortfolioEvaluationReports.Validate(report, panel.Manifest, RlPanelEvaluationMode.ShortHistory,
                ContentHash.OfFile(Path.Combine(directory, RlFiles.Model)), report.Costs, report.MaxWeightPercent, plan, RlPortfolioObservationLayout.V1);
            Assert.Equal(smoke, report.SmokeOnly);
            control.Add(report);
            controlRuns.Add(Path.GetFileName(directory.TrimEnd('\\', '/')));
        }
        var costs = control[0].Costs;
        Assert.Equal(control[0].MaxWeightPercent, settings.MaxWeightPercent);

        var runner = new RlPortfolioTrainingRunner(dataRoot: Path.Combine(output, "artifacts"));
        // The wall time of every training is recorded: the cost gate of the protocol (job deadline, observation size) is read from it.
        var timings = new List<object>();
        async Task<RlPortfolioTrainingResult> Train(RlPortfolioObservationLayout layout, int run)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var result = await runner.RunAsync(new RlPortfolioTrainingRequest(panelDirectory, costs, settings, SmokeOnly: smoke, Mode: RlPanelEvaluationMode.ShortHistory,
                Plan: plan, Layout: layout, SeedOffset: RlPortfolioAblationContract.SeedOffset(run)));
            timings.Add(new { arm = layout.SpecificationId, run, seconds = Math.Round(watch.Elapsed.TotalSeconds, 1),
                observation_length = RlPortfolioResources.ObservationLength(panel.SymbolCount, layout) });
            return result;
        }

        var armRuns = new List<string>();
        var arm = new List<RlPortfolioEvaluationReport>();
        for (int run = 0; run < runs; run++)
        {
            if (run >= control.Count)
            {
                var trained = await Train(RlPortfolioObservationLayout.V1, run);
                control.Add(trained.Evaluation);
                controlRuns.Add(Path.GetFileName(trained.Directory));
            }
            var v2 = await Train(RlPortfolioObservationLayout.V2A, run);
            arm.Add(v2.Evaluation);
            armRuns.Add(Path.GetFileName(v2.Directory));
        }

        var result = RlPortfolioAblationProtocol.Evaluate(control, arm);
        // Descriptive noise floor (not part of the rule): how far two v1 runs that differ only in the run-level seed lie apart, per judgement cell (run j minus run 0).
        var controlSpread = Enumerable.Range(1, control.Count - 1).Select(j => new
        {
            run = j,
            cells = RlPortfolioTransferComparison.Compare(control[j], control[0]).Cells
                .Where(c => c.Comparable && !c.SelectionContaminated && c.PairedDelta is not null)
                .Select(c => new { partition = c.Partition, group = c.Group.ToString(), blocks = c.BlockCount, wins = c.Wins, losses = c.Losses, mean_delta = c.PairedDelta!.Mean }).ToArray(),
        }).ToArray();
        await WriteAtomicAsync(Path.Combine(output, "ablation_result.json"), JsonSerializer.Serialize(new
        {
            panel = panelDirectory, plan = plan is null ? "ratio rule" : $"{plan.Requirement.TrainBlocks}/{plan.Requirement.ValidationBlocks}/{plan.Requirement.TestBlocks} blocks",
            smoke, control_runs = controlRuns, arm_runs = armRuns, timings, control_spread = controlSpread, control_contract = RlPortfolioContract.ContractHash, arm_contract = RlPortfolioV2Contract.ContractHash, result,
        }, new JsonSerializerOptions(RlFiles.Json) { WriteIndented = true }));
        var text = new StringBuilder().AppendLine($"# Observation ablation v2-a against v1: {result.Verdict}")
            .AppendLine($"Panel: {panelDirectory}").AppendLine($"Runs per arm: {result.RunsPerArm} (protocol: {RlPortfolioAblationContract.RunsPerArm}); passed {result.PassedRuns}, failed {result.FailedRuns}, not evaluable {result.NotEvaluableRuns}")
            .AppendLine($"Smoke budget: {smoke}").AppendLine();
        foreach (var run in result.Runs)
        {
            text.AppendLine($"## Run {run.RunIndex} (seed offset {RlPortfolioAblationContract.SeedOffset(run.RunIndex)}): {run.Verdict}");
            foreach (var c in run.Cells)
                text.AppendLine($"- {c.Partition}/{c.Group}: blocks {c.BlockCount}, wins {c.Wins}, ties {c.Ties}, losses {c.Losses}, mean delta {c.MeanDelta}, win share {c.WinShare}, qualifying {c.Qualifying}");
        }
        text.AppendLine().AppendLine(result.Statement);
        await WriteAtomicAsync(Path.Combine(output, "ablation_result.md"), text.ToString());

        Assert.Equal(runs, result.Runs.Length);
        Assert.True(File.Exists(Path.Combine(output, "ablation_result.json")));
    }

    /// <summary>Temp file next to the target, then replace (or move when the target is new): a result file is never half written.</summary>
    private static async Task WriteAtomicAsync(string path, string text)
    {
        string temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, text, new UTF8Encoding(false));
        if (File.Exists(path)) File.Replace(temp, path, null);
        else File.Move(temp, path);
    }
}
