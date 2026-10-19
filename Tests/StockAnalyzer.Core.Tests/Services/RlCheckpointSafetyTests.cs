#nullable enable
using System;
using System.IO;
using System.IO.Compression;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

[Collection("ChildProcess")]
public sealed class RlCheckpointSafetyTests
{
    [Fact] public async Task AcceptedAttemptRecordsPreparationFailureBeforeDirectionQualification()
    {
        string root = Path.Combine(Path.GetTempPath(), "sa_m01_preparation_" + Guid.NewGuid().ToString("N"));
        try
        {
            using var registry = new ModelGenerationRegistry(Path.Combine(root, "Generations")) { ArtifactFreeSpace = _ => long.MaxValue };
            registry.ConfigureRlArtifacts(root, new RlArtifactSettings(Path.Combine(root, "Config", RlArtifactSettings.FileName)));
            await registry.SaveRlArtifactPolicyAsync(new(NonactiveRetentionDays: 0, NonactiveKeepCount: 0));
            var training = new RlTrainingService(registry, new TrainingJobGate(Path.Combine(root, "Locks")), dataRoot: root);
            var job = new RlTrainingJob("AAPL", new DateOnly(2004, 1, 1), new DateOnly(2020, 1, 1), new BacktestConfiguration
                { InitialCapital = BacktestDefaults.InitialCapital, SizingModel = PositionSizingModel.FixedQuantity, SizingParameter = RlContract.FixedQuantity }, SmokeOnly: true)
                { FrozenDirectionModelId = new string('0', ContentHash.HexLength) };
            await Assert.ThrowsAsync<InvalidOperationException>(() => training.RunResumableAsync(job, CancellationToken.None));
            var run = Assert.Single(Directory.GetDirectories(Path.Combine(root, RlFiles.TrainingFolder, RlFiles.RlFolder)));
            Assert.True(RlCheckpointStore.ReadBudget(run).ActiveSeconds > 0);
            using var descriptor = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(run, ModelGenerationRegistry.ArtifactRunFile)));
            Assert.Equal((int)RlArtifactRunState.Failed, descriptor.RootElement.GetProperty("state").GetInt32());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [ChildProcessFact] public async Task DependencyIdentityRejectsChangedRealPayloadWithUnchangedDistributionMetadata()
    {
        string pythonRoot = Directory.GetParent(Path.GetDirectoryName(PythonScriptLocator.Resolve(RlFiles.DependenciesScript))!)!.FullName;
        var start = new ProcessStartInfo(Path.Combine(pythonRoot, RlFiles.VirtualEnvironment, OperatingSystem.IsWindows() ? "Scripts/python.exe" : "bin/python"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(Directory.GetParent(pythonRoot)!.FullName, "Tests", "StockAnalyzer.Core.Tests", "Assets", "check_rl_dependency_payload.py"));
        start.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(); Assert.True(process.ExitCode == 0, await output + await error);
    }

    [Theory]
    [InlineData(RlFiles.Model)]
    [InlineData(RlFiles.Native)]
    [InlineData(RlFiles.Parity)]
    public void WorkerCompleteReviewRejectsDamagedRequiredExport(string exportName)
    {
        string root = Path.Combine(Path.GetTempPath(), "sa_m01_export_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        Guid run = Guid.NewGuid();
        try
        {
            foreach (string name in new[] { RlFiles.Dependencies, RlFiles.SourceManifest, RlFiles.Bars, RlFiles.Actions, RlFiles.RawParquet,
                RlFiles.MetadataParquet, RlFiles.Catalog, RlFiles.Model + RlFiles.ScalerSuffix, RlContinuousContract.DirectionModel,
                RlContinuousContract.DirectionScaler, RlContinuousContract.DirectionManifest, RlTemporalContract.DirectionEvidenceFile })
            {
                string path = Path.Combine(root, name); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "{}");
            }
            RlCheckpointStore.Save(Path.Combine(root, RlFiles.Job), new { run_id = run.ToString("N"), continuous_defaults = new { }, direction = new { model_id = new string('0', ContentHash.HexLength) } });
            foreach (string name in new[] { RlFiles.Model, RlFiles.Native, RlFiles.Parity }) File.WriteAllText(Path.Combine(root, name), "owned export fixture");
            string environmentRoot = Path.Combine(root, "environment"); Directory.CreateDirectory(environmentRoot);
            string executionFile = Path.Combine(environmentRoot, "module.py"); File.WriteAllText(executionFile, "value = 1");
            RlCheckpointStore.Save(Path.Combine(root, RlFiles.Dependencies), new
            {
                execution_roots = new[] { environmentRoot },
                execution_files = new System.Collections.Generic.Dictionary<string, string> { [executionFile] = ContentHash.OfFile(executionFile) },
                execution_links = new System.Collections.Generic.Dictionary<string, string>(),
            });
            string id;
            using (var owner = new RlCheckpointStore(root, run, _ => true, action => action(), () => false))
            {
                var pending = JsonSerializer.SerializeToElement(owner.Prepare(new { }, RlContract.CandidateSeeds[0], RlContinuousContract.RolloutSteps, "worker_complete"), RlFiles.Json);
                id = pending.GetProperty("id").GetString()!;
                using (var archive = ZipFile.Open(Path.Combine(pending.GetProperty("directory").GetString()!, RlCheckpointContract.PythonState), ZipArchiveMode.Create))
                { using var data = new StreamWriter(archive.CreateEntry("archive/data.pkl").Open()); data.Write("tensor-only fixture"); }
                owner.Commit(id);
            }
            _ = RlCheckpointStore.Verify(root, id);
            File.Delete(Path.Combine(root, exportName)); // Mutable run output is not a committed prerequisite anymore.
            using (var owner = new RlCheckpointStore(root, run, _ => true, action => action(), () => false, id)) owner.RestoreExports();
            Assert.Equal("owned export fixture", File.ReadAllText(Path.Combine(root, exportName)));
            File.WriteAllText(executionFile, "value = 2");
            Assert.Throws<InvalidDataException>(() => RlCheckpointStore.Verify(root, id));
            File.WriteAllText(executionFile, "value = 1");
            var bundle = Path.Combine(root, RlCheckpointContract.Folder, id, RlCheckpointContract.ExportFolder, exportName);
            File.WriteAllText(bundle, "corrupted export");
            Assert.Throws<InvalidDataException>(() => RlCheckpointStore.Verify(root, id));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact] public async Task ExistingSavedArtifactPolicyGetsAdoptedCheckpointDefaultAndRejectsZero()
    {
        string root = Path.Combine(Path.GetTempPath(), "sa_r09_policy_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, RlArtifactSettings.FileName);
            await File.WriteAllTextAsync(path, "{\"schemaVersion\":1,\"policy\":{\"quotaGiB\":10,\"minimumFreeGiB\":5,\"failedRetentionDays\":7,\"nonactiveRetentionDays\":3,\"nonactiveKeepCount\":2}}");
            var settings = new RlArtifactSettings(path);
            Assert.Equal(RlCheckpointContract.DefaultKeepCount, settings.Snapshot.CheckpointKeepCount);
            Assert.Throws<ArgumentException>(() => (settings.Snapshot with { CheckpointKeepCount = 0 }).Validate());
            await settings.SaveAsync(settings.Snapshot with { CheckpointKeepCount = 1 });
            Assert.Equal(1, settings.Snapshot.CheckpointKeepCount);
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact] public async Task DurableWorkJournalNeverRollsBackFailedAttemptChargesAndExcludesTimeBetweenOwners()
    {
        string root = Path.Combine(Path.GetTempPath(), "sa_r09_budget_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            Guid run = Guid.NewGuid();
            using (var owner = new RlCheckpointStore(root, run, _ => true, action => action(), () => false))
            {
                owner.Charge(30, 4); owner.Charge(35, 5); owner.Charge(31, 4);
                await Task.Delay(TimeSpan.FromMilliseconds(60)); // Exit must charge work after the last IPC charge.
            }
            var previous = RlCheckpointStore.ReadBudget(root);
            Assert.Equal(35, previous.Transitions); Assert.Equal(5, previous.Starts); Assert.True(previous.ActiveSeconds >= .05m);
            await Task.Delay(TimeSpan.FromMilliseconds(80));
            Assert.Equal(previous.ActiveSeconds, RlCheckpointStore.ReadBudget(root).ActiveSeconds);
            using (var next = new RlCheckpointStore(root, run, _ => true, action => action(), () => false))
            {
                Assert.Equal(35, next.Used.Transitions); Assert.Equal(5, next.Used.Starts); next.Charge(40, 6);
                await Task.Delay(TimeSpan.FromMilliseconds(60));
            }
            var latest = RlCheckpointStore.ReadBudget(root); Assert.Equal(40, latest.Transitions); Assert.Equal(6, latest.Starts);
            Assert.True(latest.ActiveSeconds >= previous.ActiveSeconds + .05m);
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact] public void IncompleteJournalRefusesResumeInsteadOfSilentlyResettingBudget()
    {
        string root = Path.Combine(Path.GetTempPath(), "sa_r09_partial_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            using (var owner = new RlCheckpointStore(root, Guid.NewGuid(), _ => true, action => action(), () => false)) owner.Charge(12, 2);
            using (var file = new FileStream(Path.Combine(root, RlCheckpointContract.Journal), FileMode.Append, FileAccess.Write)) file.WriteByte(0);
            Assert.Throws<InvalidDataException>(() => RlCheckpointStore.ReadBudget(root));
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact] public void MissingJournalCannotOpenResumedOwnerOrSilentlyRecreateItsBudget()
    {
        string root = Path.Combine(Path.GetTempPath(), "sa_r09_missing_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            Guid run = Guid.NewGuid();
            using (var owner = new RlCheckpointStore(root, run, _ => true, action => action(), () => false)) owner.Charge(35, 5);
            string journal = Path.Combine(root, RlCheckpointContract.Journal); File.Delete(journal);
            Assert.Throws<InvalidDataException>(() => new RlCheckpointStore(root, run, _ => true, action => action(), () => false, Guid.NewGuid().ToString("N")));
            Assert.False(File.Exists(journal));
        }
        finally { Directory.Delete(root, true); }
    }
}
