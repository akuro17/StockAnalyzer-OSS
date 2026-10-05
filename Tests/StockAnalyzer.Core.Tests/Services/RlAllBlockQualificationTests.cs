#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

[Collection("ChildProcess")]
public sealed class RlAllBlockQualificationTests
{
    public const string EnabledVariable = "SA_RL_R06_QUALIFICATION";
    public const string OutputVariable = "SA_RL_R06_OUTPUT_DIRECTORY";
    public sealed class AllBlockQualificationAttribute : FactAttribute
    {
        public AllBlockQualificationAttribute()
        { if (Environment.GetEnvironmentVariable(EnabledVariable) != "1") Skip = "Opt-in owned R06 real all-block qualification."; }
    }
    private static string Output => Environment.GetEnvironmentVariable(OutputVariable) ?? throw new InvalidOperationException("Explicit output required.");
    private static BacktestConfiguration Costs => RlContract.CreateConfiguration(new BacktestConfiguration { InitialCapital = 1000000m,
        CommissionFlat = 2m, CommissionPerUnit = .5m, SlippageRatio = .001m,
        SizingModel = BacktestDefaults.SizingModel, SizingParameter = BacktestDefaults.SizingParameter });
    [AllBlockQualification] public async Task EarlierDirectionPrerequisite()
    {
        string root = Path.Combine(Output, "owned_direction_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        using var registry = new ModelGenerationRegistry(Path.Combine(root, "Generations"));
        var service = new RlTrainingService(registry, new TrainingJobGate(Path.Combine(root, "job-locks")), dataRoot: root);
        var result = await service.RunAsync(new("AAPL", new(1980, 1, 1), new(2002, 1, 1), Costs), Progress(root));
        Assert.NotNull(result.ModelId); Assert.Null(registry.RlActiveId);
        var evidence = registry.CaptureRlTemporalEvidence(result.ModelId!); Assert.NotNull(evidence);
        Assert.True(evidence!.InformationUpperExclusive <= new DateOnly(2002, 1, 1));
        await File.WriteAllTextAsync(Path.Combine(Output, "direction_qualification_result.json"), JsonSerializer.Serialize(result, RlFiles.Json));
    }
    [AllBlockQualification] public Task ActualAllBlockSmoke() => Qualify(true);
    [AllBlockQualification] public Task ActualAllBlockFull() => Qualify(false);
    [AllBlockQualification] public async Task RevalidateExistingAllBlockFull()
    {
        var earlier = JsonSerializer.Deserialize<RlTrainingResult>(File.ReadAllText(Path.Combine(Output, "direction_qualification_result.json")), RlFiles.Json)!;
        var result = JsonSerializer.Deserialize<RlTrainingResult>(File.ReadAllText(Path.Combine(Output, "full_qualification_result.json")), RlFiles.Json)!;
        string root = Path.Combine(Output, "owned_revalidation_" + Guid.NewGuid().ToString("N"));
        using var registry = new ModelGenerationRegistry(Path.Combine(root, "Generations"));
        var direction = await registry.RegisterRlAsync(earlier.Directory);
        Assert.Equal(result.ModelId, (await registry.RegisterRlAsync(result.Directory)).ModelId);
        await Verify(result, registry, direction.ModelId, root, false);
    }
    [AllBlockQualification] public async Task LegacyFiveBlockActorStillRegistersAndReplays()
    {
        string directory = Environment.GetEnvironmentVariable("SA_RL_R06_LEGACY_ACTOR_DIRECTORY") ?? throw new InvalidOperationException("Explicit legacy actor required.");
        string direction = Environment.GetEnvironmentVariable("SA_RL_R06_LEGACY_DIRECTION_DIRECTORY") ?? throw new InvalidOperationException("Exact qualified legacy T15 dependency required.");
        string root = Path.Combine(Output, "owned_legacy_" + Guid.NewGuid().ToString("N"));
        using var registry = new ModelGenerationRegistry(Path.Combine(root, "Generations"));
        await registry.RegisterRlAsync(direction);
        var generation = await registry.RegisterRlAsync(directory);
        var source = RlSourceSnapshot.Load(directory, RlTrainingValidation.FreezeLimits(continuous: true));
        Assert.False(source.ExpandedEvaluation); Assert.Equal(5, source.Catalog.Validation.Length); Assert.Equal(5, source.Catalog.Oos.Length);
        var replay = await new QValueService(registry).ReplayAsync(new(generation.ModelId, directory, source.Catalog.Oos[^1].Id, 0, RlContinuousContract.Target));
        Assert.Null(replay.AllBlockEvaluation); Assert.NotNull(replay.EvaluationConstraints);
        Assert.Null(registry.RlActiveId); Assert.Null(registry.RlContinuousActiveId);
        await File.WriteAllTextAsync(Path.Combine(Output, "legacy_qualification_evidence.json"), JsonSerializer.Serialize(new {
            generation.ModelId, source_directory = directory, legacy_validation = 5, legacy_oos = 5, replayed = true, original_active_untouched = true }, RlFiles.Json));
    }
    private static IProgress<RlTrainingProgress> Progress(string root) => new Progress<RlTrainingProgress>(p =>
        File.AppendAllText(Path.Combine(root, "progress.log"), $"{DateTimeOffset.UtcNow:O} {p.State} {p.CompletedSteps} {p.Message}{Environment.NewLine}"));
    private static async Task Qualify(bool smoke)
    {
        var earlier = JsonSerializer.Deserialize<RlTrainingResult>(File.ReadAllText(Path.Combine(Output, "direction_qualification_result.json")), RlFiles.Json)!;
        string root = Path.Combine(Output, "owned_" + (smoke ? "smoke_" : "full_") + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        using var registry = new ModelGenerationRegistry(Path.Combine(root, "Generations"));
        var direction = await registry.RegisterRlAsync(earlier.Directory);
        var service = new RlTrainingService(registry, new TrainingJobGate(Path.Combine(root, "job-locks")), dataRoot: root);
        var result = await service.RunAsync(new RlTrainingJob("AAPL", new(2002, 1, 1), new(2026, 10, 4), Costs, SmokeOnly: smoke)
            { FrozenDirectionModelId = direction.ModelId, EvaluationSpecificationId = RlAllBlockEvaluationContract.SpecificationId }, Progress(root));
        await File.WriteAllTextAsync(Path.Combine(Output, (smoke ? "smoke" : "full") + "_qualification_result.json"), JsonSerializer.Serialize(result, RlFiles.Json));
        await Verify(result, registry, direction.ModelId, root, smoke);
    }
    private static async Task Verify(RlTrainingResult result, ModelGenerationRegistry registry, string directionId, string root, bool smoke)
    {
        using var metrics = JsonDocument.Parse(File.ReadAllText(result.EvaluationPath)); var report = metrics.RootElement;
        var expanded = report.GetProperty(RlAllBlockEvaluationContract.ReportKey).Deserialize<RlAllBlockEvaluationReport>(RlFinancialParityContract.TraceJson)!;
        Assert.Equal(6, expanded.Validation.Count); Assert.Equal(smoke ? null : 6, expanded.Oos?.Count);
        Assert.Equal(smoke ? 11008 : 52224, expanded.ResourceEstimate.MaximumTransitions);
        Assert.True(report.GetProperty("resources").GetProperty("processed_transitions").GetInt64() <= expanded.ResourceEstimate.MaximumTransitions);
        Assert.Null(registry.RlActiveId); Assert.Null(registry.RlContinuousActiveId);
        var source = RlSourceSnapshot.Load(result.Directory, RlTrainingValidation.FreezeLimits(continuous: true), smoke);
        Assert.Equal(6218, source.Bars.Length); Assert.Equal(6, source.Catalog.Validation.Length);
        if (smoke) { Assert.Null(result.ModelId); await Assert.ThrowsAsync<InvalidDataException>(() => registry.RegisterRlAsync(result.Directory)); return; }
        Assert.NotNull(result.ModelId);
        var q = new QValueService(registry);
        foreach (var block in new[] { source.Catalog.Validation[^1], source.Catalog.Oos[^1] })
        {
            var replay = await q.ReplayAsync(new(result.ModelId!, result.Directory, block.Id, 0, RlContinuousContract.Target));
            Assert.NotNull(replay.AllBlockEvaluation); Assert.Equal(6, replay.AllBlockEvaluation!.Validation.Count);
            Assert.Equal(source.Bars[block.StartIndex + RlContract.WindowLength - 1].Timestamp, replay.Anchor);
        }
        Assert.True(await registry.ActivateRlAsync(directionId)); Assert.True(await registry.ActivateRlAsync(result.ModelId!));
        string tampered = Path.Combine(root, "tampered"); Directory.CreateDirectory(tampered);
        foreach (string file in Directory.EnumerateFiles(result.Directory, "*", SearchOption.AllDirectories))
        { string target = Path.Combine(tampered, Path.GetRelativePath(result.Directory, file)); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target); }
        string path = Path.Combine(tampered, RlFiles.Model + RlFiles.EvaluationSuffix); string original = File.ReadAllText(path);
        var changes = new Dictionary<string, Action<JsonNode>> {
            ["validation_count"] = n => n["validation_episodes"]!.AsArray().RemoveAt(5),
            ["oos_count"] = n => n["oos_episodes"]!.AsArray().RemoveAt(5),
            ["median"] = n => n["all_block_evaluation"]!["validation"]!["median"] = "999",
            ["positive_rate"] = n => n["all_block_evaluation"]!["oos"]!["positive_rate"] = "999",
            ["paired_delta"] = n => n["all_block_evaluation"]!["paired_oos_delta"]!["mean"] = "999",
            ["resources"] = n => n["all_block_evaluation"]!["resource_estimate"]!["maximum_transitions"] = 1,
            ["schema"] = n => n["schema_version"] = 1,
            ["coverage"] = n => n["all_block_evaluation"]!["coverage"]!["support_bars"] = 1,
            ["candidate_distribution"] = n => n["candidate_evaluations"]![0]!["summary"]!["return_distribution"]!["count"] = 5,
            ["random_distribution"] = n => n["baselines"]!["oos"]![RlContract.RandomBaselineId]!["return_distribution"]!["count"] = 6,
        };
        var rejected = new List<string>();
        foreach (var change in changes)
        {
            var node = JsonNode.Parse(original)!; change.Value(node); await File.WriteAllTextAsync(path, node.ToJsonString());
            await Assert.ThrowsAsync<InvalidDataException>(() => registry.RegisterRlAsync(tampered)); rejected.Add(change.Key);
        }
        await File.WriteAllTextAsync(path, original);
        foreach (string name in new[] { RlFiles.Job, RlFiles.Catalog })
        {
            string artifact = Path.Combine(tampered, name); string saved = File.ReadAllText(artifact);
            var node = JsonNode.Parse(saved)!; node.AsObject().Remove(RlAllBlockEvaluationContract.MetadataKey);
            await File.WriteAllTextAsync(artifact, node.ToJsonString());
            await Assert.ThrowsAsync<InvalidDataException>(() => registry.RegisterRlAsync(tampered)); rejected.Add(name + "_downgrade");
            await File.WriteAllTextAsync(artifact, saved);
        }
        Assert.Equal(result.ModelId, (await registry.RegisterRlAsync(tampered)).ModelId);
        Assert.Equal(result.ModelId, registry.RlContinuousActiveId);
        using var reload = new ModelGenerationRegistry(Path.Combine(root, "Generations")); Assert.Equal(result.ModelId, reload.RlContinuousActiveId);
        await File.WriteAllTextAsync(Path.Combine(Output, "all_block_qualification_evidence.json"), JsonSerializer.Serialize(new {
            result, expanded = report.GetProperty(RlAllBlockEvaluationContract.ReportKey), rejected,
            actual_resources = report.GetProperty("resources"), registry_root = Path.Combine(root, "Generations"),
            original_active_untouched = true, tail_replay_verified = true }, RlFiles.Json));
    }
}
