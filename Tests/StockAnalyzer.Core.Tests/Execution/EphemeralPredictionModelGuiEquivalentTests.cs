#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using StockAnalyzer.Core.Execution;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Execution;

public sealed class EphemeralPredictionModelGuiEquivalentTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Assets", name);

    private static ArtifactLease Lease(string onnxPath)
    {
        using FileStream stream = File.OpenRead(onnxPath);
        string hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        var artifact = new EphemeralArtifact("20260101-120000000-abcdef12", Path.GetFileName(onnxPath), new FileInfo(onnxPath).Length, hash, "model.onnx.metrics.json", true, new long[] { -1, 10, 5 }, new long[] { -1, 3 });
        return new ArtifactLease(artifact, onnxPath, () => { });
    }

    [Fact]
    public void Open_GuiEquivalentPolicy_AllowsIndicatorFactory()
    {
        string path = Fixture("trend_predictor_goodmeta.onnx");
        using ArtifactLease lease = Lease(path);
        var indicatorFactory = new IndicatorFactory();
        using EphemeralPredictionModel model = EphemeralPredictionModel.Open(
            lease, new MLDataProcessor(), EphemeralExecutionPolicy.GuiEquivalent, indicatorFactory);

        Assert.NotNull(model);
        Assert.Equal(0, model.LeadBars);
        Assert.Equal(10, model.Contract.WindowSize);
    }

    [Fact]
    public void ResolveParameterObject_DefaultSettings_ResolvesDefaultPeriod()
    {
        var factory = new IndicatorFactory();
        var channel = new FeatureChannel
        {
            Kind = FeatureChannelKind.Indicator,
            Indicator = IndicatorType.SMA,
            Params = new Dictionary<string, string>(), // Empty params = use registry default
        };

        var paramObj = FeatureChannelConverter.ResolveParameterObject(channel, factory, out var warnings);
        Assert.NotNull(paramObj);
        Assert.Empty(warnings);

        var periodProp = paramObj.GetType().GetProperty("Period");
        Assert.NotNull(periodProp);
        int period = (int)periodProp.GetValue(paramObj)!;
        Assert.True(period >= 14);
    }
}
