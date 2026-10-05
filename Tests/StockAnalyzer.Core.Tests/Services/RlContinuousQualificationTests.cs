using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>Opt-in actual PPO training on stored local prices with an independently qualified immutable direction.</summary>
public sealed class RlContinuousQualificationTests
{
    public const string EnabledVariable = "SA_RL_T16_QUALIFICATION";
    public const string DirectionDirectoryVariable = "SA_RL_T16_DIRECTION_DIRECTORY";
    public const string ActorDirectoryVariable = "SA_RL_T16_ACTOR_DIRECTORY";
    public const string StartDateVariable = "SA_RL_T16_START_DATE";
    public const string EndDateVariable = "SA_RL_T16_END_DATE_EXCLUSIVE";
    public sealed class ContinuousQualificationAttribute : FactAttribute
    {
        public ContinuousQualificationAttribute()
        {
            if (Environment.GetEnvironmentVariable(EnabledVariable) != "1") Skip = "Opt-in actual local-data PPO qualification; requires a qualified T15 dependency.";
        }
    }
    [ContinuousQualification] public async Task SmokeQualification() => await RunAsync(smoke: true);
    [ContinuousQualification] public async Task FullQualification() => await RunAsync(smoke: false);

    private static async Task RunAsync(bool smoke)
    {
        string directionDirectory = Environment.GetEnvironmentVariable(DirectionDirectoryVariable)
            ?? throw new InvalidOperationException("Explicit qualified T15 artifact directory required.");
        string root = Path.Combine(Path.GetTempPath(), "sa_rl_T16_qualification_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var registry = new ModelGenerationRegistry(Path.Combine(root, "Generations"));
        var direction = await registry.RegisterRlAsync(directionDirectory);
        Assert.Equal(RlFiles.Target, direction.Target);
        using var captured = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directionDirectory, RlFiles.Job)));
        var job = captured.RootElement;
        var costs = job.GetProperty("costs").Deserialize<BacktestConfiguration>(RlFiles.Json)!;
        var start = DateOnly.ParseExact(Environment.GetEnvironmentVariable(StartDateVariable)
            ?? throw new InvalidOperationException("Explicit later T16 start date is required by strict pre-frozen qualification."), "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var end = DateOnly.ParseExact(Environment.GetEnvironmentVariable(EndDateVariable)
            ?? throw new InvalidOperationException("Explicit T16 exclusive end date required."), "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var training = new RlTrainingJob(direction.Symbol, start, end, costs, SmokeOnly: smoke) { FrozenDirectionModelId = direction.ModelId };
        var progress = new Progress<RlTrainingProgress>(p => File.AppendAllText(Path.Combine(root, "progress.log"),
            $"{DateTimeOffset.UtcNow:O} {p.State} {p.CompletedSteps} {p.Message}{Environment.NewLine}"));
        var service = new RlTrainingService(registry, new TrainingJobGate(Path.Combine(root, "job-locks")), dataRoot: root);
        var result = await service.RunAsync(training, progress);
        Assert.Null(registry.RlActiveId); Assert.Null(registry.RlContinuousActiveId);
        using var metrics = JsonDocument.Parse(await File.ReadAllTextAsync(result.EvaluationPath));
        Assert.True(metrics.RootElement.GetProperty("parity_verified").GetBoolean());
        Assert.Equal(RlContinuousContract.ContractHash, metrics.RootElement.GetProperty("contract_hash").GetString());
        Assert.Equal(smoke ? 1 : 2, registry.ListRl().Count);
        Assert.Equal(ContentHash.OfFile(Path.Combine(directionDirectory, RlFiles.Model + RlFiles.ScalerSuffix)),
            ContentHash.OfFile(Path.Combine(result.Directory, RlFiles.Model + RlFiles.ScalerSuffix)));
        if (smoke)
        {
            Assert.Null(result.ModelId);
            await Assert.ThrowsAsync<InvalidDataException>(() => registry.RegisterRlAsync(result.Directory));
        }
        else
        {
            Assert.NotNull(result.ModelId);
            var actor = registry.GetRlGeneration(result.ModelId!)!;
            Assert.Equal(RlContinuousContract.Target, actor.Target); Assert.Equal(direction.ModelId, actor.FrozenDirectionModelId);
            Assert.Equal(RlContract.EvaluationEpisodeCount, metrics.RootElement.GetProperty("oos_episodes").GetArrayLength());
            Assert.Equal(RlContract.CandidateSeeds.Length * 2, metrics.RootElement.GetProperty("candidate_evaluations").GetArrayLength());
            var source = RlSourceSnapshot.Load(result.Directory, RlTrainingValidation.FreezeLimits(continuous: true));
            foreach (string policy in new[] { RlContinuousContract.Target, RlContinuousContract.PairedBaselineId })
            {
                var replay = await new QValueService(registry).ReplayAsync(new(result.ModelId!, result.Directory, source.Catalog.Validation[0].Id, 0, policy));
                Assert.NotNull(replay.Continuous); Assert.Equal(direction.ModelId, replay.Continuous!.DirectionModelId);
                Assert.Equal(RlContract.ActionCount, replay.QValues.Length);
            }
            Assert.True(await registry.ActivateRlAsync(direction.ModelId));
            Assert.True(await registry.ActivateRlAsync(actor.ModelId));
            Assert.Equal(direction.ModelId, registry.RlActiveId); Assert.Equal(actor.ModelId, registry.RlContinuousActiveId);
            using var retained = registry.AcquireRl(direction.ModelId); Assert.NotNull(retained);
        }
        string path = Path.Combine(Path.GetTempPath(), $"sa_T16_R01_{(smoke ? "smoke" : "full")}_{result.RunId:N}.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(result, RlFiles.Json));
    }

    [ContinuousQualification] public void QualifiedInferenceMeasurement()
    {
        string directory = Environment.GetEnvironmentVariable(ActorDirectoryVariable) ?? throw new InvalidOperationException("Qualified actor directory required.");
        using var actor = new RlQModel(Path.Combine(directory, RlFiles.Model), continuous: true);
        using var parity = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, RlFiles.Parity)));
        var observation = parity.RootElement.GetProperty("observations")[0].Deserialize<float[]>()!;
        for (int i = 0; i < RlContract.InferenceWarmupCalls; i++) actor.Infer(observation);
        using var process = Process.GetCurrentProcess(); process.Refresh(); long beforePrivate = process.PrivateMemorySize64;
        long allocated = GC.GetAllocatedBytesForCurrentThread(), start = Stopwatch.GetTimestamp();
        float[]? last = null;
        for (int i = 0; i < RlContract.InferenceMeasuredCalls; i++) last = actor.Infer(observation);
        long elapsed = Stopwatch.GetTimestamp() - start, managed = GC.GetAllocatedBytesForCurrentThread() - allocated;
        process.Refresh();
        Assert.Equal(RlContinuousContract.ActionCount, last!.Length);
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "sa_T16_LocalData_inference_measurement_20261004.json"), JsonSerializer.Serialize(new
        {
            provider = "CPUExecutionProvider", runtime = Microsoft.ML.OnnxRuntime.OrtEnv.Instance().GetVersionString(),
            input_shape = new[] { 1, RlContract.ObservationLength }, output_shape = new[] { 1, RlContinuousContract.ActionCount },
            threads = RlContract.CpuThreads, warmup_calls = RlContract.InferenceWarmupCalls, measured_calls = RlContract.InferenceMeasuredCalls,
            managed_allocated_bytes = managed, process_private_before_bytes = beforePrivate, process_private_after_bytes = process.PrivateMemorySize64,
            native_measurement_limit = "Process private memory is a process-wide proxy, not isolated native allocator accounting.",
            measured_elapsed_ms = elapsed * 1000d / Stopwatch.Frequency, model_sha256 = ContentHash.OfFile(Path.Combine(directory, RlFiles.Model))
        }, RlFiles.Json));
    }
}
