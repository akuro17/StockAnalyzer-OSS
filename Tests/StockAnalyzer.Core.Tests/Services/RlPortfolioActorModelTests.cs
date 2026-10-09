using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// The actor boundary on hand-built linear ONNX graphs (not trained models). The fixture logits are
/// <c>sum_i x[i] * W[i][j] + b[j]</c> with <c>W[i][j] = ((7i + 13j) mod 17 - 8) / 1000</c> and <c>b[j] = (j - 1.5) / 10</c>, recomputed here in double
/// independently of ONNX Runtime. N_max = 3, so the observation has 970 values and the model returns 4 logits (cash first).
/// </summary>
public sealed class RlPortfolioActorModelTests : IDisposable
{
    private const int Length = 970, Logits = 4;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sa_rl_portfolio_actor_" + Guid.NewGuid().ToString("N"));
    private static readonly string Assets = Path.Combine(AppContext.BaseDirectory, "Assets");
    private static string Fixture(string name) => Path.Combine(Assets, name);

    public RlPortfolioActorModelTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private static Dictionary<string, string> Metadata() => new()
    {
        [RlPortfolioContract.MetadataContractHash] = RlPortfolioContract.ContractHash,
        [RlPortfolioContract.MetadataTargetType] = RlPortfolioContract.TargetType,
        [RlPortfolioContract.MetadataMaxSymbols] = "3",
        ["window_size"] = "64",
        [RlPortfolioContract.MetadataChannels] = "5",
        [RlPortfolioContract.MetadataUniverseSha256] = ContentHash.OfText("universe"),
        [RlPortfolioContract.MetadataCalendarSha256] = ContentHash.OfText("calendar"),
        [RlPortfolioContract.MetadataEvaluationSpecification] = "SA-RL-SHORT-HISTORY-v1",
        ["scaler_ref"] = "policy.onnx.scaler.json",
    };

    private string Model(Action<Dictionary<string, string>>? change = null, string fixture = "rl_portfolio_fixture_n3.onnx", bool scaler = true)
    {
        string directory = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "policy.onnx");
        var metadata = Metadata();
        change?.Invoke(metadata);
        OnnxMetadataFixture.Write(Fixture(fixture), path, metadata);
        if (scaler) RlPortfolioScaler.Save(path, RlPortfolioScaler.Create(Enumerable.Repeat(new FixedScalerStatistic(0d, 1d, -10d, 10d), 5).ToArray()));
        return path;
    }

    private static double[] Expected(float[] x)
    {
        var logits = new double[Logits];
        for (int j = 0; j < Logits; j++)
        {
            double sum = (j - 1.5) / 10;
            for (int i = 0; i < Length; i++) sum += x[i] * (((i * 7 + j * 13) % 17 - 8) / 1000.0);
            logits[j] = sum;
        }
        return logits;
    }

    private static float[] Pattern(int seed) => Enumerable.Range(0, Length).Select(i => ((i * 31 + seed) % 23 - 11) / 10f).ToArray();

    [Fact]
    public void LoadsAndMatchesTheIndependentLinearRecomputation()
    {
        using var model = new RlPortfolioActorModel(Model());
        Assert.Equal(3, model.MaxSymbols);
        foreach (int seed in new[] { 0, 5, 12 })
        {
            float[] x = Pattern(seed);
            double[] expected = Expected(x);
            float[] actual = model.Infer(x);
            Assert.Equal(Logits, actual.Length);
            for (int j = 0; j < Logits; j++) Assert.Equal(expected[j], actual[j], 4);
        }
    }

    [Fact]
    public void ParityWithTheNativeReferenceHolds()
    {
        using var model = new RlPortfolioActorModel(Model());
        model.ValidateParity(Fixture("rl_portfolio_fixture_n3.parity.json"));
    }

    [Fact]
    public void ParityRejectsAShiftedLogitAndAMalformedReference()
    {
        using var model = new RlPortfolioActorModel(Model());
        string shifted = Path.Combine(_root, "shifted.json");
        var doc = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Fixture("rl_portfolio_fixture_n3.parity.json")))!;
        doc["logits"]![1]![2] = doc["logits"]![1]![2]!.GetValue<double>() + 0.01;
        File.WriteAllText(shifted, doc.ToJsonString());
        Assert.Contains("logits parity failed", Assert.Throws<InvalidDataException>(() => model.ValidateParity(shifted)).Message);
        doc["logits"]!.AsArray().RemoveAt(0);
        File.WriteAllText(shifted, doc.ToJsonString());
        Assert.Throws<InvalidDataException>(() => model.ValidateParity(shifted));
    }

    [Theory]
    [InlineData(RlPortfolioContract.MetadataContractHash, "00")]
    [InlineData(RlPortfolioContract.MetadataTargetType, "reinforcement_learning_q")]
    [InlineData("window_size", "63")]
    [InlineData(RlPortfolioContract.MetadataChannels, "4")]
    [InlineData(RlPortfolioContract.MetadataMaxSymbols, "1")]
    [InlineData(RlPortfolioContract.MetadataMaxSymbols, "abc")]
    [InlineData(RlPortfolioContract.MetadataMaxSymbols, "4")]   // the graph is exported for 3 slots: shape mismatch
    [InlineData(RlPortfolioContract.MetadataUniverseSha256, "not-a-hash")]
    [InlineData(RlPortfolioContract.MetadataCalendarSha256, "ABC")]
    [InlineData("scaler_ref", "other.onnx.scaler.json")]
    public void MetadataThatBreaksTheContractIsRejected(string key, string value)
        => Assert.Throws<InvalidDataException>(() => new RlPortfolioActorModel(Model(m => m[key] = value)));

    [Theory]
    [InlineData(RlPortfolioContract.MetadataEvaluationSpecification)]
    [InlineData(RlPortfolioContract.MetadataContractHash)]
    [InlineData(RlPortfolioContract.MetadataUniverseSha256)]
    public void MissingMetadataIsRejected(string key)
        => Assert.Throws<InvalidDataException>(() => new RlPortfolioActorModel(Model(m => m.Remove(key))));

    [Fact]
    public void AMissingScalerSidecarIsRejected()
        => Assert.Throws<FileNotFoundException>(() => new RlPortfolioActorModel(Model(scaler: false)));

    [Fact]
    public void AWrongLogitCountIsRejectedAtLoad()
        => Assert.Contains("requires fixed float32", Assert.Throws<InvalidDataException>(() => new RlPortfolioActorModel(Model(fixture: "rl_portfolio_fixture_badshape.onnx"))).Message);

    [Fact]
    public void NonFiniteLogitsAreRejected()
    {
        using var model = new RlPortfolioActorModel(Model(fixture: "rl_portfolio_fixture_nan.onnx"));
        Assert.Contains("must be finite", Assert.Throws<InvalidDataException>(() => model.Infer(Pattern(0))).Message);
    }

    [Fact]
    public void ObservationsOfTheWrongLengthOrWithNonFiniteValuesAreRejected()
    {
        using var model = new RlPortfolioActorModel(Model());
        Assert.Throws<InvalidDataException>(() => model.Infer(new float[Length - 1]));
        Assert.Throws<InvalidDataException>(() => model.Infer(new float[Length + 1]));
        float[] bad = Pattern(1); bad[17] = float.NaN;
        Assert.Throws<InvalidDataException>(() => model.Infer(bad));
        bad[17] = float.PositiveInfinity;
        Assert.Throws<InvalidDataException>(() => model.Infer(bad));
        Assert.Throws<ArgumentNullException>(() => model.Infer(null!));
    }

    [Fact]
    public void DecideMasksIneligibleSlotsAndMatchesIndependentWeights()
    {
        using var model = new RlPortfolioActorModel(Model());
        float[] x = Pattern(5);
        double[] z = Expected(x);
        // Slot 1 (second asset) masked: softmax over cash, asset 0 and asset 2 only; independent double softmax, floored to four decimals.
        double max = new[] { z[0], z[1], z[3] }.Max();
        double sum = Math.Exp(z[0] - max) + Math.Exp(z[1] - max) + Math.Exp(z[3] - max);
        var weights = model.Decide(x, new[] { true, false, true }, 100);
        Assert.Equal(0m, weights.Assets[1]);
        Assert.Equal((decimal)Math.Floor(Math.Exp(z[1] - max) / sum * 10_000) / 10_000, weights.Assets[0]);
        Assert.Equal((decimal)Math.Floor(Math.Exp(z[3] - max) / sum * 10_000) / 10_000, weights.Assets[2]);
        Assert.Equal(1m, weights.Cash + weights.Assets.Sum());
    }

    [Fact]
    public void PaddedSlotsAreInertForTwoSymbolsAndTheFullModel()
    {
        using var model = new RlPortfolioActorModel(Model());
        // N = 2 in a model of 3 slots: the builder leaves the third slot exactly zero and the mask forces its weight to zero.
        var bars = RlPanelBarsFixture.Flat(new[] { 100m, 50m });
        var costs = RlContract.CreateConfiguration(new BacktestConfiguration { SizingModel = PositionSizingModel.FixedQuantity, SizingParameter = 1m, InitialCapital = 10_000m });
        using var session = new RlPortfolioSession(bars, RlPanelBarsFixture.Block(0, 1), ImmutableArray.Create(0, 1), costs);
        var start = session.Reset();
        float[] two = RlPortfolioObservationBuilder.Build(bars, 3, start, ImmutableArray.Create(0, 1), model.Scaler);
        Assert.All(two.Skip(2 * 322).Take(322), v => Assert.Equal(0f, v));
        var padded = model.Decide(two, RlPortfolioObservationBuilder.SlotMask(2, 3, ImmutableArray.Create(0, 1)), 100);
        Assert.Equal(0m, padded.Assets[2]);
        Assert.Equal(1m, padded.Cash + padded.Assets.Sum());

        // N = N_max = 3: every slot competes.
        var bars3 = RlPanelBarsFixture.Flat(new[] { 100m, 50m, 20m });
        using var session3 = new RlPortfolioSession(bars3, RlPanelBarsFixture.Block(0, 1, 2), ImmutableArray.Create(0, 1, 2), costs);
        float[] three = RlPortfolioObservationBuilder.Build(bars3, 3, session3.Reset(), ImmutableArray.Create(0, 1, 2), model.Scaler);
        Assert.Contains(three.Skip(2 * 322).Take(322), v => v != 0f);
        var full = model.Decide(three, RlPortfolioObservationBuilder.SlotMask(3, 3, ImmutableArray.Create(0, 1, 2)), 100);
        Assert.Equal(1m, full.Cash + full.Assets.Sum());
    }

    [Fact]
    public void DecideChecksItsArguments()
    {
        using var model = new RlPortfolioActorModel(Model());
        Assert.Throws<ArgumentException>(() => model.Decide(Pattern(0), new[] { true, true }, 100));
        Assert.Throws<ArgumentNullException>(() => model.Decide(Pattern(0), null!, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => model.Decide(Pattern(0), new[] { true, true, true }, 0));
    }

    [Fact]
    public void ADisposedModelRejectsInference()
    {
        var model = new RlPortfolioActorModel(Model());
        model.Dispose();
        model.Dispose();
        Assert.Throws<ObjectDisposedException>(() => model.Infer(Pattern(0)));
    }
}
