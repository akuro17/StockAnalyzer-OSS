using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>Opt-in real local Data/Daily qualification; never substitutes a synthetic source or shorter full run.</summary>
public sealed class RlQualificationTests
{
    public const string EnabledVariable = "SA_RL_REAL_QUALIFICATION";
    public sealed class RealQualificationAttribute : FactAttribute
    {
        public RealQualificationAttribute()
        {
            if (Environment.GetEnvironmentVariable(EnabledVariable) != "1")
                Skip = "Opt-in existing local Data/Daily CPU DQN qualification; set SA_RL_REAL_QUALIFICATION=1 after the source specification is resolved.";
        }
    }

    [RealQualification]
    public async Task FullQualification()
    {
        var result = await RunAsync(smoke: false);
        Assert.NotNull(result.ModelId);
        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(result.EvaluationPath));
        Assert.True(report.RootElement.GetProperty("parity_verified").GetBoolean());
        Assert.Equal(RlContract.EvaluationEpisodeCount, report.RootElement.GetProperty("oos_episodes").GetArrayLength());
        Assert.Equal(RlContract.CandidateSeeds.Length * RlContract.TrainingTransitionsPerSeed / RlContract.ValidationCheckpointInterval,
            report.RootElement.GetProperty("candidate_evaluations").GetArrayLength());
    }

    [RealQualification]
    public async Task SmokeQualification()
    {
        var result = await RunAsync(smoke: true);
        Assert.Null(result.ModelId);
        Assert.True(result.SmokeOnly);
    }

    [RealQualification]
    public void QualifiedInferenceMeasurement()
    {
        string directory = Environment.GetEnvironmentVariable("SA_RL_QUALIFIED_DIRECTORY")
            ?? throw new InvalidOperationException("A completed real local-data qualification directory is required.");
        using var model = new RlQModel(Path.Combine(directory, RlFiles.Model));
        using var parity = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, RlFiles.Parity)));
        var observation = parity.RootElement.GetProperty("observations")[0].Deserialize<float[]>()!;
        for (int i = 0; i < RlContract.InferenceWarmupCalls; i++) model.Infer(observation);
        using var process = Process.GetCurrentProcess();
        process.Refresh(); long privateBefore = process.PrivateMemorySize64;
        long managedBefore = GC.GetAllocatedBytesForCurrentThread();
        long ticksBefore = Stopwatch.GetTimestamp();
        float[]? last = null;
        for (int i = 0; i < RlContract.InferenceMeasuredCalls; i++) last = model.Infer(observation);
        long ticks = Stopwatch.GetTimestamp() - ticksBefore;
        long managed = GC.GetAllocatedBytesForCurrentThread() - managedBefore;
        process.Refresh(); long privateAfter = process.PrivateMemorySize64;
        Assert.Equal(RlContract.ActionCount, last!.Length);
        Assert.All(last, q => Assert.True(float.IsFinite(q)));
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "sa_T15_LocalData_inference_measurement_20261004.json"), JsonSerializer.Serialize(new
        {
            model_path = Path.Combine(directory, RlFiles.Model),
            model_sha256 = StockAnalyzer.Core.Common.ContentHash.OfFile(Path.Combine(directory, RlFiles.Model)),
            observation_sha256 = StockAnalyzer.Core.Common.ContentHash.OfFile(Path.Combine(directory, RlFiles.Parity)),
            input_shape = new[] { 1, RlContract.ObservationLength }, output_shape = new[] { 1, RlContract.ActionCount },
            provider = "CPUExecutionProvider", intra_op_threads = RlContract.CpuThreads, inter_op_threads = RlContract.CpuThreads,
            runtime = Microsoft.ML.OnnxRuntime.OrtEnv.Instance().GetVersionString(),
            warmup_calls = RlContract.InferenceWarmupCalls, measured_calls = RlContract.InferenceMeasuredCalls,
            managed_allocated_bytes = managed, managed_bytes_per_call = managed / (decimal)RlContract.InferenceMeasuredCalls,
            process_private_before_bytes = privateBefore, process_private_after_bytes = privateAfter,
            process_private_delta_bytes = privateAfter - privateBefore,
            native_measurement_limit = "Process private memory is a process-wide proxy, not isolated native allocator accounting. No zero-native-allocation assertion is made.",
            measured_elapsed_ms = ticks * 1000d / Stopwatch.Frequency,
            render_contract = "Input wrappers, output arrays and ORT boundary allocations are outside rendering; cached draw allocation is verified separately."
        }, RlFiles.Json));
    }

    private static async Task<RlTrainingResult> RunAsync(bool smoke)
    {
        // Explicit real validation fixture only; application inputs remain user-selected.
        string root = Path.Combine(Path.GetTempPath(), "sa_rl_qualification_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var registry = new ModelGenerationRegistry(Path.Combine(root, "Generations"));
        var service = new RlTrainingService(registry, new TrainingJobGate(Path.Combine(root, "job-locks")), dataRoot: root);
        var job = new RlTrainingJob("AAPL", new DateOnly(1990, 1, 1), DateOnly.FromDateTime(DateTime.UtcNow),
            new BacktestConfiguration { InitialCapital = BacktestDefaults.InitialCapital,
                SizingModel = BacktestDefaults.SizingModel, SizingParameter = BacktestDefaults.SizingParameter,
                LiquidationPenaltyRatio = BacktestDefaults.LiquidationPenaltyRatio },
            SmokeOnly: smoke);
        var progress = new Progress<RlTrainingProgress>(p => File.AppendAllText(Path.Combine(root, "progress.log"),
            $"{DateTimeOffset.UtcNow:O} {p.State} {p.CompletedSteps} {p.Message}{Environment.NewLine}"));
        var result = await service.RunAsync(job, progress);
        Assert.Equal(smoke ? 0 : 1, registry.ListRl().Count);
        Assert.Null(registry.RlActiveId);
        if (!smoke)
        {
            using var lease = registry.AcquireRl(result.ModelId!);
            Assert.NotNull(lease);
            var source = RlSourceSnapshot.Load(Path.GetDirectoryName(lease!.ModelPath)!, RlTrainingValidation.FreezeLimits());
            var q = await new QValueService(registry).ReplayAsync(new(result.ModelId!, source.Directory,
                source.Catalog.Oos[0].Id, 0, RlFiles.Target));
            Assert.Equal(RlContract.ActionCount, q.QValues.Length);
            Assert.All(q.QValues, value => Assert.True(float.IsFinite(value)));
        }
        string evidence = Path.Combine(Path.GetTempPath(), smoke ? "sa_T15_LocalData_smoke_qualification_result_20261004.json" : "sa_T15_LocalData_full_qualification_result_20261004.json");
        await File.WriteAllTextAsync(evidence, JsonSerializer.Serialize(result, RlFiles.Json));
        return result;
    }
}
