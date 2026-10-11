#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Tests.Services;
using Xunit;
using Xunit.Sdk;

namespace StockAnalyzer.Core.Tests.Models.Training;

/// <summary>T24: the attention sidecar contract, checked against the values the Python exporter writes (<c>Tests/AttentionParity.json</c>).</summary>
[Collection("ChildProcess")]
public sealed class AttentionContractTests
{
    internal static JsonDocument Parity()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "StockAnalyzer.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return JsonDocument.Parse(File.ReadAllText(Path.Combine(directory!.FullName, "Tests", "AttentionParity.json")));
    }

    [Fact]
    public async Task ArchitectureName_IsTheOneTheTrainerOffers()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "StockAnalyzer.sln")))
            root = root.Parent;
        Assert.NotNull(root);
        var python = Path.Combine(root.FullName, "StockAnalyzer.Python", ".venv", OperatingSystem.IsWindows() ? "Scripts/python.exe" : "bin/python");
        if (!File.Exists(python))
            throw SkipException.ForSkip("The training Python virtual environment is not installed.");

        var start = new ProcessStartInfo(python) { WorkingDirectory = root.FullName };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("import sys; sys.path.insert(0, sys.argv[1]); import attention_export, train_pytorch; "
            + "print('arch=' + attention_export.ARCH); print('offered=' + str(attention_export.ARCH in train_pytorch.ARCHES))");
        start.ArgumentList.Add(Path.Combine(root.FullName, "StockAnalyzer.Python", "training"));
        var output = await TrainingPythonTestRunner.RunAsync(start, TimeSpan.FromMinutes(2));

        Assert.Contains("arch=" + AttentionContract.Architecture, output);
        Assert.Contains("offered=True", output);
    }

    [Theory]
    [InlineData("attn_lstm", true)]
    [InlineData(" ATTN_LSTM ", true)]
    [InlineData("lstm", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsAttentionArchitecture_FollowsTheTrainersNormalisation(string? name, bool expected) =>
        Assert.Equal(expected, AttentionContract.IsAttentionArchitecture(name));

    [Fact]
    public void JobConfig_AcceptsTheAttentionArchitecture_ButNotWithPretraining()
    {
        var job = new TrainingJobConfig { Symbols = new[] { "AAA" }, Architecture = AttentionContract.Architecture, WindowSize = 20, Horizon = 5 };
        job.Validate();
        var heads = job with { Hyperparameters = new Dictionary<string, string> { ["heads"] = "2" } };
        heads.Validate();

        var pretraining = job with { Pretraining = TrainingPretraining.MaskedReconstruction };
        Assert.Contains(AttentionContract.Architecture, Assert.Throws<InvalidOperationException>(pretraining.Validate).Message);
        (pretraining with { Architecture = "lstm" }).Validate(); // the same recipe stays valid for another architecture
    }

    [Fact]
    public void Constants_MatchPython()
    {
        using var parity = Parity();
        var contract = parity.RootElement.GetProperty("contract");
        Assert.Equal(contract.GetProperty("sidecar_suffix").GetString(), AttentionContract.SidecarSuffix);
        Assert.Equal(contract.GetProperty("metadata_prefix").GetString(), AttentionContract.MetadataPrefix);
        Assert.Equal(contract.GetProperty("schema_version").GetInt32(), AttentionContract.SchemaVersion);
        Assert.Equal(contract.GetProperty("layout").GetString(), AttentionContract.Layout);
        Assert.Equal(contract.GetProperty("mask").GetString(), AttentionContract.Mask);
        Assert.Equal(contract.GetProperty("unit").GetString(), AttentionContract.Unit);
        Assert.Equal(contract.GetProperty("input_name").GetString(), AttentionContract.InputName);
        Assert.Equal(contract.GetProperty("output_name").GetString(), AttentionContract.OutputName);
        Assert.Equal(contract.GetProperty("max_heads").GetInt32(), AttentionContract.MaxHeads);
        Assert.Equal((float)contract.GetProperty("row_sum_tolerance").GetDouble(), AttentionContract.RowSumTolerance);

        var keys = contract.GetProperty("keys");
        Assert.Equal(AttentionContract.BindingKeys.Length, keys.EnumerateObject().Count());
        Assert.Equal(keys.GetProperty("schema_version").GetString(), AttentionContract.SchemaVersionKey);
        Assert.Equal(keys.GetProperty("parent_sha256").GetString(), AttentionContract.ParentSha256Key);
        Assert.Equal(keys.GetProperty("window_size").GetString(), AttentionContract.WindowSizeKey);
        Assert.Equal(keys.GetProperty("heads").GetString(), AttentionContract.HeadsKey);
        Assert.Equal(keys.GetProperty("feature_contract_hash").GetString(), AttentionContract.FeatureContractHashKey);
        Assert.Equal(keys.GetProperty("layout").GetString(), AttentionContract.LayoutKey);
        Assert.Equal(keys.GetProperty("mask").GetString(), AttentionContract.MaskKey);
        Assert.Equal(keys.GetProperty("unit").GetString(), AttentionContract.UnitKey);
        Assert.All(AttentionContract.BindingKeys, key => Assert.StartsWith(AttentionContract.MetadataPrefix, key, StringComparison.Ordinal));
    }

    [Fact]
    public void Sidecar_IsNotAnExistingRegistrySidecar_AndTheLegendReusesTheTrainingPeriodTokens()
    {
        Assert.NotEqual(ModelExplanationContract.SidecarSuffix, AttentionContract.SidecarSuffix);
        Assert.Equal(ModelAnalysisContract.LegendInset, AttentionContract.LegendInset);
        Assert.Equal(ModelAnalysisContract.LegendSwatchSize, AttentionContract.LegendSwatchSize);
        Assert.Equal(ModelAnalysisContract.LegendGap, AttentionContract.LegendGap);
        Assert.True(AttentionContract.LegendRowOffset >= AttentionContract.LegendSwatchSize);
        Assert.Equal(GraphLimits.MaxWindow, AttentionContract.MaxWindow);
    }

    [Fact]
    public void Results_CarryASnapshotOnlyWhenReady()
    {
        Assert.Null(AttentionResult.Unsupported(AttentionContract.UnsupportedReason.NoSidecar).Snapshot);
        Assert.Null(AttentionResult.Failed("x").Snapshot);
        Assert.Equal(AttentionStatus.Unsupported, AttentionResult.Unsupported(AttentionContract.UnsupportedReason.Timeframe).Status);
        Assert.Equal(AttentionStatus.Failed, AttentionResult.Failed("x").Status);
    }
}
