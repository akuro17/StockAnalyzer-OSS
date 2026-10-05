#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

[Collection("ChildProcess")]
public sealed class RlFinancialQualificationTests
{
    public const string EnabledVariable = "SA_RL_R03_QUALIFICATION";
    public const string OutputVariable = "SA_RL_R03_OUTPUT_DIRECTORY";
    public const string RevalidateVariable = "SA_RL_R03_REVALIDATE";
    public sealed class FinancialQualificationAttribute : FactAttribute
    {
        public FinancialQualificationAttribute()
        {
            if (Environment.GetEnvironmentVariable(EnabledVariable) != "1")
                Skip = "Opt-in owned earlier-T15 and later-T16 smoke/full decimal financial qualification.";
        }
    }

    public sealed class FinancialRevalidationAttribute : FactAttribute
    {
        public FinancialRevalidationAttribute()
        {
            if (Environment.GetEnvironmentVariable(RevalidateVariable) != "1")
                Skip = "Opt-in revalidation of retained R03 artifacts without repeating training.";
        }
    }

    [FinancialRevalidation]
    public async Task RevalidateRetainedArtifactsWithCurrentCode()
    {
        string output = Environment.GetEnvironmentVariable(OutputVariable) ?? throw new InvalidOperationException("Explicit R03 output required.");
        RlTrainingResult Read(string file) => JsonSerializer.Deserialize<RlTrainingResult>(File.ReadAllText(Path.Combine(output, file)), RlFiles.Json)!;
        var direction = Read("direction_qualification_result.json"); var actor = Read("full_qualification_result.json");
        string root = Path.Combine(output, "owned_revalidation_" + Guid.NewGuid().ToString("N"));
        using var registry = new ModelGenerationRegistry(Path.Combine(root, "Generations"));
        Assert.Equal(direction.ModelId, (await registry.RegisterRlAsync(direction.Directory)).ModelId);
        Assert.Equal(actor.ModelId, (await registry.RegisterRlAsync(actor.Directory)).ModelId);
        VerifyEvidence(actor, smokeOnly: false);
        using var job = JsonDocument.Parse(File.ReadAllText(Path.Combine(actor.Directory, RlFiles.Job)));
        var costs = job.RootElement.GetProperty("costs").Deserialize<BacktestConfiguration>(RlFiles.Json)!;
        await CheckProtocol(actor, RlSourceSnapshot.Load(actor.Directory, RlTrainingValidation.FreezeLimits(continuous: true)), costs, output);
        Assert.Null(registry.RlActiveId); Assert.Null(registry.RlContinuousActiveId);
        Assert.True(await registry.ActivateRlAsync(direction.ModelId!));
        Assert.True(await registry.ActivateRlAsync(actor.ModelId!));
        await CheckTampering(direction, actor, registry, output, root);
        await Save(output, "current_code_revalidation.json", new { actor.ModelId, financial_version = RlFinancialParityContract.SpecificationId,
            registry_root = Path.Combine(root, "Generations"), registered = true, active_unchanged = true, protocol_failures_verified = true });
    }

    [FinancialQualification]
    public async Task ActualOwnedTrainingRegistrationTamperingAndProtocolAccounting()
    {
        string output = Environment.GetEnvironmentVariable(OutputVariable)
            ?? throw new InvalidOperationException("Explicit R03 output directory required.");
        string root = Path.Combine(output, "owned_qualification_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var registry = new ModelGenerationRegistry(Path.Combine(root, "Generations"));
        var costs = new BacktestConfiguration { InitialCapital = BacktestDefaults.InitialCapital,
            SizingModel = BacktestDefaults.SizingModel, SizingParameter = BacktestDefaults.SizingParameter,
            CommissionFlat = 2m, CommissionPerUnit = .5m, SlippageRatio = .001m,
            LiquidationPenaltyRatio = BacktestDefaults.LiquidationPenaltyRatio };
        var service = new RlTrainingService(registry, new TrainingJobGate(Path.Combine(root, "job-locks")), dataRoot: root);
        var progress = new Progress<RlTrainingProgress>(p => File.AppendAllText(Path.Combine(root, "progress.log"),
            $"{DateTimeOffset.UtcNow:O} {p.RunId} {p.State} {p.CompletedSteps} {p.Message}{Environment.NewLine}"));
        // Historical temporary generations are unavailable. Regenerate the unchanged approved earlier producer in this owned root.
        var direction = await service.RunAsync(new("AAPL", new(1980, 1, 1), new(2004, 1, 1), costs), progress);
        Assert.NotNull(direction.ModelId);
        await Save(output, "direction_qualification_result.json", direction);
        RlTrainingJob Job(bool smoke) => new("AAPL", new(2004, 1, 1), new(2026, 10, 4), costs, SmokeOnly: smoke)
            { FrozenDirectionModelId = direction.ModelId };
        var smoke = await service.RunAsync(Job(true), progress);
        Assert.Null(smoke.ModelId);
        VerifyEvidence(smoke, smokeOnly: true);
        await Assert.ThrowsAsync<InvalidDataException>(() => registry.RegisterRlAsync(smoke.Directory));
        await Save(output, "smoke_qualification_result.json", smoke);
        var full = await service.RunAsync(Job(false), progress);
        Assert.NotNull(full.ModelId);
        VerifyEvidence(full, smokeOnly: false);
        await Save(output, "full_qualification_result.json", full);
        Assert.Null(registry.RlActiveId); Assert.Null(registry.RlContinuousActiveId);
        Assert.Equal(2, registry.ListRl().Count);
        Assert.True(await registry.ActivateRlAsync(direction.ModelId!));
        Assert.True(await registry.ActivateRlAsync(full.ModelId!));
        using (var reload = new ModelGenerationRegistry(Path.Combine(root, "Generations")))
        {
            Assert.Equal(full.ModelId, reload.RlContinuousActiveId);
            Assert.Equal(direction.ModelId, reload.RlActiveId);
            Assert.NotNull(reload.GetRlGeneration(full.ModelId!));
        }
        var source = RlSourceSnapshot.Load(full.Directory, RlTrainingValidation.FreezeLimits(continuous: true));
        await CheckProtocol(full, source, costs, output);
        await CheckTampering(direction, full, registry, output, root);
    }

    private static async Task CheckTampering(RlTrainingResult direction, RlTrainingResult full,
        ModelGenerationRegistry registry, string output, string root)
    {
        var rejected = new List<string>();
        string tampered = Path.Combine(root, "tampered"); Directory.CreateDirectory(tampered);
        foreach (string file in Directory.EnumerateFiles(full.Directory, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(tampered, Path.GetRelativePath(full.Directory, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target);
        }
        string parityPath = Path.Combine(tampered, RlFiles.Parity);
        string originalParity = await File.ReadAllTextAsync(parityPath);
        var changes = new Dictionary<string, Action<JsonNode>>
        {
            ["cash"] = n => First(n)["financial_trace"]!["account"]!["equity_point"]!["cash"] = "999999999",
            ["equity"] = n => First(n)["financial_trace"]!["account"]!["equity_point"]!["equity"] = "999999999",
            ["peak"] = n => First(n)["financial_trace"]!["account"]!["peak_equity"] = "999999999",
            ["drawdown"] = n => First(n)["financial_trace"]!["account"]!["drawdown"] = "0.999",
            ["unrealized"] = n => First(n)["financial_trace"]!["account"]!["unrealized_pnl"] = "999999999",
            ["margin"] = n => First(n)["financial_trace"]!["account"]!["equity_point"]!["held_margin"] = "999999999",
            ["legacy_account_cash"] = n => First(n)["account"]!["cash"] = "999999999",
        };
        JsonNode? With(string field) => JsonNode.Parse(originalParity)!["paired_episodes"]!.AsArray()
            .SelectMany(e => e!["steps"]!.AsArray()).FirstOrDefault(s => s!["financial_trace"]![field]!.AsArray().Count > 0);
        if (With("fills") is not null)
            foreach (string field in new[] { "price", "commission", "slippage_amount" })
                changes["fill_" + field] = n => n["paired_episodes"]!.AsArray().SelectMany(e => e!["steps"]!.AsArray())
                    .First(s => s!["financial_trace"]!["fills"]!.AsArray().Count > 0)!["financial_trace"]!["fills"]![0]![field] = "999999999";
        if (With("trades") is not null)
            foreach (string field in new[] { "exit_price", "closed_gross", "closed_net" })
                changes["trade_" + field] = n => n["paired_episodes"]!.AsArray().SelectMany(e => e!["steps"]!.AsArray())
                    .First(s => s!["financial_trace"]!["trades"]!.AsArray().Count > 0)!["financial_trace"]!["trades"]![0]!["trade"]![field] = "999999999";
        foreach (var change in changes)
        {
            var node = JsonNode.Parse(originalParity)!; change.Value(node);
            await File.WriteAllTextAsync(parityPath, node.ToJsonString());
            await Assert.ThrowsAsync<InvalidDataException>(() => registry.RegisterRlAsync(tampered));
            rejected.Add(change.Key);
        }
        await File.WriteAllTextAsync(parityPath, originalParity);
        string metricsPath = Path.Combine(tampered, RlFiles.Model + RlFiles.EvaluationSuffix);
        string originalMetrics = await File.ReadAllTextAsync(metricsPath);
        foreach (string change in new[] { "saved_envelope", "resource_successes" })
        {
            var node = JsonNode.Parse(originalMetrics)!;
            if (change == "saved_envelope") node["financial_parity"]!["episodes"]![0]!["decisions"]![0]!["cash_error_bound"] = "999999999";
            else node["resources"]!["work"]!["successful_financial_transitions"] = 0;
            await File.WriteAllTextAsync(metricsPath, node.ToJsonString());
            await Assert.ThrowsAsync<InvalidDataException>(() => registry.RegisterRlAsync(tampered)); rejected.Add(change);
        }
        await File.WriteAllTextAsync(metricsPath, originalMetrics);
        Assert.Equal(full.ModelId, (await registry.RegisterRlAsync(tampered)).ModelId);
        Assert.Equal(2, registry.ListRl().Count);
        Assert.Equal(direction.ModelId, registry.RlActiveId); Assert.Equal(full.ModelId, registry.RlContinuousActiveId);
        await Save(output, "financial_qualification_result.json", new { direction, full, rejected,
            fill_tampering_exercised = With("fills") is not null, trade_tampering_exercised = With("trades") is not null,
            original_active_untouched = true, owned_active_unchanged_on_failure = true, registry_root = Path.Combine(root, "Generations") });
    }

    private static JsonNode First(JsonNode parity) => parity["paired_episodes"]![0]!["steps"]![0]!;
    private static Task Save(string directory, string name, object value)
        => File.WriteAllTextAsync(Path.Combine(directory, name), JsonSerializer.Serialize(value, RlFiles.Json));

    private static void VerifyEvidence(RlTrainingResult result, bool smokeOnly)
    {
        using var metrics = JsonDocument.Parse(File.ReadAllText(result.EvaluationPath));
        using var parity = JsonDocument.Parse(File.ReadAllText(Path.Combine(result.Directory, RlFiles.Parity)));
        var report = metrics.RootElement; var proof = report.GetProperty("financial_parity");
        int decisions = parity.RootElement.GetProperty("paired_episodes").EnumerateArray().Sum(e => e.GetProperty("steps").GetArrayLength());
        Assert.Equal(RlFinancialParityContract.SpecificationId, proof.GetProperty("specification_id").GetString());
        foreach (string layer in new[] { "same_input_observations", "same_raw_transitions", "own_observation_transitions" })
            Assert.Equal(decisions, proof.GetProperty(layer).GetInt32());
        var resources = report.GetProperty("resources"); var work = resources.GetProperty("work").Deserialize<RlWorkEvidence>(RlFiles.Json)!;
        Assert.Equal(work.TransitionAttempts, work.SuccessfulFinancialTransitions);
        Assert.Equal(work.EpisodeStartAttempts, work.SuccessfulEpisodeStarts);
        Assert.Equal(smokeOnly ? 0 : decisions * RlFinancialParityContract.ReplayLayers, work.RegistrationReservedTransitions);
        Assert.Equal(work.TransitionAttempts + work.RegistrationReservedTransitions, work.BudgetChargedTransitions);
        Assert.Equal(work.EpisodeStartAttempts + work.RegistrationReservedEpisodeStarts, work.BudgetChargedEpisodeStarts);
        Assert.True(work.BudgetChargedTransitions <= RlContract.ProcessedTransitionLimit);
        Assert.True(work.BudgetChargedEpisodeStarts <= RlContract.EpisodeStartLimit);
    }

    private static async Task CheckProtocol(RlTrainingResult actor, RlSourceSnapshot source, BacktestConfiguration costs, string output)
    {
        costs = RlContract.CreateConfiguration(costs);
        using var job = JsonDocument.Parse(File.ReadAllText(Path.Combine(actor.Directory, RlFiles.Job)));
        var metadata = job.RootElement.GetProperty("metadata").Deserialize<Dictionary<string, string>>()!;
        var run = Guid.NewGuid(); string session = Guid.NewGuid().ToString("N");
        using var host = new RlIpcHost(run, source, costs, actor.Directory, metadata, null, continuous: true);
        JsonElement Request(long sequence, string method, object payload) => JsonSerializer.SerializeToElement(new {
            protocol_version = RlContract.SchemaVersion, run_id = run.ToString("N"), sequence, session_id = session,
            contract_hash = RlContinuousContract.ContractHash, method, payload }, RlFiles.Json);
        host.Handle(Request(1, "Ready", new { temporal_producer_revision = RlTemporalContract.ProducerRevision }), default);
        host.Handle(Request(2, "Reset", new { role = "train", block_id = source.Catalog.Train[0].Id, seed = RlContract.CandidateSeeds[0], checkpoint = 0 }), default);
        var step = Request(3, "Step", new { actions = new[] { 1f, .5f } }); host.Handle(step, default);
        string successful = JsonSerializer.Serialize(host.CreateFailureTrace(), RlFiles.Json);
        Assert.Throws<InvalidDataException>(() => host.Handle(step, default));
        Assert.Equal(successful, JsonSerializer.Serialize(host.CreateFailureTrace(), RlFiles.Json));
        Assert.Throws<InvalidDataException>(() => host.Handle(Request(4, "Step", new { actions = Array.Empty<float>() }), default));
        var trace = JsonSerializer.SerializeToElement(host.CreateFailureTrace(), RlFiles.Json);
        var work = trace.GetProperty("work").Deserialize<RlWorkEvidence>(RlFiles.Json)!;
        Assert.Equal(2, work.TransitionAttempts); Assert.Equal(1, work.SuccessfulFinancialTransitions);
        Assert.Equal(1, work.EpisodeStartAttempts); Assert.Equal(1, work.SuccessfulEpisodeStarts);
        Assert.Equal(1, trace.GetProperty("last_successful_account").GetProperty("completed_decisions").GetInt32());
        await Save(output, "protocol_failure_trace.json", host.CreateFailureTrace());
    }
}
