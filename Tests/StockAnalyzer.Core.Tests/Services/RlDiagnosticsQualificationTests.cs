#nullable enable
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

[Collection("ChildProcess")]
public sealed class RlDiagnosticsQualificationTests
{
    public const string EnabledVariable = "SA_RL_R02_QUALIFICATION";
    public const string OutputVariable = "SA_RL_R02_OUTPUT_DIRECTORY";
    public sealed class DiagnosticsQualificationAttribute : FactAttribute
    {
        public DiagnosticsQualificationAttribute()
        {
            if (Environment.GetEnvironmentVariable(EnabledVariable) != "1") Skip = "Opt-in owned R02 stored-price training and numerical fault qualification.";
        }
    }

    [DiagnosticsQualification] public async Task ActualSmokeDiagnostics() => await Qualify(smoke: true);
    [DiagnosticsQualification] public async Task ActualFullDiagnostics() => await Qualify(smoke: false);

    private static string Output() => Environment.GetEnvironmentVariable(OutputVariable)
        ?? throw new InvalidOperationException("Explicit R02 output directory required.");
    private static string DirectionDirectory() => Environment.GetEnvironmentVariable(RlContinuousQualificationTests.DirectionDirectoryVariable)
        ?? throw new InvalidOperationException("Explicit immutable qualified direction required.");
    private static RlTrainingJob Job(string directionId, bool smoke)
    {
        using var captured = JsonDocument.Parse(File.ReadAllText(Path.Combine(DirectionDirectory(), RlFiles.Job)));
        var costs = captured.RootElement.GetProperty("costs").Deserialize<BacktestConfiguration>(RlFiles.Json)!;
        DateOnly Date(string variable) => DateOnly.ParseExact(Environment.GetEnvironmentVariable(variable)
            ?? throw new InvalidOperationException("Explicit later interval required."), "yyyy-MM-dd", CultureInfo.InvariantCulture);
        return new("AAPL", Date(RlContinuousQualificationTests.StartDateVariable), Date(RlContinuousQualificationTests.EndDateVariable), costs,
            SmokeOnly: smoke) { FrozenDirectionModelId = directionId };
    }

    private static async Task Qualify(bool smoke)
    {
        string root = Path.Combine(Output(), "owned_" + (smoke ? "smoke_" : "full_") + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var registry = new ModelGenerationRegistry(Path.Combine(root, "Generations"));
        var direction = await registry.RegisterRlAsync(DirectionDirectory());
        var service = new RlTrainingService(registry, new TrainingJobGate(Path.Combine(root, "job-locks")), dataRoot: root);
        var result = await service.RunAsync(Job(direction.ModelId, smoke));
        Assert.Null(registry.RlActiveId); Assert.Null(registry.RlContinuousActiveId);
        Assert.Equal(smoke ? 1 : 2, registry.ListRl().Count);
        using var metrics = JsonDocument.Parse(await File.ReadAllTextAsync(result.EvaluationPath));
        var diagnostics = metrics.RootElement.GetProperty("diagnostics");
        Assert.Equal(smoke ? 2 : 60, diagnostics.GetProperty("updates").GetArrayLength());
        using var captured = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(result.Directory, RlFiles.Job)));
        using var actor = new RlQModel(result.ModelPath, continuous: true);
        RlDiagnosticsValidator.Validate(result.Directory, captured.RootElement, metrics.RootElement, actor.Metadata);
        Assert.Equal((smoke ? 1 : RlContract.CandidateSeeds.Length) * (smoke ? RlContract.SmokeTrainingTransitions : RlContinuousContract.TrainingTransitionsPerSeed),
            diagnostics.GetProperty("actions").EnumerateArray().Where(a => a.GetProperty("role").GetString() == "train")
                .Sum(a => a.GetProperty("all_actions").GetProperty("denominator").GetInt32()));
        // Every parity proposal can be independently recounted from the actual raw actor output transcript.
        using var parity = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(result.Directory, RlFiles.Parity)));
        var counter = new RlActionDiagnosticCounter();
        var entryCounter = new RlActionDiagnosticCounter();
        foreach (var episode in parity.RootElement.GetProperty("paired_episodes").EnumerateArray())
            foreach (var step in episode.GetProperty("steps").EnumerateArray())
            {
                var proposal = RlContinuousContract.Map(step.GetProperty("actions")[0].GetSingle(), step.GetProperty("actions")[1].GetSingle());
                counter.Add(proposal);
                var observation = step.GetProperty("observation");
                int offset = RlContract.WindowLength * RlContract.MarketChannelCount;
                if (step.GetProperty("direction").GetString() == RlAction.Buy.ToString()
                    && observation[offset + RlContract.AccountFieldNames.IndexOf("position_long")].GetSingle() == 0f
                    && observation[offset + RlContract.AccountFieldNames.IndexOf("pending_entry")].GetSingle() == 0f
                    && observation[offset + RlContract.AccountFieldNames.IndexOf("deferred_market_exit")].GetSingle() == 0f)
                    entryCounter.Add(proposal);
            }
        var saved = diagnostics.GetProperty("actions").EnumerateArray().Single(a => a.GetProperty("role").GetString() == "parity")
            .GetProperty("all_actions").Deserialize<RlActionCounts>(RlFiles.Json)!;
        Assert.Equal(JsonSerializer.Serialize(counter.Snapshot(), RlFiles.Json), JsonSerializer.Serialize(saved, RlFiles.Json));
        var savedEntry = diagnostics.GetProperty("actions").EnumerateArray().Single(a => a.GetProperty("role").GetString() == "parity")
            .GetProperty("entry_actions").Deserialize<RlActionCounts>(RlFiles.Json)!;
        Assert.Equal(JsonSerializer.Serialize(entryCounter.Snapshot(), RlFiles.Json), JsonSerializer.Serialize(savedEntry, RlFiles.Json));
        if (!smoke)
        {
            using var reload = new ModelGenerationRegistry(Path.Combine(root, "Generations"));
            Assert.NotNull(reload.GetRlGeneration(result.ModelId!));
            // Rehashed, plausible-looking but inconsistent evidence cannot register.
            string tampered = Path.Combine(root, "tampered"); Directory.CreateDirectory(tampered);
            foreach (string file in Directory.EnumerateFiles(result.Directory, "*", SearchOption.AllDirectories))
            {
                string target = Path.Combine(tampered, Path.GetRelativePath(result.Directory, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target);
            }
            var altered = JsonNode.Parse(await File.ReadAllTextAsync(result.EvaluationPath))!;
            altered["diagnostics"]!["updates"]![0]!["optimizer_steps"] = 0;
            await File.WriteAllTextAsync(Path.Combine(tampered, RlFiles.Model + RlFiles.EvaluationSuffix), altered.ToJsonString());
            await Assert.ThrowsAsync<InvalidDataException>(() => registry.RegisterRlAsync(tampered));
            Assert.Equal(2, registry.ListRl().Count);
        }
        else await Assert.ThrowsAsync<InvalidDataException>(() => registry.RegisterRlAsync(result.Directory));
        await File.WriteAllTextAsync(Path.Combine(Output(), (smoke ? "smoke" : "full") + "_qualification_result.json"), JsonSerializer.Serialize(result, RlFiles.Json));
    }

    [DiagnosticsQualification]
    public async Task NumericalFailuresDoNotRegisterOrChangeActivePointers()
    {
        string root = Path.Combine(Output(), "owned_faults_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        using var registry = new ModelGenerationRegistry(Path.Combine(root, "Generations"));
        var direction = await registry.RegisterRlAsync(DirectionDirectory());
        Assert.True(await registry.ActivateRlAsync(direction.ModelId));
        string? earlierActor = Environment.GetEnvironmentVariable(RlContinuousQualificationTests.ActorDirectoryVariable);
        if (earlierActor is not null)
        {
            var prior = await registry.RegisterRlAsync(earlierActor); Assert.True(await registry.ActivateRlAsync(prior.ModelId));
        }
        string? activeActor = registry.RlContinuousActiveId;
        int generations = registry.ListRl().Count;
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "StockAnalyzer.sln"))) repo = repo.Parent;
        string originalPythonPath = Environment.GetEnvironmentVariable("PYTHONPATH") ?? "";
        string? originalFault = Environment.GetEnvironmentVariable("SA_RL_R02_TEST_FAULT");
        string? originalOutput = Environment.GetEnvironmentVariable("SA_RL_R02_TEST_FAULT_OUTPUT");
        var results = new System.Collections.Generic.List<object>();
        try
        {
            foreach (string fault in new[] { "loss", "ratio", "log_std", "gradient" })
            {
                string faultRoot = Path.Combine(root, fault); Directory.CreateDirectory(faultRoot);
                File.Copy(Path.Combine(repo!.FullName, "Tests", "StockAnalyzer.Core.Tests", "Assets", "rl_guard_fault_sitecustomize.py"), Path.Combine(faultRoot, "sitecustomize.py"));
                Environment.SetEnvironmentVariable("PYTHONPATH", faultRoot + Path.PathSeparator + originalPythonPath);
                Environment.SetEnvironmentVariable("SA_RL_R02_TEST_FAULT", fault);
                Environment.SetEnvironmentVariable("SA_RL_R02_TEST_FAULT_OUTPUT", faultRoot);
                var service = new RlTrainingService(registry, new TrainingJobGate(Path.Combine(faultRoot, "job-locks")), dataRoot: faultRoot);
                await Assert.ThrowsAsync<InvalidOperationException>(() => service.RunAsync(Job(direction.ModelId, smoke: true)));
                Assert.True(File.Exists(Path.Combine(faultRoot, "injection_started.txt")));
                Assert.False(File.Exists(Path.Combine(faultRoot, "optimizer_step_called.txt")));
                string artifact = Directory.GetDirectories(Path.Combine(faultRoot, RlFiles.TrainingFolder, RlFiles.RlFolder)).Single();
                using var failed = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(artifact, "failure.json")));
                Assert.Equal("Failed", failed.RootElement.GetProperty("state").GetString());
                Assert.Contains("PPO", await File.ReadAllTextAsync(Path.Combine(artifact, "train_rl_ppo.stderr.log")));
                Assert.False(File.Exists(Path.Combine(artifact, RlFiles.Model)));
                Assert.True(File.Exists(Path.Combine(artifact, RlDiagnosticsContract.FailureEvidenceFile)));
                Assert.Equal(generations, registry.ListRl().Count);
                Assert.Equal(direction.ModelId, registry.RlActiveId); Assert.Equal(activeActor, registry.RlContinuousActiveId);
                results.Add(new { fault, artifact, state = "Failed", optimizer_steps = 0, registered = false, active_unchanged = true });
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("PYTHONPATH", originalPythonPath.Length == 0 ? null : originalPythonPath);
            Environment.SetEnvironmentVariable("SA_RL_R02_TEST_FAULT", originalFault);
            Environment.SetEnvironmentVariable("SA_RL_R02_TEST_FAULT_OUTPUT", originalOutput);
        }
        await File.WriteAllTextAsync(Path.Combine(Output(), "numerical_failure_qualification.json"), JsonSerializer.Serialize(results, RlFiles.Json));
    }
}
