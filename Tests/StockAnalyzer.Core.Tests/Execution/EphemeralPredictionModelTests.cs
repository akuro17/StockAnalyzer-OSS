#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using StockAnalyzer.Core.Execution;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Tests.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Execution;

/// <summary>
/// WP-4: running a leased ephemeral model. The fixtures are real ONNX files with the contract the trainer writes (window 10, OHLCV min-max, 3 classes,
/// or a regression head); the equivalence test compares every probability with the application's own prediction service.
/// </summary>
public sealed class EphemeralPredictionModelTests
{
    private const int Window = 10;
    private const float ProbabilityTolerance = 1e-6f;

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Assets", name);

    private static ArtifactLease Lease(string onnxPath, Action? released = null)
    {
        using FileStream stream = File.OpenRead(onnxPath);
        string hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        var artifact = new EphemeralArtifact("20260101-120000000-abcdef12", Path.GetFileName(onnxPath), new FileInfo(onnxPath).Length, hash, "model.onnx.metrics.json", true, new long[] { -1, Window, 5 }, new long[] { -1, 3 });
        return new ArtifactLease(artifact, onnxPath, released ?? (() => { }));
    }

    // A reproducible random walk: the same bars on every run.
    private static List<CandleData> History(int bars, int seed = 7)
    {
        var random = new Random(seed);
        var candles = new List<CandleData>(bars);
        decimal close = 100m;
        for (int day = 0; day < bars; day++)
        {
            decimal open = close;
            close = Math.Max(1m, close + (decimal)(random.NextDouble() - 0.48) * 2m);
            decimal high = Math.Max(open, close) + (decimal)random.NextDouble();
            decimal low = Math.Min(open, close) - (decimal)random.NextDouble();
            candles.Add(new CandleData(new DateTime(2024, 1, 1).AddDays(day), open, high, low, close, 1000 + random.Next(0, 500)));
        }

        return candles;
    }

    [Fact]
    public void Open_ReadsTheContractFromTheModelItself()
    {
        using ArtifactLease lease = Lease(Fixture("trend_predictor_goodmeta.onnx"));
        using EphemeralPredictionModel model = EphemeralPredictionModel.Open(lease, new MLDataProcessor());

        EphemeralModelContract contract = model.Contract;
        Assert.Equal(Window, contract.WindowSize);
        Assert.Equal(nameof(PredictionFeatureMode.OhlcvMinMax), contract.FeatureMode);
        Assert.False(contract.IsRegression);
        Assert.Equal(3, contract.ClassLabels.Count);
        Assert.Equal(lease.Artifact.Sha256, contract.Sha256);
        Assert.True(contract.HorizonBars > 0);
    }

    // ---- AT-5.5: the same input gives the same probabilities as the GUI's prediction path ------------------------------------------------

    [Fact]
    public async Task PredictLast_GivesTheProbabilitiesOfTheApplicationsPredictionService()
    {
        string path = Fixture("trend_predictor_goodmeta.onnx");
        List<CandleData> history = History(40);
        using ArtifactLease lease = Lease(path);
        using EphemeralPredictionModel model = EphemeralPredictionModel.Open(lease, new MLDataProcessor());
        using var service = new PredictionService(new PredictionServiceTests.TestSettings(path, predictionWindowSize: Window), new MLDataProcessor());

        IReadOnlyList<EphemeralPrediction> predictions = model.PredictLast(history, 5);

        Assert.Equal(5, predictions.Count);
        for (int index = 0; index < predictions.Count; index++)
        {
            int end = history.Count - predictions.Count + 1 + index;
            PredictionResult gui = await service.PredictAsync(history.Take(end));
            Assert.False(gui.IsFallback);
            Assert.Equal(gui.Scores.Count, predictions[index].Scores.Count);
            for (int cls = 0; cls < gui.Scores.Count; cls++)
            {
                Assert.Equal(gui.Scores[cls].Label, predictions[index].Scores[cls].Label);
                Assert.InRange(Math.Abs(gui.Scores[cls].Score - predictions[index].Scores[cls].Score), 0f, ProbabilityTolerance);
            }

            Assert.InRange(Math.Abs(gui.Confidence - predictions[index].Confidence!.Value), 0f, ProbabilityTolerance);
        }
    }

    // ---- AT-5.1 / AT-5.2: the window of a bar holds that bar and the ones before it, nothing later ----------------------------------------

    [Fact]
    public void PredictLast_TheLastOfSeveralEqualsTheSingleResultAndNoLaterBarChangesAPrediction()
    {
        List<CandleData> history = History(40);
        using ArtifactLease lease = Lease(Fixture("trend_predictor_goodmeta.onnx"));
        using EphemeralPredictionModel model = EphemeralPredictionModel.Open(lease, new MLDataProcessor());

        IReadOnlyList<EphemeralPrediction> several = model.PredictLast(history, 4);
        EphemeralPrediction single = Assert.Single(model.PredictLast(history, 1));

        Assert.Equal(Scores(single), Scores(several[^1]));

        // The prediction for bar k is the same whether or not bars after k exist.
        for (int offset = 0; offset < several.Count; offset++)
        {
            int cut = history.Count - several.Count + 1 + offset;
            EphemeralPrediction truncated = Assert.Single(model.PredictLast(history.Take(cut).ToList(), 1));
            Assert.Equal(Scores(truncated), Scores(several[offset]));
        }

        // Different windows give different numbers (the model is not constant on this data).
        Assert.NotEqual(Scores(several[0]), Scores(several[^1]));
    }

    [Fact]
    public void PredictLast_AClassifierReturnsProbabilitiesThatSumToOneAndTheMostProbableClass()
    {
        using ArtifactLease lease = Lease(Fixture("trend_predictor_goodmeta.onnx"));
        using EphemeralPredictionModel model = EphemeralPredictionModel.Open(lease, new MLDataProcessor());

        foreach (EphemeralPrediction prediction in model.PredictLast(History(30), 3))
        {
            Assert.InRange(prediction.Scores.Sum(s => s.Score), 0.9999f, 1.0001f);
            ClassScore best = prediction.Scores.OrderByDescending(s => s.Score).First();
            Assert.Equal(best.Label, prediction.PredictedClass);
            Assert.Equal(best.Score, prediction.Confidence!.Value, 5);
            Assert.Null(prediction.Value);
        }
    }

    [Fact]
    public void PredictLast_ARegressionModelReturnsItsValueAndNoClasses()
    {
        using ArtifactLease lease = Lease(Fixture("trend_predictor_regression_v2.onnx"));
        using EphemeralPredictionModel model = EphemeralPredictionModel.Open(lease, new MLDataProcessor());

        EphemeralPrediction prediction = Assert.Single(model.PredictLast(History(20), 1));

        Assert.True(model.Contract.IsRegression);
        Assert.Empty(model.Contract.ClassLabels);
        Assert.Equal(0.04f, prediction.Value!.Value, 5); // the fixture's constant output
        Assert.Empty(prediction.Scores);
        Assert.Null(prediction.PredictedClass);
        Assert.Null(prediction.Confidence);
    }

    // ---- guards ---------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void PredictLast_TooFewBarsOrAnUnorderedHistoryIsRefused()
    {
        using ArtifactLease lease = Lease(Fixture("trend_predictor_goodmeta.onnx"));
        using EphemeralPredictionModel model = EphemeralPredictionModel.Open(lease, new MLDataProcessor());

        Assert.Throws<ArgumentException>(() => model.PredictLast(History(Window + 2), 4)); // needs 13
        Assert.Throws<ArgumentOutOfRangeException>(() => model.PredictLast(History(30), 0));
        List<CandleData> unordered = History(30);
        unordered[15] = unordered[14];
        Assert.Throws<InvalidDataException>(() => model.PredictLast(unordered, 1));
    }

    [Theory]
    [InlineData("trend_predictor_jointmeta.onnx")]            // another feature mode
    [InlineData("trend_predictor_fixed_scaler.onnx")]         // needs a fixed scaler
    [InlineData("trend_predictor_badmeta.onnx")]              // a contract that does not hold together
    [InlineData("trend_predictor_double_input.onnx")]         // not the graph shape of the trainer
    [InlineData("trend_predictor_duplicate_classes.onnx")]    // an invalid class order
    public void Open_AModelOutsideTheContractIsRefusedAsAContractError(string fixture)
    {
        using ArtifactLease lease = Lease(Fixture(fixture));

        Assert.Throws<EphemeralModelContractException>(() => EphemeralPredictionModel.Open(lease, new MLDataProcessor()));
    }

    [Fact]
    public void Open_AFileThatIsNotAnOnnxModelIsRefusedAsAContractError()
    {
        string path = Path.Combine(Path.GetTempPath(), "not-a-model-" + Guid.NewGuid().ToString("N") + ".onnx");
        File.WriteAllText(path, "not a model");
        try
        {
            using ArtifactLease lease = Lease(path);
            Assert.Throws<EphemeralModelContractException>(() => EphemeralPredictionModel.Open(lease, new MLDataProcessor()));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Open_WithGuiEquivalentPolicy_RunsFixedScalerAndJointMetaModels()
    {
        // 1. JointMeta (ZScoreOhlcvJoint feature mode)
        using (ArtifactLease leaseJoint = Lease(Fixture("trend_predictor_jointmeta.onnx")))
        {
            using EphemeralPredictionModel modelJoint = EphemeralPredictionModel.Open(
                leaseJoint, new MLDataProcessor(), EphemeralExecutionPolicy.GuiEquivalent);
            Assert.Equal("ZScoreOhlcvJoint", modelJoint.Contract.FeatureMode);
            var predsJoint = modelJoint.PredictLast(History(30), 2);
            Assert.Equal(2, predsJoint.Count);
            Assert.NotNull(predsJoint[0].PredictedClass);
        }

        // 2. Fixed scaler model
        using (ArtifactLease leaseScaler = Lease(Fixture("trend_predictor_fixed_scaler.onnx")))
        {
            using EphemeralPredictionModel modelScaler = EphemeralPredictionModel.Open(
                leaseScaler, new MLDataProcessor(), EphemeralExecutionPolicy.GuiEquivalent);
            var predsScaler = modelScaler.PredictLast(History(30), 2);
            Assert.Equal(2, predsScaler.Count);
            Assert.NotNull(predsScaler[0].PredictedClass);
        }
    }

    [Fact]
    public void Open_AFailedOpenReleasesTheModelFile()
    {
        string path = Path.Combine(Path.GetTempPath(), "released-" + Guid.NewGuid().ToString("N") + ".onnx");
        File.Copy(Fixture("trend_predictor_jointmeta.onnx"), path);
        try
        {
            using ArtifactLease lease = Lease(path);
            Assert.Throws<EphemeralModelContractException>(() => EphemeralPredictionModel.Open(lease, new MLDataProcessor()));
            File.Delete(path); // would fail while the session still held the file
            Assert.False(File.Exists(path));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static string Scores(EphemeralPrediction prediction) => string.Join(",", prediction.Scores.Select(s => $"{s.Label}:{s.Score:R}"));
}
