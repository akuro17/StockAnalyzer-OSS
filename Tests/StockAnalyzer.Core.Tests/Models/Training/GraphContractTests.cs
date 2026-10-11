using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Training;
using Xunit;

namespace StockAnalyzer.Core.Tests.Models.Training;

/// <summary>T25: the graph-model contract constants, checked against the values the Python trainer writes (<c>Tests/GraphParity.json</c>).</summary>
public sealed class GraphContractTests
{
    internal static JsonDocument Parity()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "StockAnalyzer.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return JsonDocument.Parse(File.ReadAllText(Path.Combine(directory!.FullName, "Tests", "GraphParity.json")));
    }

    [Fact]
    public void Constants_MatchPython()
    {
        using var parity = Parity();
        var contract = parity.RootElement.GetProperty("contract");
        Assert.Equal(contract.GetProperty("contract_version").GetInt32(), GraphContract.ContractVersion);
        Assert.Equal(contract.GetProperty("metadata_prefix").GetString(), GraphContract.MetadataPrefix);
        Assert.Equal(contract.GetProperty("output_name").GetString(), GraphContract.OutputName);
        Assert.Equal(contract.GetProperty("class_count").GetInt32(), GraphContract.ClassCount);
        Assert.Equal(contract.GetProperty("class_order").GetString(), GraphContract.ClassOrder);
        Assert.Equal(contract.GetProperty("timeframe").GetString(), GraphContract.Timeframe);
        Assert.Equal(contract.GetProperty("min_corr_window").GetInt32(), GraphContract.MinCorrWindow);
        Assert.Equal(contract.GetProperty("kind").GetString(), GraphContract.ArtifactKind);
        Assert.Equal(contract.GetProperty("model_file").GetString(), GraphContract.ModelFileName);
        Assert.Equal(contract.GetProperty("document_file").GetString(), GraphContract.DocumentFileName);
        Assert.Equal(contract.GetProperty("document_schema_version").GetInt32(), GraphContract.DocumentSchemaVersion);

        var inputs = contract.GetProperty("input_names");
        Assert.Equal(new[] { GraphContract.InputNodeFeatures, GraphContract.InputAdjacency, GraphContract.InputNodeMask },
            new[] { inputs[0].GetString(), inputs[1].GetString(), inputs[2].GetString() });

        var keys = contract.GetProperty("keys");
        Assert.Equal(GraphContract.ContractKeys.Length, keys.EnumerateObject().Count());
        Assert.Equal(keys.GetProperty("contract_version").GetString(), GraphContract.ContractVersionKey);
        Assert.Equal(keys.GetProperty("node_order").GetString(), GraphContract.NodeOrderKey);
        Assert.Equal(keys.GetProperty("window").GetString(), GraphContract.WindowKey);
        Assert.Equal(keys.GetProperty("horizon").GetString(), GraphContract.HorizonKey);
        Assert.Equal(keys.GetProperty("corr_window").GetString(), GraphContract.CorrWindowKey);
        Assert.Equal(keys.GetProperty("edge_threshold").GetString(), GraphContract.EdgeThresholdKey);
        Assert.Equal(keys.GetProperty("min_nodes").GetString(), GraphContract.MinNodesKey);
        Assert.Equal(keys.GetProperty("feature_mode").GetString(), GraphContract.FeatureModeKey);
        Assert.Equal(keys.GetProperty("channels").GetString(), GraphContract.ChannelsKey);
        Assert.Equal(keys.GetProperty("class_order").GetString(), GraphContract.ClassOrderKey);
        Assert.Equal(keys.GetProperty("timeframe").GetString(), GraphContract.TimeframeKey);
        Assert.All(GraphContract.ContractKeys, key => Assert.StartsWith(GraphContract.MetadataPrefix, key, StringComparison.Ordinal));
    }

    [Fact]
    public void Limits_MatchPython()
    {
        using var parity = Parity();
        var limits = parity.RootElement.GetProperty("contract").GetProperty("limits");
        Assert.Equal(limits.GetProperty("max_nodes").GetInt32(), GraphLimits.MaxNodes);
        Assert.Equal(limits.GetProperty("max_window").GetInt32(), GraphLimits.MaxWindow);
        Assert.Equal(limits.GetProperty("max_tensor_bytes").GetInt64(), GraphLimits.MaxTensorBytes);
    }

    [Fact]
    public void ChannelsOf_MatchesPythonForEveryPerWindowMode_AndRefusesComposedFeatures()
    {
        using var parity = Parity();
        foreach (var mode in parity.RootElement.GetProperty("contract").GetProperty("channels").EnumerateObject())
        {
            var parsed = PredictionModelMetadata.ParseFeatureMode(mode.Name);
            Assert.NotNull(parsed);
            Assert.Equal(mode.Value.GetInt32(), GraphContract.ChannelsOf(parsed!.Value));
        }

        Assert.Null(GraphContract.ChannelsOf(PredictionFeatureMode.ComposedFeatures));
    }

    [Fact]
    public void UnavailableResult_CarriesAReasonAndNeverAProbability()
    {
        var result = GraphPredictionResult.Unavailable(GraphUnavailableReason.InsufficientNodes, "1 valid node");
        Assert.False(result.IsAvailable);
        Assert.Equal(GraphPredictionStatus.Unavailable, result.Status);
        Assert.Equal("insufficient_nodes", result.Reason);
        Assert.Empty(result.Nodes);
    }
}
