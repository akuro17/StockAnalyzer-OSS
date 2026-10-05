#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

[Collection("ChildProcess")]
public sealed class RlConstraintQualificationTests
{
    public const string EnabledVariable = "SA_RL_R04_QUALIFICATION";
    public const string OutputVariable = "SA_RL_R04_OUTPUT_DIRECTORY";
    public const string PreviousActorVariable = "SA_RL_R04_PREVIOUS_ACTOR_DIRECTORY";
    public const string ExistingFullVariable = "SA_RL_R04_EXISTING_FULL";
    public sealed class ConstraintQualificationAttribute : FactAttribute
    {
        public ConstraintQualificationAttribute()
        {
            if (Environment.GetEnvironmentVariable(EnabledVariable) != "1") Skip = "Opt-in owned R04 real stored-price constraint/report qualification.";
        }
    }
    [ConstraintQualification] public async Task ActualSmokeConstraints() => await Qualify(true);
    [ConstraintQualification] public async Task ActualFullConstraints() => await Qualify(false);
    [ConstraintQualification] public async Task RevalidateExistingFullConstraints()
    {
        string output = Environment.GetEnvironmentVariable(OutputVariable) ?? throw new InvalidOperationException("Explicit output required.");
        string directionDirectory = Environment.GetEnvironmentVariable(RlContinuousQualificationTests.DirectionDirectoryVariable)
            ?? throw new InvalidOperationException("Explicit qualified earlier T15 required.");
        string pointer = Environment.GetEnvironmentVariable(ExistingFullVariable) ?? throw new InvalidOperationException("Explicit owned Full result required.");
        var result = JsonSerializer.Deserialize<RlTrainingResult>(File.ReadAllText(pointer), RlFiles.Json)!;
        string root = Path.Combine(output, "owned_revalidation_" + Guid.NewGuid().ToString("N"));
        using var registry = new ModelGenerationRegistry(Path.Combine(root, "Generations"));
        var direction = await registry.RegisterRlAsync(directionDirectory);
        Assert.Equal(result.ModelId, (await registry.RegisterRlAsync(result.Directory)).ModelId);
        await Verify(result, registry, direction.ModelId, directionDirectory, root, output);
    }

    private static async Task Qualify(bool smoke)
    {
        string output = Environment.GetEnvironmentVariable(OutputVariable) ?? throw new InvalidOperationException("Explicit output required.");
        string directionDirectory = Environment.GetEnvironmentVariable(RlContinuousQualificationTests.DirectionDirectoryVariable)
            ?? throw new InvalidOperationException("Explicit qualified earlier T15 required.");
        string root = Path.Combine(output, "owned_" + (smoke ? "smoke_" : "full_") + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        using var registry = new ModelGenerationRegistry(Path.Combine(root, "Generations"));
        var direction = await registry.RegisterRlAsync(directionDirectory);
        using var directionJob = JsonDocument.Parse(File.ReadAllText(Path.Combine(directionDirectory, RlFiles.Job)));
        var costs = directionJob.RootElement.GetProperty("costs").Deserialize<BacktestConfiguration>(RlFiles.Json)!;
        var job = new RlTrainingJob("AAPL", new(2004, 1, 1), new(2026, 10, 4), costs, SmokeOnly: smoke) { FrozenDirectionModelId = direction.ModelId };
        var service = new RlTrainingService(registry, new TrainingJobGate(Path.Combine(root, "job-locks")), dataRoot: root);
        var progress = new Progress<RlTrainingProgress>(p => File.AppendAllText(Path.Combine(root, "progress.log"),
            $"{DateTimeOffset.UtcNow:O} {p.State} {p.CompletedSteps} {p.Message}{Environment.NewLine}"));
        var result = await service.RunAsync(job, progress);
        await File.WriteAllTextAsync(Path.Combine(output, (smoke ? "smoke" : "full") + "_qualification_result.json"), JsonSerializer.Serialize(result, RlFiles.Json));
        await Verify(result, registry, direction.ModelId, directionDirectory, root, output);
    }

    private static async Task Verify(RlTrainingResult result, ModelGenerationRegistry registry, string directionId,
        string directionDirectory, string root, string output)
    {
        using var directionJob = JsonDocument.Parse(File.ReadAllText(Path.Combine(directionDirectory, RlFiles.Job)));
        var costs = directionJob.RootElement.GetProperty("costs").Deserialize<BacktestConfiguration>(RlFiles.Json)!;
        using var metrics = JsonDocument.Parse(File.ReadAllText(result.EvaluationPath));
        var report = metrics.RootElement;
        var constraints = report.GetProperty("constraint_diagnostics").Deserialize<RlConstraintReport>(RlFinancialParityContract.TraceJson)!;
        Assert.Equal(RlConstraintContract.SpecificationId, constraints.SpecificationId);
        Assert.Equal(RlConstraintContract.SelectionMetric, constraints.SelectionMetric);
        Assert.Equal(RlContract.Gamma, constraints.LearningGamma); Assert.Equal(RlContract.EpisodeHorizon, constraints.HorizonBars);
        Assert.Equal(RlConstraintContract.InvestmentEfficacy, constraints.InvestmentEfficacy);
        Assert.All(constraints.PairedDeltas, d => Assert.Equal(d.ActorReturn - d.BaselineReturn, d.Delta));
        Assert.True(report.GetProperty("resources").GetProperty("processed_transitions").GetInt32() <= RlContract.ProcessedTransitionLimit);
        Assert.Null(registry.RlActiveId); Assert.Null(registry.RlContinuousActiveId);
        var hold = constraints.BaselinePolicies.Where(p => p.PolicyId == RlContract.HoldBaselineId).ToArray();
        Assert.All(hold, h => { Assert.Equal(0, h.Entries.EligibleBuys); Assert.Equal(RlEntryConstraintStatus.NotApplicable, h.Entries.Status); });
        if (result.SmokeOnly) { Assert.Null(result.ModelId); await Assert.ThrowsAsync<InvalidDataException>(() => registry.RegisterRlAsync(result.Directory)); return; }
        Assert.NotNull(result.ModelId);
        var source = RlSourceSnapshot.Load(result.Directory, RlTrainingValidation.FreezeLimits(continuous: true));
        // This supplemental train-block replay is deliberately outside selection and the job's validation/OOS work ledger.
        var trainBlock = source.Catalog.Train[0];
        using var fixedDirection = new RlQModel(Path.Combine(result.Directory, RlContinuousContract.DirectionModel));
        using var supplemental = new BacktestEngine(IndicatorFactory.Default).CreateRlContinuousSession(source.CreateInput(trainBlock), costs);
        var state = supplemental.Reset(); var attempts = new List<RlEntryExecution>();
        while (!state.Terminated)
        {
            var action = RlContract.SelectAction(fixedDirection.Infer(RlObservationBuilder.Build(state, fixedDirection.Scaler)));
            var step = supplemental.StepFixedControl(action); state = step.Snapshot;
            if (step.EntryExecution is { } attempt) attempts.Add(attempt);
        }
        var supplementalConstraints = RlConstraintEvaluation.Finish(state, supplemental.GetResult(), attempts);
        Assert.True(supplementalConstraints.Entries.QuantityCapExceeded > 0);
        Assert.Equal(source.Bars.Length, constraints.Coverage.SupportBars);
        Assert.Equal(source.Bars.Length - RlContract.WindowLength, constraints.Coverage.PossibleDecisionBars);
        foreach (string policy in new[] { RlContinuousContract.Target, RlContinuousContract.PairedBaselineId, RlContract.HoldBaselineId })
        {
            var replay = await new QValueService(registry).ReplayAsync(new(result.ModelId!, result.Directory, source.Catalog.Validation[0].Id, 0, policy));
            Assert.NotNull(replay.EvaluationConstraints); Assert.NotNull(replay.Continuous!.SelectedPolicyControl);
            if (policy != RlContinuousContract.Target)
            { Assert.Equal(.30m, replay.Continuous.SelectedPolicyControl!.SizeRatio); Assert.Equal(.02m, replay.Continuous.SelectedPolicyControl.StopDistance); }
        }
        Assert.True(await registry.ActivateRlAsync(directionId)); Assert.True(await registry.ActivateRlAsync(result.ModelId!));
        var rejections = new List<string>();
        string tampered = Path.Combine(root, "tampered"); Directory.CreateDirectory(tampered);
        foreach (string file in Directory.EnumerateFiles(result.Directory, "*", SearchOption.AllDirectories))
        { string target = Path.Combine(tampered, Path.GetRelativePath(result.Directory, file)); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target); }
        string metricsPath = Path.Combine(tampered, RlFiles.Model + RlFiles.EvaluationSuffix);
        string original = File.ReadAllText(metricsPath);
        var changes = new Dictionary<string, Action<JsonNode>>
        {
            ["coverage"] = n => n["constraint_diagnostics"]!["coverage"]!["support_bars"] = 0,
            ["paired_delta"] = n => n["constraint_diagnostics"]!["paired_deltas"]![0]!["delta"] = "999",
            ["cap_count"] = n => n["validation_episodes"]![0]!["constraints"]!["entries"]!["quantity_cap_exceeded"] = 999,
            ["exit_sensitivity"] = n => n["oos_episodes"]![0]!["constraints"]!["exit_cost_sensitivity"]!["adjusted_equity"] = "0",
            ["investment_claim"] = n => n["constraint_diagnostics"]!["investment_efficacy"] = "Established",
        };
        foreach (var change in changes)
        {
            var node = JsonNode.Parse(original)!; change.Value(node); await File.WriteAllTextAsync(metricsPath, node.ToJsonString());
            await Assert.ThrowsAsync<InvalidDataException>(() => registry.RegisterRlAsync(tampered)); rejections.Add(change.Key);
        }
        await File.WriteAllTextAsync(metricsPath, original);
        string jobPath = Path.Combine(tampered, RlFiles.Job); string originalJob = File.ReadAllText(jobPath);
        var removed = JsonNode.Parse(originalJob)!; removed.AsObject().Remove(RlConstraintContract.MetadataKey);
        await File.WriteAllTextAsync(jobPath, removed.ToJsonString());
        await Assert.ThrowsAsync<InvalidDataException>(() => registry.RegisterRlAsync(tampered)); rejections.Add("downgrade");
        await File.WriteAllTextAsync(jobPath, originalJob);
        Assert.Equal(result.ModelId, (await registry.RegisterRlAsync(tampered)).ModelId);
        Assert.Equal(directionId, registry.RlActiveId); Assert.Equal(result.ModelId, registry.RlContinuousActiveId);
        using var reload = new ModelGenerationRegistry(Path.Combine(root, "Generations")); Assert.Equal(result.ModelId, reload.RlContinuousActiveId);
        string? previousDirectory = Environment.GetEnvironmentVariable(PreviousActorVariable);
        if (previousDirectory is not null)
        {
            using var previous = JsonDocument.Parse(File.ReadAllText(Path.Combine(previousDirectory, RlFiles.Model + RlFiles.EvaluationSuffix)));
            foreach (string key in new[] { "candidate_seed", "checkpoint_step", "validation_mean_return", "oos_mean_return" })
                Assert.Equal(previous.RootElement.GetProperty(key).GetRawText(), report.GetProperty(key).GetRawText());
            using var legacy = new ModelGenerationRegistry(Path.Combine(root, "LegacyGenerations"));
            await legacy.RegisterRlAsync(directionDirectory); var old = await legacy.RegisterRlAsync(previousDirectory);
            Assert.NotNull(old); Assert.Equal(RlContinuousContract.ContractHash, old.ContractHash);
        }
        await File.WriteAllTextAsync(Path.Combine(output, "constraint_qualification_evidence.json"), JsonSerializer.Serialize(new {
            result, constraints = report.GetProperty("constraint_diagnostics"), rejections,
            score_unchanged_vs_r03 = previousDirectory is not null, original_active_untouched = true,
            supplemental_train_block = trainBlock.Id, supplemental_train_transitions = state.CompletedDecisions,
            supplemental_train_constraints = RlConstraintEvaluation.ToWire(supplementalConstraints),
            supplemental_excluded_from_selection_and_job_ledger = true,
            own_active_unchanged_on_rejection = true, registry_root = Path.Combine(root, "Generations") }, RlFiles.Json));
    }
}
