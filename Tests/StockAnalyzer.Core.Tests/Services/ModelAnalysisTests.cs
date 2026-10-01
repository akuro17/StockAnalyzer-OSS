#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

public sealed class ModelAnalysisTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sa_t09_" + Guid.NewGuid().ToString("N"));
    private static string Fixture => Path.Combine("Assets", "analysis_fixture.onnx");
    private static ModelAnalysis ReadFixture()
    {
        using var session = new InferenceSession(Fixture);
        var document = JsonNode.Parse(File.ReadAllText(Fixture + ".analysis.json"))!;
        return ModelAnalysis.Load(Fixture, session.ModelMetadata.CustomMetadataMap,
            document["model_sha256"]!.GetValue<string>(), document["data_revision"]!.GetValue<string>());
    }

    [Fact]
    public void PythonFixture_ApprovedConstantsImmutableContractAndCausalWarmup()
    {
        var fixture = ReadFixture();
        Assert.Equal(ModelAnalysisContract.StandardizationDdof, fixture.Regime.StandardizationDdof);
        Assert.Equal(0, fixture.Regime.Std[1]);
        Assert.Equal("inner_validation", fixture.Importance.Population);
        Assert.Equal(-2.5, fixture.Importance.Values[0].Value);
        Assert.Equal(3, fixture.Periods.Length);
        var service = new ModelAnalysisService(null!, new MLDataProcessor());
        var snapshot = new ModelAnalysisSnapshot(new string('c', 64), fixture);
        var candles = Enumerable.Range(0, 21).Select(i => new CandleData(new DateTime(2020, 5, 1).AddDays(i),
            100m, 100m, 100m, 100m, 100)).ToArray();
        Assert.Null(service.Assign(snapshot, candles[..20], TimeframeType.Daily));
        var result = service.Assign(snapshot, candles, TimeframeType.Daily);
        Assert.Equal(1, result!.Id);
        Assert.Equal(candles[^1].Timestamp, result.Anchor);
        Assert.Null(service.Assign(snapshot, candles, TimeframeType.Weekly));
        candles[^1] = candles[^1] with { Close = 0m };
        Assert.Null(service.Assign(snapshot, candles, TimeframeType.Daily));
        candles[^1] = candles[^1] with { Close = 100m, Timestamp = candles[^2].Timestamp };
        Assert.Null(service.Assign(snapshot, candles, TimeframeType.Daily));
    }

    private string Source()
    {
        Directory.CreateDirectory(_directory);
        var source = Path.Combine(_directory, "analysis_fixture.onnx");
        foreach (var suffix in new[] { "", ".metrics.json", ".analysis.json" })
            File.Copy(Fixture + suffix, source + suffix, overwrite: true);
        return source;
    }

    [Fact]
    public async Task Registry_RequiresAndPreservesDeclaredAnalysis_AndDetectsTampering()
    {
        var source = Source();
        using var registry = new ModelGenerationRegistry(Path.Combine(_directory, "registry"));
        var model = await registry.RegisterAsync(source);
        Assert.Contains(model.RequiredSidecars, s => s.Kind == "analysis");
        var service = new ModelAnalysisService(registry, new MLDataProcessor());
        var first = await service.LoadAsync(model.ModelId);
        Assert.NotNull(first);
        Assert.Same(first, await service.LoadAsync(model.ModelId));
        using var lease = registry.Acquire(model.ModelId);
        File.AppendAllText(lease!.ModelPath + ".analysis.json", " ");
        await Assert.ThrowsAsync<InvalidDataException>(() => service.LoadAsync(model.ModelId));
        Assert.Equal(model.ModelId, registry.GetManifest(model.ModelId)!.ModelId);
    }

    [Theory]
    [InlineData("model")]
    [InlineData("data")]
    [InlineData("unit")]
    [InlineData("channel")]
    [InlineData("fit")]
    [InlineData("ddof")]
    [InlineData("null")]
    public async Task Registry_RejectsContradictoryAnalysis(string fault)
    {
        var source = Source();
        var doc = JsonNode.Parse(File.ReadAllText(source + ".analysis.json"))!;
        switch (fault)
        {
            case "model": doc["model_sha256"] = new string('0', 64); break;
            case "data": doc["data_revision"] = new string('0', 64); break;
            case "unit": doc["importance"]!["unit"] = "probability"; break;
            case "channel": doc["ordered_channels"]![0] = "lag0:wrong"; break;
            case "fit": doc["regime"]!["fit_periods"]![0]!["anchor_end"] = "2021-01-01"; break;
            case "ddof": doc["regime"]!["standardization_ddof"] = 1; break;
            case "null": doc["regime"] = null; break;
        }
        File.WriteAllText(source + ".analysis.json", doc.ToJsonString());
        using var registry = new ModelGenerationRegistry(Path.Combine(_directory, "registry"));
        await Assert.ThrowsAsync<InvalidDataException>(() => registry.RegisterAsync(source));
        Assert.Empty(registry.List());
    }

    [Fact]
    public async Task Registry_RejectsMissingDeclaredAnalysis()
    {
        var source = Source();
        File.Delete(source + ".analysis.json");
        using var registry = new ModelGenerationRegistry(Path.Combine(_directory, "registry"));
        await Assert.ThrowsAsync<FileNotFoundException>(() => registry.RegisterAsync(source));
    }

    [Theory]
    [InlineData("pytorch", false)]
    [InlineData("pytorch", true)]
    [InlineData("lightgbm", false)]
    [InlineData("tensorflow", false)]
    public async Task Python_RealTrainingExportOrtAndRecoverableAnalysisPublication(string framework, bool regression)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "StockAnalyzer.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var python = Path.Combine(root.FullName, "StockAnalyzer.Python", ".venv",
            OperatingSystem.IsWindows() ? "Scripts/python.exe" : "bin/python");
        var info = new ProcessStartInfo(python) { WorkingDirectory = root.FullName, CreateNoWindow = true };
        info.ArgumentList.Add(Path.Combine(root.FullName, "Tests", "test_model_analysis.py"));
        info.ArgumentList.Add("--framework"); info.ArgumentList.Add(framework);
        if (regression) info.ArgumentList.Add("--regression");
        info.Environment["OMP_NUM_THREADS"] = "1";
        info.Environment["TF_NUM_INTRAOP_THREADS"] = "1";
        info.Environment["TF_NUM_INTEROP_THREADS"] = "1";
        var output = await TrainingPythonTestRunner.RunAsync(info, TimeSpan.FromMinutes(5));
        Assert.Contains($"T09 real {framework}/{(regression ? "regression" : "classification")} train/export/ORT/publication: PASS", output);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
