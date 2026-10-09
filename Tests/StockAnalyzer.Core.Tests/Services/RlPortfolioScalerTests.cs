using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>Scaler fit against an independent brute-force recomputation, and the sidecar contract.</summary>
public sealed class RlPortfolioScalerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sa_rl_portfolio_scaler_" + Guid.NewGuid().ToString("N"));
    public RlPortfolioScalerTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private static decimal Open(int s, int t) => s == 0 ? 100m + (t + 3) % 5 : 50m + (t + 1) % 4 * 0.5m;
    private static decimal Close(int s, int t) => s == 0 ? 100m + t % 7 : 50m + t % 11 * 0.5m;
    private static RlPanelBarsFixture Bars() => new(2, (s, t) => (Open(s, t), Close(s, t)));

    private static RlPanelCatalog Catalog()
    {
        var presence = Enumerable.Range(0, 2).Select(_ => (ReadOnlyMemory<int>)Enumerable.Range(0, 1279).ToArray()).ToArray();
        return RlPanelCatalogBuilder.Build(1278, presence, ImmutableArray.Create(false, false), RlPanelEvaluationMode.ShortHistory);
    }

    [Fact]
    public void FitMatchesAnIndependentTwoPassComputation()
    {
        var catalog = Catalog();
        Assert.Equal(new[] { 0, 192, 384 }, catalog.Train.Select(b => b.StartIndex).ToArray());
        var scaler = RlPortfolioScaler.Fit(Bars(), catalog);

        // Independent recomputation: every window element of every training decision (3 blocks x 2 symbols x decisions 63..190 of each block).
        var values = Enumerable.Range(0, 5).Select(_ => new List<double>()).ToArray();
        foreach (int block in new[] { 0, 192, 384 })
            for (int s = 0; s < 2; s++)
                for (int anchor = block + 63; anchor <= block + 190; anchor++)
                    for (int t = anchor - 63; t <= anchor; t++)
                    {
                        decimal reference = Close(s, anchor);
                        decimal high = Math.Max(Open(s, t), Close(s, t)) + 1m, low = Math.Max(Math.Min(Open(s, t), Close(s, t)) - 1m, 0.01m);
                        values[0].Add(Math.Log((double)Open(s, t) / (double)reference));
                        values[1].Add(Math.Log((double)high / (double)reference));
                        values[2].Add(Math.Log((double)low / (double)reference));
                        values[3].Add(Math.Log((double)Close(s, t) / (double)reference));
                        values[4].Add(Math.Log(1001d));
                    }
        Assert.All(values, v => Assert.Equal(3 * 2 * 128 * 64, v.Count));
        for (int c = 0; c < 5; c++)
        {
            double mean = values[c].Sum() / values[c].Count;
            double variance = values[c].Sum(v => (v - mean) * (v - mean)) / values[c].Count;
            Assert.Equal(mean, scaler.Statistics[c].Mean, 9);
            Assert.Equal(Math.Sqrt(variance), scaler.Statistics[c].Std, 9);
            Assert.Equal(values[c].Min(), scaler.Statistics[c].Min, 9);
            Assert.Equal(values[c].Max(), scaler.Statistics[c].Max, 9);
        }
        // The constant volume channel is exactly ln(1 + 1000) with no spread.
        Assert.Equal(Math.Log(1001d), scaler.Statistics[4].Mean, 12);
        Assert.Equal(0d, scaler.Statistics[4].Std, 9);
        Assert.Equal(RlPortfolioContract.ScalerFeatureMode, scaler.FeatureMode);
        Assert.Equal(RlPortfolioContract.ContractHash, scaler.FeatureContractHash);
    }

    [Fact]
    public void FitReadsTrainingBlocksOfTrainingSymbolsOnly()
    {
        // Four symbols, the last two held out: the fit must never read a holdout symbol's bars.
        var presence = Enumerable.Range(0, 4).Select(_ => (ReadOnlyMemory<int>)Enumerable.Range(0, 1279).ToArray()).ToArray();
        var catalog = RlPanelCatalogBuilder.Build(1278, presence, ImmutableArray.Create(false, false, true, true), RlPanelEvaluationMode.ShortHistory);
        var bars = new RlPanelBarsFixture(4, (s, t) => (Open(s % 2, t), Close(s % 2, t)));
        _ = RlPortfolioScaler.Fit(bars, catalog);
        Assert.All(bars.Reads, r => Assert.True(r.Symbol < 2));
        Assert.All(bars.Reads, r => Assert.InRange(r.Index, 0, 575)); // train blocks end at 576; validation and OOS bars are never read
    }

    [Fact]
    public void SaveAndLoadRoundTripThroughTheOnnxMetadata()
    {
        var scaler = RlPortfolioScaler.Fit(Bars(), Catalog());
        string model = Path.Combine(_root, "policy.onnx");
        RlPortfolioScaler.Save(model, scaler);
        var loaded = RlPortfolioScaler.Load(model, Metadata());
        Assert.Equal(scaler.Statistics, loaded.Statistics);
        Assert.Equal(scaler.ChannelIdentities, loaded.ChannelIdentities);
        Assert.Equal(("policy.onnx.scaler.json"), RlPortfolioScaler.SidecarName(model));
    }

    private static Dictionary<string, string> Metadata() => new()
    {
        ["scaler_ref"] = "policy.onnx.scaler.json", ["window_size"] = "64", ["channels"] = "5",
    };

    [Theory]
    [InlineData("schema_version", 2)]
    [InlineData("feature_mode", "composed_features")]
    [InlineData("window_size", 65)]
    [InlineData("clip_sigma", 3.0)]
    [InlineData("feature_contract_hash", "00")]
    public void ASidecarThatBreaksTheContractIsRejected(string field, object value)
    {
        string model = SavedScaler();
        RlSourceFixture.Mutate(_root, "policy.onnx.scaler.json", root => root[field] = System.Text.Json.JsonSerializer.SerializeToNode(value));
        Assert.Throws<InvalidDataException>(() => RlPortfolioScaler.Load(model, Metadata()));
    }

    [Fact]
    public void LagsChannelsAndStatisticsAreValidated()
    {
        string model = SavedScaler();
        void Reject(Action<JsonNode> change)
        {
            File.Copy(model + ".scaler.json", model + ".scaler.json.bak", true);
            try
            {
                RlSourceFixture.Mutate(_root, "policy.onnx.scaler.json", change);
                Assert.Throws<InvalidDataException>(() => RlPortfolioScaler.Load(model, Metadata()));
            }
            finally { File.Copy(model + ".scaler.json.bak", model + ".scaler.json", true); }
        }
        Reject(root => root["lags"] = new JsonArray(1));
        Reject(root => { var names = root["channel_identities"]!.AsArray(); var first = names[0]!.DeepClone(); names[0] = names[1]!.DeepClone(); names[1] = first; });
        Reject(root => root["statistics"]!.AsArray().RemoveAt(0));
        Reject(root => root["statistics"]![0]!["std"] = -1);
        Reject(root => { root["statistics"]![0]!["min"] = 5; root["statistics"]![0]!["max"] = 1; });
        _ = RlPortfolioScaler.Load(model, Metadata());
    }

    [Fact]
    public void MetadataMustAgreeWithTheSidecar()
    {
        string model = SavedScaler();
        Assert.Throws<InvalidDataException>(() => RlPortfolioScaler.Load(model, new Dictionary<string, string>(Metadata()) { ["scaler_ref"] = "other.onnx.scaler.json" }));
        Assert.Throws<InvalidDataException>(() => RlPortfolioScaler.Load(model, new Dictionary<string, string>(Metadata()) { ["window_size"] = "63" }));
        Assert.Throws<InvalidDataException>(() => RlPortfolioScaler.Load(model, new Dictionary<string, string>(Metadata()) { ["channels"] = "4" }));
        Assert.Throws<InvalidDataException>(() => RlPortfolioScaler.Load(model, new Dictionary<string, string>()));
        File.Delete(model + ".scaler.json");
        Assert.Throws<FileNotFoundException>(() => RlPortfolioScaler.Load(model, Metadata()));
    }

    [Fact]
    public void ArgumentsAreChecked()
    {
        Assert.Throws<ArgumentNullException>(() => RlPortfolioScaler.Fit(null!, Catalog()));
        Assert.Throws<ArgumentNullException>(() => RlPortfolioScaler.Fit(Bars(), null!));
        Assert.Throws<ArgumentNullException>(() => RlPortfolioScaler.Create(null!));
        Assert.Throws<InvalidDataException>(() => RlPortfolioScaler.Save(Path.Combine(_root, "x.onnx"),
            RlPortfolioScaler.Create(new[] { new FixedScalerStatistic(0, 1, 0, 1) })));
    }

    private string SavedScaler()
    {
        string model = Path.Combine(_root, "policy.onnx");
        RlPortfolioScaler.Save(model, RlPortfolioScaler.Fit(Bars(), Catalog()));
        return model;
    }
}
