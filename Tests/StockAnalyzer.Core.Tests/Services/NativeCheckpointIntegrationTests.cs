using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using StockAnalyzer.Core.Services;
using Xunit;
using Xunit.Sdk;

namespace StockAnalyzer.Core.Tests.Services;

public sealed class NativeCheckpointIntegrationTests
{
    [Fact]
    public async Task RealTransferLineage_RegistersVerifiedParentAndRejectsMismatchedEvidence()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "StockAnalyzer.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var python = Path.Combine(root.FullName, "StockAnalyzer.Python", ".venv",
            OperatingSystem.IsWindows() ? "Scripts/python.exe" : "bin/python");
        if (!File.Exists(python)) throw SkipException.ForSkip("The training Python environment is not installed.");
        var directory = Path.Combine(Path.GetTempPath(), "sa_t08_registry_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            async Task<string> Train(string? parentId = null, string? parentHash = null)
            {
                var info = new ProcessStartInfo(python) { WorkingDirectory = root.FullName, CreateNoWindow = true };
                info.ArgumentList.Add(Path.Combine(root.FullName, "Tests", "test_native_checkpoints.py"));
                info.ArgumentList.Add("--registry-bundle");
                info.ArgumentList.Add(directory);
                if (parentId is not null)
                {
                    info.ArgumentList.Add("--parent-model-id"); info.ArgumentList.Add(parentId);
                    info.ArgumentList.Add("--parent-model-hash"); info.ArgumentList.Add(parentHash!);
                }
                info.Environment["OMP_NUM_THREADS"] = "1";
                var output = await TrainingPythonTestRunner.RunAsync(info, TimeSpan.FromMinutes(5));
                return output.Split('\n').Single(line => line.StartsWith("REGISTRY_MODEL:", StringComparison.Ordinal))
                    ["REGISTRY_MODEL:".Length..].Trim();
            }
            using var registry = new ModelGenerationRegistry(Path.Combine(directory, "registry"));
            var parent = await registry.RegisterAsync(await Train());
            Assert.Equal("fresh", parent.TrainingLineage!.Mode);
            var childPath = await Train(parent.ModelId, parent.ModelHash);
            var child = await registry.RegisterAsync(childPath);
            Assert.Equal(parent.ModelId, child.TrainingLineage!.ParentModelId);
            Assert.Equal(parent.ModelHash, child.TrainingLineage.ParentModelSha256);
            using var lease = registry.Acquire(child.ModelId);
            Assert.NotNull(lease);
            var metricsPath = childPath + ".metrics.json";
            var metrics = JsonNode.Parse(File.ReadAllText(metricsPath))!;
            metrics["training_lineage"]!["parent_model_id"] = new string('b', 64);
            File.WriteAllText(metricsPath, metrics.ToJsonString());
            await Assert.ThrowsAsync<InvalidDataException>(() => registry.RegisterAsync(childPath));
            Assert.Equal(2, registry.List().Count);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task NativeCheckpoints_ExactResumeTransferAndIndependentFolds()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "StockAnalyzer.sln")))
            root = root.Parent;
        Assert.NotNull(root);
        var python = Path.Combine(root.FullName, "StockAnalyzer.Python", ".venv",
            OperatingSystem.IsWindows() ? "Scripts/python.exe" : "bin/python");
        if (!File.Exists(python)) throw SkipException.ForSkip("The training Python environment is not installed.");
        var info = new ProcessStartInfo(python) { WorkingDirectory = root.FullName, CreateNoWindow = true };
        info.ArgumentList.Add(Path.Combine(root.FullName, "Tests", "test_native_checkpoints.py"));
        info.Environment["OMP_NUM_THREADS"] = "1";
        var output = await TrainingPythonTestRunner.RunAsync(info, TimeSpan.FromMinutes(5));
        Assert.Contains("T08 native checkpoint acceptance: PASS", output);
    }
}
