using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// FX1a: the observation variants (v1 = the pinned five-channel contract, v2-a = five channels plus two cross-sectional ones) behind the shared readers.
/// The v1 numbers are hand-computed and must not move; the v2-a numbers follow the same formula with seven channels. The v2-a ONNX graph is a hand-built
/// linear graph (generate_rl_portfolio_fixture.py), recomputed here in double independently of ONNX Runtime.
/// </summary>
public sealed class RlPortfolioObservationLayoutTests : IDisposable
{
    private const string PinnedV2Hash = "41EA1C71AA575C8A7301B7C2EAD5F7EE0D8027365D1234F37F43F402BEF2E7C6";
    private const int V2Length = 1354, Logits = 4;      // 3 * (64 * 7 + 2) + 4
    private static readonly TrainingResourceLimits Limits = RlTrainingValidation.FreezeLimits(continuous: true);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sa_rl_portfolio_layout_" + Guid.NewGuid().ToString("N"));
    private static readonly string Assets = Path.Combine(AppContext.BaseDirectory, "Assets");

    public RlPortfolioObservationLayoutTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private static RlPortfolioObservationLayout V1 => RlPortfolioObservationLayout.V1;
    private static RlPortfolioObservationLayout V2 => RlPortfolioObservationLayout.V2A;

    [Fact]
    public void TheV1LayoutIsExactlyThePinnedContract()
    {
        Assert.Equal(RlPortfolioContract.SpecificationId, V1.SpecificationId);
        Assert.Equal("A9F7C8AC29790AF92714161DF24A39CC49C6BB470C0AD1EBFB38E617F066F00F", V1.ContractHash);
        Assert.Equal(RlPortfolioContract.ScalerFeatureMode, V1.ScalerFeatureMode);
        Assert.Equal(RlContract.MarketChannelCount, V1.MarketChannelCount);
        Assert.Equal(RlPortfolioContract.ScalerChannelNames, V1.ChannelNames);
        Assert.Equal(322, V1.SlotLength);                          // 64 * 5 + 2
        Assert.Equal(16_104, V1.ObservationLength(50));
    }

    [Fact]
    public void TheV2ContractIsItsOwnPinnedHashAndNeverTouchesTheV1Contract()
    {
        Assert.Equal(PinnedV2Hash, RlPortfolioV2Contract.ContractHash);
        Assert.NotEqual(RlPortfolioContract.ContractHash, RlPortfolioV2Contract.ContractHash);
        Assert.Equal(64, RlPortfolioV2Contract.ContractHash.Length);
        Assert.Equal(RlPortfolioContract.ContractHash, RlPortfolioV2Contract.ToWireDefaults()["PortfolioContractHash"]);
        // The v1 wire defaults do not mention any v2 constant (they are reflection-pinned), and the v2 constants are not duplicated from v1.
        Assert.DoesNotContain(RlPortfolioContract.ToWireDefaults().Keys, k => k.Contains("FeatureConventions", StringComparison.Ordinal));
        Assert.Equal(RlPortfolioContract.ScalerChannelNames.Concat(RlPortfolioV2Contract.ExtraChannelNames), RlPortfolioV2Contract.ScalerChannelNames);
        Assert.Equal(RlContract.MarketChannelCount + RlPortfolioV2Contract.ExtraChannelNames.Length, RlPortfolioV2Contract.MarketChannelCount);
    }

    [Fact]
    public void TheV2LayoutAddsTwoChannelsAndKeepsTheFirstFiveInPlace()
    {
        Assert.Equal(7, V2.MarketChannelCount);
        Assert.Equal(RlPortfolioContract.ScalerChannelNames, V2.ChannelNames.Take(RlContract.MarketChannelCount));
        Assert.Equal(450, V2.SlotLength);                          // 64 * 7 + 2
        Assert.Equal(V2Length, V2.ObservationLength(3));
        Assert.Throws<ArgumentOutOfRangeException>(() => V2.ObservationLength(0));
    }

    [Fact]
    public void ALayoutIsFoundByItsContractHashAndOnlyByIt()
    {
        Assert.True(RlPortfolioObservationLayout.TryFromContractHash(RlPortfolioContract.ContractHash, out var one));
        Assert.Same(V1, one);
        Assert.True(RlPortfolioObservationLayout.TryFromContractHash(RlPortfolioV2Contract.ContractHash, out var two));
        Assert.Same(V2, two);
        Assert.False(RlPortfolioObservationLayout.TryFromContractHash("00", out _));
        Assert.False(RlPortfolioObservationLayout.TryFromContractHash(RlPortfolioContract.ContractHash.ToLowerInvariant(), out _));
        Assert.False(RlPortfolioObservationLayout.TryFromContractHash(null, out _));
    }

    [Fact]
    public void TheResourceArithmeticFollowsTheLayoutAndDefaultsToV1()
    {
        Assert.Equal(RlPortfolioResources.ObservationLength(50), RlPortfolioResources.ObservationLength(50, V1));
        Assert.Equal(RlPortfolioResources.RolloutBytes(50), RlPortfolioResources.RolloutBytes(50, V1));
        Assert.Equal(RlPortfolioResources.ObservationFrameBytes(50), RlPortfolioResources.ObservationFrameBytes(50, V1));
        Assert.Equal(RlPortfolioResources.MaximumSymbols(Limits), RlPortfolioResources.MaximumSymbols(Limits, V1));
        Assert.Equal(50L * 450 + 4, RlPortfolioResources.ObservationLength(50, V2));
        Assert.Equal(576L * (22_504 + 51 + 6) * 4 + 2L * 1024 * 1024, RlPortfolioResources.RolloutBytes(50, V2));
        Assert.Equal(22_504L * 16, RlPortfolioResources.ObservationFrameBytes(50, V2));
    }

    [Fact]
    public void TheV2UniverseLimitDropsToAboutFiveSeventhsOfV1AndIsTheLargestThatFits()
    {
        int v1 = RlPortfolioResources.MaximumSymbols(Limits, V1);
        int v2 = RlPortfolioResources.MaximumSymbols(Limits, V2);
        Assert.True(RlPortfolioResources.Fits(v2, Limits, V2));
        Assert.False(RlPortfolioResources.Fits(v2 + 1, Limits, V2));
        // 326 / 450 = 0.724 and the 7/5 channel ratio of the market window gives about 5/7 = 0.714; the bound states the accepted cost (D-FX5).
        Assert.InRange((double)v2 / v1, 0.70, 0.74);
    }

    private static FixedScalerStatistic[] Statistics(int count) => Enumerable.Repeat(new FixedScalerStatistic(0d, 1d, -10d, 10d), count).ToArray();

    private static Dictionary<string, string> ScalerMetadata(string channels, string? hash = null)
    {
        var metadata = new Dictionary<string, string> { ["scaler_ref"] = "policy.onnx.scaler.json", ["window_size"] = "64", ["channels"] = channels };
        if (hash is not null) metadata[RlPortfolioContract.MetadataContractHash] = hash;
        return metadata;
    }

    [Fact]
    public void AV2ScalerRoundTripsAndNamesItsVariant()
    {
        string model = Path.Combine(_root, "policy.onnx");
        var scaler = RlPortfolioScaler.Create(Statistics(7), V2);
        Assert.Equal(RlPortfolioV2Contract.ContractHash, scaler.FeatureContractHash);
        Assert.Equal(RlPortfolioV2Contract.ScalerFeatureMode, scaler.FeatureMode);
        RlPortfolioScaler.Save(model, scaler);
        var loaded = RlPortfolioScaler.Load(model, ScalerMetadata("7", RlPortfolioV2Contract.ContractHash));
        Assert.Equal(scaler.Statistics, loaded.Statistics);
        Assert.Equal(V2.ChannelNames.ToArray(), loaded.ChannelIdentities);
    }

    [Fact]
    public void AV1ScalerIsStillTheDefaultAndStillLoadsOnlyWithFiveChannels()
    {
        string model = Path.Combine(_root, "policy.onnx");
        var scaler = RlPortfolioScaler.Create(Statistics(5));
        Assert.Equal(RlPortfolioContract.ContractHash, scaler.FeatureContractHash);
        RlPortfolioScaler.Save(model, scaler);
        _ = RlPortfolioScaler.Load(model, ScalerMetadata("5"));
        Assert.Throws<InvalidDataException>(() => RlPortfolioScaler.Load(model, ScalerMetadata("7")));
    }

    [Fact]
    public void AScalerThatMixesTheVariantsIsRejectedAtSaveAndAtLoad()
    {
        string model = Path.Combine(_root, "policy.onnx");
        Assert.Throws<InvalidDataException>(() => RlPortfolioScaler.Save(model, RlPortfolioScaler.Create(Statistics(5), V2)));   // 5 statistics for 7 channels
        Assert.Throws<InvalidDataException>(() => RlPortfolioScaler.Save(model, RlPortfolioScaler.Create(Statistics(7), V1)));
        var wrongNames = RlPortfolioScaler.Create(Statistics(7), V2) with { ChannelIdentities = V1.ChannelNames.Concat(new[] { "a", "b" }).ToArray() };
        Assert.Throws<InvalidDataException>(() => RlPortfolioScaler.Save(model, wrongNames));
        var v1Mode = RlPortfolioScaler.Create(Statistics(7), V2) with { FeatureMode = RlPortfolioContract.ScalerFeatureMode };
        Assert.Throws<InvalidDataException>(() => RlPortfolioScaler.Save(model, v1Mode));
        var unknown = RlPortfolioScaler.Create(Statistics(7), V2) with { FeatureContractHash = "00" };
        Assert.Throws<InvalidDataException>(() => RlPortfolioScaler.Save(model, unknown));
        RlPortfolioScaler.Save(model, RlPortfolioScaler.Create(Statistics(7), V2));
        // The ONNX contract hash must be the one the sidecar names.
        Assert.Throws<InvalidDataException>(() => RlPortfolioScaler.Load(model, ScalerMetadata("7", RlPortfolioContract.ContractHash)));
    }

    // ---- ONNX boundary on the hand-built 7-channel graph ----

    private static Dictionary<string, string> OnnxMetadata(string hash, string channels) => new()
    {
        [RlPortfolioContract.MetadataContractHash] = hash,
        [RlPortfolioContract.MetadataTargetType] = RlPortfolioContract.TargetType,
        [RlPortfolioContract.MetadataMaxSymbols] = "3",
        ["window_size"] = "64",
        [RlPortfolioContract.MetadataChannels] = channels,
        [RlPortfolioContract.MetadataUniverseSha256] = ContentHash.OfText("universe"),
        [RlPortfolioContract.MetadataCalendarSha256] = ContentHash.OfText("calendar"),
        [RlPortfolioContract.MetadataEvaluationSpecification] = "SA-RL-SHORT-HISTORY-v1",
        ["scaler_ref"] = "policy.onnx.scaler.json",
    };

    private string Model(string fixture, Dictionary<string, string> metadata, RlPortfolioObservationLayout scalerLayout)
    {
        string directory = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "policy.onnx");
        OnnxMetadataFixture.Write(Path.Combine(Assets, fixture), path, metadata);
        RlPortfolioScaler.Save(path, RlPortfolioScaler.Create(Statistics(scalerLayout.MarketChannelCount), scalerLayout));
        return path;
    }

    private static double[] Expected(float[] x)
    {
        var logits = new double[Logits];
        for (int j = 0; j < Logits; j++)
        {
            double sum = (j - 1.5) / 10;
            for (int i = 0; i < V2Length; i++) sum += x[i] * (((i * 7 + j * 13) % 17 - 8) / 1000.0);
            logits[j] = sum;
        }
        return logits;
    }

    [Fact]
    public void TheV2ActorLoadsOnTheSevenChannelGraphAndMatchesTheIndependentRecomputation()
    {
        using var model = new RlPortfolioActorModel(Model("rl_portfolio_fixture_v2a_n3.onnx", OnnxMetadata(RlPortfolioV2Contract.ContractHash, "7"), V2));
        Assert.Same(V2, model.Layout);
        Assert.Equal(RlPortfolioV2Contract.ContractHash, model.Scaler.FeatureContractHash);
        float[] x = Enumerable.Range(0, V2Length).Select(i => ((i * 31 + 5) % 23 - 11) / 10f).ToArray();
        double[] expected = Expected(x);
        float[] actual = model.Infer(x);
        for (int j = 0; j < Logits; j++) Assert.Equal(expected[j], actual[j], 4);
        Assert.Throws<InvalidDataException>(() => model.Infer(new float[1_330]));           // the v1 length for three slots is 970: wrong for v2
    }

    [Fact]
    public void TheV1ActorStillReportsTheV1Layout()
    {
        using var model = new RlPortfolioActorModel(Model("rl_portfolio_fixture_n3.onnx", OnnxMetadata(RlPortfolioContract.ContractHash, "5"), V1));
        Assert.Same(V1, model.Layout);
    }

    [Fact]
    public void AnActorWhoseGraphMetadataAndSidecarDisagreeIsRejected()
    {
        // v1 metadata on the v2 graph: the declared observation length (970) does not match the graph input (1354).
        Assert.Contains("requires fixed float32", Assert.Throws<InvalidDataException>(() =>
            new RlPortfolioActorModel(Model("rl_portfolio_fixture_v2a_n3.onnx", OnnxMetadata(RlPortfolioContract.ContractHash, "5"), V1))).Message);
        // v2 metadata on the v1 graph.
        Assert.Contains("requires fixed float32", Assert.Throws<InvalidDataException>(() =>
            new RlPortfolioActorModel(Model("rl_portfolio_fixture_n3.onnx", OnnxMetadata(RlPortfolioV2Contract.ContractHash, "7"), V2))).Message);
        // v2 hash with the v1 channel count, and an unknown hash.
        Assert.Throws<InvalidDataException>(() => new RlPortfolioActorModel(Model("rl_portfolio_fixture_v2a_n3.onnx", OnnxMetadata(RlPortfolioV2Contract.ContractHash, "5"), V2)));
        Assert.Throws<InvalidDataException>(() => new RlPortfolioActorModel(Model("rl_portfolio_fixture_v2a_n3.onnx", OnnxMetadata("00", "7"), V2)));
        // A v1 sidecar beside a v2 model.
        Assert.Throws<InvalidDataException>(() => new RlPortfolioActorModel(Model("rl_portfolio_fixture_v2a_n3.onnx", OnnxMetadata(RlPortfolioV2Contract.ContractHash, "7"), V1)));
    }
}
