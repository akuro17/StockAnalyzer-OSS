using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Execution;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Models.Training;

public sealed class TrainingHyperparameterAllowlistTests
{
    [Fact]
    public void ValidateStrictAllowlist_AllowsRegisteredApplicableHyperparameters()
    {
        var res = TrainingHyperparameterCatalog.ValidateStrictAllowlist(
            TrainingFramework.LightGBM,
            "gbdt",
            new Dictionary<string, string> { ["n-estimators"] = "200", ["learning-rate"] = "0.05" });

        Assert.True(res.IsValid);
        Assert.Null(res.OffendingKey);
        Assert.Contains("n-estimators", res.AllowedKeys);
        Assert.DoesNotContain("epochs", res.AllowedKeys);
    }

    [Fact]
    public void ValidateStrictAllowlist_RejectsUnknownWrongFrameworkWrongArchitectureAndDivisibility()
    {
        var unknown = TrainingHyperparameterCatalog.ValidateStrictAllowlist(
            TrainingFramework.LightGBM, "gbdt", new Dictionary<string, string> { ["opset"] = "17" });
        Assert.False(unknown.IsValid);
        Assert.Equal("opset", unknown.OffendingKey);

        var wrongFramework = TrainingHyperparameterCatalog.ValidateStrictAllowlist(
            TrainingFramework.LightGBM, "gbdt", new Dictionary<string, string> { ["epochs"] = "10" });
        Assert.False(wrongFramework.IsValid);
        Assert.Equal("epochs", wrongFramework.OffendingKey);

        var wrongArch = TrainingHyperparameterCatalog.ValidateStrictAllowlist(
            TrainingFramework.PyTorch, "lstm", new Dictionary<string, string> { ["heads"] = "4" });
        Assert.False(wrongArch.IsValid);
        Assert.Equal("heads", wrongArch.OffendingKey);
        Assert.Contains("not supported", wrongArch.Message, StringComparison.Ordinal);

        var badHeads = TrainingHyperparameterCatalog.ValidateStrictAllowlist(
            TrainingFramework.PyTorch, AttentionContract.Architecture, new Dictionary<string, string> { ["hidden"] = "10", ["heads"] = "4" });
        Assert.False(badHeads.IsValid);
        Assert.Equal("heads", badHeads.OffendingKey);
        Assert.Contains("divisible by heads", badHeads.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SandboxedExecutionGateway_RejectsDisallowedHyperparameterDirectly()
    {
        var root = Path.Combine(Path.GetTempPath(), "sa-gw-allowlist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var gateway = new SandboxedExecutionGateway(
                new NoOpOrchestrator(),
                new MissingPythonResolver(),
                new SandboxedExecutionSettings(),
                logger: null,
                sandboxRoot: root,
                cancelGrace: TimeSpan.FromMilliseconds(100));

            var request = new EphemeralTrainingRequest
            {
                Symbols = new[] { "AAA" },
                Framework = TrainingFramework.LightGBM,
                Architecture = "gbdt",
                WindowSize = 10,
                Horizon = 1,
                Hyperparameters = new Dictionary<string, string> { ["unknown-hp"] = "1" },
            };

            var ex = await Assert.ThrowsAsync<ExecutionRequestInvalidException>(() =>
                gateway.StartTrainingAsync(request, CancellationToken.None));
            Assert.Equal(nameof(EphemeralTrainingRequest.Hyperparameters), ex.ParameterName);
            Assert.Contains("Allowed hyperparameters", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    private sealed class NoOpOrchestrator : ITrainingOrchestrator
    {
        public Task<TrainingRunResult> StartTrainingAsync(TrainingJobConfig config, IProgress<TrainingProgress>? progress = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class MissingPythonResolver : IPythonExecutableResolver
    {
        public bool TryResolve(out string pythonExe)
        {
            pythonExe = string.Empty;
            return false;
        }
    }
}
