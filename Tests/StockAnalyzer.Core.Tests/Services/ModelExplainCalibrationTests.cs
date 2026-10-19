#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// T11 explanation and T12 calibration. The fixture (<c>Assets/explain_calibration_fixture.onnx*</c>) is produced by the
/// production Python code, so every loader below is tested against real Python output.
/// </summary>
[Collection("Non-Parallel ONNX Tests")]
public sealed class ModelExplainCalibrationTests : IDisposable
{
    private const string FixtureName = "explain_calibration_fixture.onnx";
    private static readonly string DataRevision = new('a', 64);
    private static string Fixture => Path.Combine("Assets", FixtureName);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sa_t11_t12_" + Guid.NewGuid().ToString("N"));

    private static string SolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "StockAnalyzer.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return directory!.FullName;
    }

    private static JsonDocument Parity(string name) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(SolutionRoot(), "Tests", name)));

    /// <summary>A private copy of the fixture bundle; <paramref name="omit"/> lists sidecar suffixes to leave out.</summary>
    private string Source(params string[] omit)
    {
        var folder = Path.Combine(_directory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var model = Path.Combine(folder, FixtureName);
        foreach (var suffix in new[] { "", ".metrics.json", ".calibration.json", ".explain.json" })
            if (!omit.Contains(suffix)) File.Copy(Fixture + suffix, model + suffix);
        return model;
    }

    private static Dictionary<string, string> Metadata(string model)
    {
        using var session = new InferenceSession(model);
        return new Dictionary<string, string>(session.ModelMetadata.CustomMetadataMap);
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static void Edit(string path, Action<JsonNode> change)
    {
        var node = JsonNode.Parse(File.ReadAllText(path))!;
        change(node);
        File.WriteAllText(path, node.ToJsonString());
    }

    private static ModelCalibration LoadCalibration(string model) =>
        ModelCalibration.Load(model, Metadata(model), Hash(model), DataRevision);

    // ---------------------------------------------------------------- calibration contract / math

    [Fact]
    public void CalibrationConstants_MatchPythonParityFixture()
    {
        using var parity = Parity("ModelCalibrationParity.json");
        var root = parity.RootElement;
        Assert.Equal(ModelCalibrationContract.SchemaVersion, root.GetProperty("schema_version").GetInt32());
        Assert.Equal(ModelCalibrationContract.ReferenceKey, root.GetProperty("reference_key").GetString());
        Assert.Equal(ModelCalibrationContract.Method, root.GetProperty("method").GetString());
        Assert.Equal(ModelCalibrationContract.Score, root.GetProperty("score").GetString());
        Assert.Equal(ModelCalibrationContract.Epsilon, root.GetProperty("epsilon").GetDouble());
        Assert.Equal(ModelCalibrationContract.EceBins, root.GetProperty("ece_bins").GetInt32());
        Assert.Equal(root.GetProperty("softmax_sum_tolerance").GetDouble(), IMLDataProcessor.SoftmaxSumTolerance, 8);
        Assert.Equal(ModelCalibrationContract.AvailableStatus, root.GetProperty("status_available").GetString());
        Assert.Equal(ModelCalibrationContract.BoundaryStatus, root.GetProperty("status_boundary").GetString());
        Assert.Equal(ModelCalibrationContract.NllDefinition, root.GetProperty("nll_definition").GetString());
        Assert.Equal(ModelCalibrationContract.EvaluationPopulation, root.GetProperty("evaluation_population").GetString());
        Assert.Equal(ModelCalibrationLimits.TemperatureHardMin, root.GetProperty("temperature_hard_min").GetDouble());
        Assert.Equal(ModelCalibrationLimits.TemperatureHardMax, root.GetProperty("temperature_hard_max").GetDouble());
        Assert.Equal(ModelCalibrationLimits.TemperatureStandardMin, root.GetProperty("temperature_standard_min").GetDouble());
        Assert.Equal(ModelCalibrationLimits.TemperatureStandardMax, root.GetProperty("temperature_standard_max").GetDouble());
        Assert.Equal(ModelCalibrationLimits.MinimumSamples, root.GetProperty("min_samples").GetInt32());
    }

    [Fact]
    public void Apply_MatchesPythonCases_AndNeverChangesTheArgmax()
    {
        using var parity = Parity("ModelCalibrationParity.json");
        var template = LoadCalibration(Source());
        foreach (var item in parity.RootElement.GetProperty("cases").EnumerateArray())
        {
            var probabilities = item.GetProperty("probabilities").EnumerateArray().Select(v => (float)v.GetDouble()).ToArray();
            var expected = item.GetProperty("calibrated").EnumerateArray().Select(v => v.GetDouble()).ToArray();
            var calibration = template with { Temperature = item.GetProperty("temperature").GetDouble() };
            var actual = new float[probabilities.Length];
            calibration.Apply(probabilities, actual);
            for (int i = 0; i < actual.Length; i++) Assert.Equal(expected[i], actual[i], 6);
            Assert.Equal(1.0, actual.Sum(), 5);
            Assert.Equal(Array.IndexOf(probabilities, probabilities.Max()), Array.IndexOf(actual, actual.Max()));
        }
    }

    [Fact]
    public void Apply_KnownValue_UsesRootOfTheProbabilitiesAtTemperatureTwo()
    {
        var calibration = LoadCalibration(Source()) with { Temperature = 2.0 };
        var output = new float[3];
        calibration.Apply(new[] { 0.8f, 0.1f, 0.1f }, output);
        Assert.Equal(0.585786f, output[0], 5);
        Assert.Equal(0.207107f, output[1], 5);
        Assert.Equal(0.207107f, output[2], 5);
    }

    [Theory]
    [InlineData(float.NaN, 0.5f, 0.5f)]
    [InlineData(-0.1f, 0.6f, 0.5f)]
    [InlineData(1.2f, 0.0f, -0.2f)]
    [InlineData(0.5f, 0.3f, 0.1f)]
    [InlineData(0.9f, 0.9f, 0.9f)]
    public void Apply_RejectsAnInvalidProbabilityRow(float a, float b, float c)
    {
        var calibration = LoadCalibration(Source());
        Assert.Throws<InvalidDataException>(() => calibration.Apply(new[] { a, b, c }, new float[3]));
    }

    [Fact]
    public void Apply_RejectsMismatchedOrEmptyWidth()
    {
        var calibration = LoadCalibration(Source());
        Assert.Throws<ArgumentException>(() => calibration.Apply(new[] { 0.5f, 0.5f }, new float[3]));
        Assert.Throws<ArgumentException>(() => calibration.Apply(ReadOnlySpan<float>.Empty, Span<float>.Empty));
    }

    [Fact]
    public void Fixture_LoadsAndImprovesOnTheRawProbabilities()
    {
        var model = Source();
        var calibration = LoadCalibration(model);
        Assert.True(ModelCalibrationLimits.IsWithinStandardBand(calibration.Temperature));
        Assert.Equal("Up,Down,Neutral", calibration.ClassOrder);
        Assert.Equal(new[] { "Up", "Down", "Neutral" }, calibration.Classes);
        Assert.True(calibration.Fit.SampleCount >= ModelCalibrationLimits.MinimumSamples);
        var set = calibration.Evaluation.CalibrationSet;
        Assert.True(set.After.Nll <= set.Before.Nll);
        Assert.True(calibration.Evaluation.OuterTest.After.Nll <= calibration.Evaluation.OuterTest.Before.Nll);
        // the runtime loader needs no evaluation revision and computes the model hash itself
        Assert.Equal(calibration.Temperature, ModelCalibration.Load(model, Metadata(model)).Temperature);
    }

    [Fact]
    public void Fixture_CarriesEqualMassEceReliabilityAndTheEvaluationDefinition()
    {
        var calibration = LoadCalibration(Source());
        Assert.True(calibration.IsEnabled);
        Assert.Equal(ModelCalibrationContract.NllDefinition, calibration.NllDefinition);
        Assert.Equal(ModelCalibrationContract.EvaluationPopulation, calibration.EvaluationPopulation);
        Assert.True(calibration.EvaluationIsOos);
        foreach (var metrics in new[] { calibration.Evaluation.CalibrationSet, calibration.Evaluation.OuterTest })
        {
            Assert.InRange(metrics.Before.EceEqualMass!.Value, 0, 1);
            Assert.InRange(metrics.After.EceEqualMass!.Value, 0, 1);
            foreach (var bins in new[] { metrics.Reliability!.Before, metrics.Reliability.After })
            {
                Assert.Equal(ModelCalibrationContract.EceBins, bins.Length);
                Assert.Equal(metrics.SampleCount, bins.Sum(b => b.Count));
                Assert.All(bins, b => Assert.Equal(b.Count > 0, b.MeanConfidence.HasValue && b.Accuracy.HasValue));
            }
            // the equal-width bins reproduce the stored ECE
            var after = metrics.Reliability.After.Where(b => b.Count > 0)
                .Sum(b => (double)b.Count / metrics.SampleCount * Math.Abs(b.Accuracy!.Value - b.MeanConfidence!.Value));
            Assert.Equal(metrics.After.Ece, after, 9);
        }
    }

    [Fact]
    public void Load_AcceptsASidecarWithoutTheOptionalEvaluationDiagnostics()
    {
        var model = Source();
        Edit(model + ".calibration.json", doc =>
        {
            var root = doc.AsObject();
            foreach (var key in new[] { "nll_definition", "evaluation_population", "evaluation_is_oos" }) root.Remove(key);
            foreach (var set in new[] { "calibration_set", "outer_test" })
            {
                var block = root["evaluation"]![set]!.AsObject();
                block.Remove("reliability");
                block["before"]!.AsObject().Remove("ece_equal_mass");
                block["after"]!.AsObject().Remove("ece_equal_mass");
            }
        });
        var calibration = LoadCalibration(model);
        Assert.True(calibration.IsEnabled);
        Assert.Null(calibration.NllDefinition);
        Assert.Null(calibration.Evaluation.OuterTest.Reliability);
        Assert.Null(calibration.Evaluation.OuterTest.After.EceEqualMass);
    }

    [Fact]
    public void Load_AcceptsABoundaryStatus_ButDoesNotEnableIt()
    {
        var model = Source();
        Edit(model + ".calibration.json", doc => doc["status"] = ModelCalibrationContract.BoundaryStatus);
        var calibration = LoadCalibration(model);
        Assert.Equal(ModelCalibrationContract.BoundaryStatus, calibration.Status);
        Assert.False(calibration.IsEnabled);
        Assert.True(ModelCalibrationLimits.IsWithinHardBand(calibration.Temperature));
    }

    [Theory]
    [InlineData("temperature_low")]
    [InlineData("temperature_high")]
    [InlineData("method")]
    [InlineData("epsilon")]
    [InlineData("status")]
    [InlineData("schema")]
    [InlineData("class_order")]
    [InlineData("samples")]
    [InlineData("bins")]
    [InlineData("periods_empty")]
    [InlineData("population")]
    [InlineData("feature_hash")]
    [InlineData("model_hash")]
    [InlineData("nll_negative")]
    [InlineData("ece_range")]
    [InlineData("null_evaluation")]
    [InlineData("unknown_property")]
    [InlineData("equal_mass_range")]
    [InlineData("reliability_length")]
    [InlineData("reliability_total")]
    [InlineData("reliability_empty_with_values")]
    [InlineData("reliability_filled_without_values")]
    [InlineData("reliability_value_range")]
    [InlineData("nll_definition")]
    [InlineData("evaluation_population")]
    [InlineData("evaluation_not_oos")]
    public void Load_RejectsEveryContradiction(string fault)
    {
        var model = Source();
        Edit(model + ".calibration.json", doc =>
        {
            switch (fault)
            {
                case "temperature_low": doc["temperature"] = 0.001; break;
                case "temperature_high": doc["temperature"] = 1000.0; break;
                case "method": doc["method"] = "platt"; break;
                case "epsilon": doc["epsilon"] = 1e-9; break;
                case "status": doc["status"] = "insufficient_samples"; break;
                case "schema": doc["schema_version"] = 2; break;
                case "class_order": doc["class_order"] = "Up,Neutral,Down"; break;
                case "samples": doc["fit"]!["sample_count"] = 50; break;
                case "bins": doc["fit"]!["ece_bins"] = 20; break;
                case "periods_empty": doc["fit"]!["periods"] = new JsonArray(); break;
                case "population": doc["fit"]!["population"] = "validation"; break;
                case "feature_hash": doc["feature_contract_hash"] = new string('0', 64); break;
                case "model_hash": doc["model_sha256"] = new string('0', 64); break;
                case "nll_negative": doc["evaluation"]!["outer_test"]!["after"]!["nll"] = -1.0; break;
                case "ece_range": doc["evaluation"]!["outer_test"]!["after"]!["ece"] = 1.5; break;
                case "null_evaluation": doc["evaluation"] = null; break;
                case "unknown_property": doc["extra"] = 1; break;
                case "equal_mass_range": doc["evaluation"]!["outer_test"]!["before"]!["ece_equal_mass"] = 1.5; break;
                case "reliability_length": doc["evaluation"]!["outer_test"]!["reliability"]!["after"]!.AsArray().RemoveAt(0); break;
                case "reliability_total":
                    var counted = doc["evaluation"]!["outer_test"]!["reliability"]!["before"]!.AsArray().First(b => b!["count"]!.GetValue<int>() > 0)!;
                    counted["count"] = counted["count"]!.GetValue<int>() + 1;
                    break;
                case "reliability_empty_with_values":
                    var empty = doc["evaluation"]!["outer_test"]!["reliability"]!["after"]!.AsArray().First(b => b!["count"]!.GetValue<int>() == 0)!;
                    empty["mean_confidence"] = 0.5;
                    break;
                case "reliability_filled_without_values":
                    doc["evaluation"]!["outer_test"]!["reliability"]!["after"]!.AsArray()
                        .First(b => b!["count"]!.GetValue<int>() > 0)!["accuracy"] = null;
                    break;
                case "reliability_value_range":
                    doc["evaluation"]!["outer_test"]!["reliability"]!["after"]!.AsArray()
                        .First(b => b!["count"]!.GetValue<int>() > 0)!["mean_confidence"] = 1.5;
                    break;
                case "nll_definition": doc["nll_definition"] = "natural_log_unclamped"; break;
                case "evaluation_population": doc["evaluation_population"] = "calibration"; break;
                case "evaluation_not_oos": doc["evaluation_is_oos"] = false; break;
            }
        });
        Assert.Throws<InvalidDataException>(() => LoadCalibration(model));
    }

    [Fact]
    public void Load_RejectsWrongModelHashDataRevisionReferenceAndMissingSidecar()
    {
        var model = Source();
        var metadata = Metadata(model);
        Assert.Throws<InvalidDataException>(() => ModelCalibration.Load(model, metadata, new string('0', 64), DataRevision));
        Assert.Throws<InvalidDataException>(() => ModelCalibration.Load(model, metadata, Hash(model), new string('c', 64)));
        var wrongReference = new Dictionary<string, string>(metadata)
        { [ModelCalibrationContract.ReferenceKey] = "other.onnx.calibration.json" };
        Assert.Throws<InvalidDataException>(() => ModelCalibration.Load(model, wrongReference, Hash(model), DataRevision));
        File.Delete(model + ".calibration.json");
        Assert.Throws<FileNotFoundException>(() => ModelCalibration.Load(model, metadata, Hash(model), DataRevision));
    }

    [Fact]
    public void Load_RejectsMalformedJson()
    {
        var model = Source();
        File.WriteAllText(model + ".calibration.json", "{ not json");
        Assert.Throws<InvalidDataException>(() => LoadCalibration(model));
    }

    // ---------------------------------------------------------------- explanation contract

    [Fact]
    public void ExplanationConstants_MatchPythonParityFixture_AndTargetHashMatches()
    {
        using var parity = Parity("ModelExplanationParity.json");
        var root = parity.RootElement;
        Assert.Equal(ModelExplanationContract.SchemaVersion, root.GetProperty("schema_version").GetInt32());
        Assert.Equal(ModelExplanationContract.OutputSpace, root.GetProperty("output_space").GetString());
        Assert.Equal(ModelExplanationContract.Method, root.GetProperty("method").GetString());
        Assert.Equal(ModelExplanationContract.Seed, root.GetProperty("seed").GetInt32());
        Assert.Equal(ModelExplanationContract.BackgroundSize, root.GetProperty("background_size").GetInt32());
        Assert.Equal(ModelExplanationContract.RepresentativePerClass, root.GetProperty("representative_per_class").GetInt32());
        Assert.Equal(ModelExplanationContract.RepresentativeLatest, root.GetProperty("representative_latest").GetInt32());
        Assert.Equal(ModelExplanationContract.PdpGridPoints, root.GetProperty("pdp_grid_points").GetInt32());
        Assert.Equal(ModelExplanationContract.PdpSemantics, root.GetProperty("pdp_semantics").GetString());
        Assert.Equal(ModelExplanationContract.ExplanationType, root.GetProperty("explanation_type").GetString());
        Assert.Equal(ModelExplanationContract.RngPolicy, root.GetProperty("rng_policy").GetString());
        Assert.Equal(ModelExplanationContract.ReasonLatest, root.GetProperty("reason_latest").GetString());
        Assert.Equal(ModelExplanationContract.ReasonTopPrefix, root.GetProperty("reason_top_prefix").GetString());
        Assert.Equal(ModelExplanationLimits.MaxModelCalls, root.GetProperty("max_model_calls").GetInt32());
        var target = root.GetProperty("target_contract");
        var metadata = target.GetProperty("metadata").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
        Assert.Equal(target.GetProperty("hash").GetString(), ModelExplanation.ComputeTargetContractHash(metadata));
    }

    [Fact]
    public void ExplanationFixture_LoadsAndSatisfiesEfficiencyInProbabilitySpace()
    {
        var model = Source();
        var explanation = ModelExplanation.Load(model, Metadata(model), Hash(model));
        Assert.NotNull(explanation);
        Assert.True(explanation!.IsAvailable);
        Assert.Equal(ModelExplanationContract.BackgroundSize, explanation.BackgroundSize);
        Assert.Equal(new[] { "Up", "Down", "Neutral" }, explanation.Classes);
        Assert.InRange(explanation.Records.Length, 1, 3 * ModelExplanationContract.RepresentativePerClass
            + ModelExplanationContract.RepresentativeLatest);
        foreach (var record in explanation.Records)
        foreach (var output in record.Outputs)
        {
            Assert.Equal(output.Prediction, output.BaseValue + output.Contributions.Sum(c => c.Value), 9);
            Assert.True(output.Top().Length <= ModelExplanationContract.TopCount);
        }
        Assert.Equal(explanation.BaseChannels.Length, explanation.Pdp!.Channels.Length);
    }

    [Fact]
    public void ExplanationFixture_CarriesSamplingReliabilityAndInputShiftSupport()
    {
        var model = Source();
        var explanation = ModelExplanation.Load(model, Metadata(model), Hash(model))!;
        Assert.Equal(ModelExplanationContract.ExplanationType, explanation.ExplanationType);
        Assert.Equal(ModelExplanationContract.RngPolicy, explanation.RngPolicy);
        var contributions = explanation.Records.SelectMany(r => r.Outputs).SelectMany(o => o.Contributions).ToArray();
        Assert.All(contributions, c => Assert.True(c.StandardError is >= 0));
        Assert.All(explanation.Records, r => Assert.True(r.StabilityHalfDelta is >= 0));
        var stability = explanation.Stability!;
        Assert.Equal(explanation.Permutations, stability.PermutationsUsed);
        Assert.Equal(contributions.Max(c => c.StandardError!.Value), stability.MaxStandardError);
        Assert.Equal(explanation.Records.Max(r => r.StabilityHalfDelta!.Value), stability.MaxHalfDelta);
        foreach (var channel in explanation.Pdp!.Channels)
        {
            Assert.Equal(explanation.Pdp.GridPoints, channel.OutOfSupportRate.Length);
            Assert.Equal(0.0, channel.OutOfSupportRate[explanation.Pdp.GridPoints / 2]); // the median offset is zero
        }
    }

    [Fact]
    public void ExplanationLoad_AcceptsASidecarWithoutTheOptionalReliabilityFields()
    {
        var model = Source();
        Edit(model + ".explain.json", doc =>
        {
            var root = doc.AsObject();
            foreach (var key in new[] { "stability", "rng_policy", "explanation_type" }) root.Remove(key);
            foreach (var record in root["records"]!.AsArray())
            {
                record!.AsObject().Remove("stability_half_delta");
                foreach (var output in record["outputs"]!.AsArray())
                foreach (var contribution in output!["contributions"]!.AsArray()) contribution!.AsObject().Remove("standard_error");
            }
            foreach (var channel in root["pdp"]!["channels"]!.AsArray()) channel!.AsObject().Remove("out_of_support_rate");
        });
        var explanation = ModelExplanation.Load(model, Metadata(model), Hash(model))!;
        Assert.True(explanation.IsAvailable);
        Assert.Null(explanation.Stability);
        Assert.Null(explanation.ExplanationType);
        Assert.All(explanation.Records, r => Assert.Null(r.StabilityHalfDelta));
        Assert.All(explanation.Records.SelectMany(r => r.Outputs).SelectMany(o => o.Contributions), c => Assert.Null(c.StandardError));
        Assert.All(explanation.Pdp!.Channels, c => Assert.True(c.OutOfSupportRate.IsDefault));
    }

    [Fact]
    public void Top_OrdersByMagnitudeThenIndex_KeepsSign_AndReturnsAllWhenShort()
    {
        var values = new[] { 0.1, -0.5, 0.5, 0.0, -0.1, 0.3, 0.05 };
        var output = new ExplanationOutput("Up", 0.3, 0.3, values.Select((v, i) => new ExplanationContribution("c" + i, v)).ToImmutableArray());
        Assert.Equal(new[] { "c1", "c2", "c5", "c0", "c4" }, output.Top().Select(c => c.Channel));
        Assert.Equal(-0.5, output.Top()[0].Value);
        Assert.Equal(7, output.Top(10).Length);
        Assert.Empty(output.Top(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => output.Top(-1));
        var two = new ExplanationOutput("Up", 0, 0, ImmutableArray.Create(new ExplanationContribution("a", 0.2), new ExplanationContribution("b", -0.2)));
        Assert.Equal(new[] { "a", "b" }, two.Top().Select(c => c.Channel));
    }

    [Theory]
    [InlineData("model")]
    [InlineData("feature_hash")]
    [InlineData("target_hash")]
    [InlineData("identity")]
    [InlineData("class_order")]
    [InlineData("pdp_grid")]
    [InlineData("status")]
    [InlineData("method")]
    [InlineData("seed")]
    [InlineData("budget")]
    [InlineData("records_empty")]
    [InlineData("population")]
    [InlineData("channel_order")]
    [InlineData("pdp_probability")]
    [InlineData("unknown_property")]
    [InlineData("negative_standard_error")]
    [InlineData("negative_half_delta")]
    [InlineData("stability_permutations")]
    [InlineData("stability_negative")]
    [InlineData("rng_policy")]
    [InlineData("explanation_type")]
    [InlineData("out_of_support_length")]
    [InlineData("out_of_support_range")]
    public void ExplanationLoad_RejectsEveryContradiction(string fault)
    {
        var model = Source();
        Edit(model + ".explain.json", doc =>
        {
            switch (fault)
            {
                case "model": doc["model_sha256"] = new string('0', 64); break;
                case "feature_hash": doc["feature_contract_hash"] = new string('0', 64); break;
                case "target_hash": doc["target_contract_hash"] = new string('0', 64); break;
                case "identity": doc["records"]![0]!["outputs"]![0]!["contributions"]![0]!["value"] = 5.0; break;
                case "class_order": doc["records"]![0]!["outputs"]![0]!["output_id"] = "Down"; break;
                case "pdp_grid": doc["pdp"]!["grid_points"] = 5; break;
                case "status": doc["status"] = "bogus"; break;
                case "method": doc["method"] = "kernel_shap"; break;
                case "seed": doc["seed"] = 7; break;
                case "budget": doc["model_calls"] = 3_000_000; break;
                case "records_empty": doc["records"] = new JsonArray(); break;
                case "population": doc["population"]!["background"] = "validation"; break;
                case "channel_order":
                    var contributions = doc["records"]![0]!["outputs"]![0]!["contributions"]!.AsArray();
                    (contributions[0]!["channel"], contributions[1]!["channel"]) =
                        (contributions[1]!["channel"]!.DeepClone(), contributions[0]!["channel"]!.DeepClone());
                    break;
                case "pdp_probability": doc["pdp"]!["channels"]![0]!["values"]!["Up"]![0] = 1.5; break;
                case "unknown_property": doc["extra"] = 1; break;
                case "negative_standard_error": doc["records"]![0]!["outputs"]![0]!["contributions"]![0]!["standard_error"] = -0.001; break;
                case "negative_half_delta": doc["records"]![0]!["stability_half_delta"] = -1.0; break;
                case "stability_permutations": doc["stability"]!["permutations_used"] = 0; break;
                case "stability_negative": doc["stability"]!["max_standard_error"] = -1.0; break;
                case "rng_policy": doc["rng_policy"] = "shared_stream"; break;
                case "explanation_type": doc["explanation_type"] = "kernel_shap"; break;
                case "out_of_support_length": doc["pdp"]!["channels"]![0]!["out_of_support_rate"]!.AsArray().RemoveAt(0); break;
                case "out_of_support_range": doc["pdp"]!["channels"]![0]!["out_of_support_rate"]![0] = 1.5; break;
            }
        });
        var metadata = Metadata(model);
        Assert.Throws<InvalidDataException>(() => ModelExplanation.Load(model, metadata, Hash(model)));
    }

    [Fact]
    public void ExplanationLoad_AbsentFileIsNullAndHeaderOnlyStatusesAreValid()
    {
        var model = Source(".explain.json");
        Assert.Null(ModelExplanation.Load(model, Metadata(model), Hash(model)));

        model = Source();
        Edit(model + ".explain.json", doc =>
        {
            var node = doc.AsObject();
            foreach (var key in new[] { "records", "pdp", "classes", "population", "output_space", "method", "seed",
                         "permutations", "background_size", "model_calls" }) node.Remove(key);
            node["status"] = ModelExplanationContract.NoBackgroundStatus;
        });
        var loaded = ModelExplanation.Load(model, Metadata(model), Hash(model));
        Assert.False(loaded!.IsAvailable);
        Assert.True(loaded.Records.IsDefaultOrEmpty);

        // a non-available status must not carry records
        model = Source();
        Edit(model + ".explain.json", doc => doc["status"] = ModelExplanationContract.NoBackgroundStatus);
        Assert.Throws<InvalidDataException>(() => ModelExplanation.Load(model, Metadata(model), Hash(model)));
    }

    // ---------------------------------------------------------------- registry and service

    private async Task<(ModelGenerationRegistry Registry, ModelGenerationManifest Manifest)> RegisterAsync(string model)
    {
        var registry = new ModelGenerationRegistry(Path.Combine(_directory, "registry_" + Guid.NewGuid().ToString("N")));
        return (registry, await registry.RegisterAsync(model));
    }

    [Fact]
    public async Task Registry_RequiresCalibration_DetectsTampering_AndTreatsExplanationAsOptional()
    {
        var model = Source();
        var (registry, manifest) = await RegisterAsync(model);
        using (registry)
        {
            Assert.Contains(manifest.RequiredSidecars, s => s.Kind == ModelCalibrationContract.SidecarKind);
            Assert.DoesNotContain(manifest.RequiredSidecars, s => s.File.EndsWith(".explain.json", StringComparison.Ordinal));
            using (var lease = registry.Acquire(manifest.ModelId))
            {
                Assert.True(File.Exists(lease!.ModelPath + ".explain.json"));
                // the explanation is outside the model identity: tampering never makes the generation corrupt
                File.AppendAllText(lease.ModelPath + ".explain.json", " ");
                using (var second = registry.Acquire(manifest.ModelId)) Assert.NotNull(second); // a lease holds a store file until disposed
                File.AppendAllText(lease.ModelPath + ".calibration.json", " ");
            }
            Assert.Throws<InvalidDataException>(() => registry.Acquire(manifest.ModelId));
        }
    }

    [Fact]
    public async Task Registry_RejectsMissingAndContradictoryCalibration()
    {
        var missing = Source(".calibration.json");
        using (var registry = new ModelGenerationRegistry(Path.Combine(_directory, "r1")))
        {
            await Assert.ThrowsAsync<FileNotFoundException>(() => registry.RegisterAsync(missing));
            Assert.Empty(registry.List());
        }
        var stale = Source();
        Edit(stale + ".calibration.json", doc => doc["data_revision"] = new string('c', 64));
        using (var registry = new ModelGenerationRegistry(Path.Combine(_directory, "r2")))
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => registry.RegisterAsync(stale));
            Assert.Empty(registry.List());
        }
    }

    [Fact]
    public async Task Registry_RegistersWithoutExplanation_AndAddsItWhenTheSameGenerationIsRegisteredAgain()
    {
        var withoutExplanation = Source(".explain.json");
        var (registry, manifest) = await RegisterAsync(withoutExplanation);
        using (registry)
        {
            using var lease = registry.Acquire(manifest.ModelId);
            Assert.False(File.Exists(lease!.ModelPath + ".explain.json"));
            var again = await registry.RegisterAsync(Source());
            Assert.Equal(manifest.ModelId, again.ModelId);
            Assert.True(File.Exists(lease.ModelPath + ".explain.json"));
        }
    }

    [Fact]
    public async Task Service_ReportsExplanationAndCalibrationStates()
    {
        var model = Source();
        var (registry, manifest) = await RegisterAsync(model);
        using (registry)
        {
            var service = new ModelExplainabilityService(registry);
            var explanation = await service.LoadExplanationAsync(manifest.ModelId);
            Assert.Equal(ExplanationAvailability.Available, explanation.Availability);
            Assert.Same(explanation.Explanation, (await service.LoadExplanationAsync(manifest.ModelId)).Explanation);
            var calibration = await service.LoadCalibrationAsync(manifest.ModelId);
            Assert.Equal(CalibrationAvailability.Active, calibration.Availability);
            Assert.NotNull(calibration.Calibration);

            using var lease = registry.Acquire(manifest.ModelId);
            var fresh = new ModelExplainabilityService(registry);
            File.WriteAllText(lease!.ModelPath + ".explain.json", "{ not json");
            Assert.Equal(ExplanationAvailability.Invalid, (await fresh.LoadExplanationAsync(manifest.ModelId)).Availability);
            File.Delete(lease.ModelPath + ".explain.json");
            Assert.Equal(ExplanationAvailability.NotGenerated, (await new ModelExplainabilityService(registry)
                .LoadExplanationAsync(manifest.ModelId)).Availability);
            Assert.Equal(ExplanationAvailability.NotGenerated, (await service.LoadExplanationAsync("missing-id")).Availability);
        }
    }

    [Fact]
    public async Task Service_ReportsAFailedExplanation_OnlyWhenTheRunMetricsRecordIt()
    {
        var failed = Source(".explain.json");
        Edit(failed + ".metrics.json", node => node[ModelExplanationContract.MetricsKey] = new JsonObject
        {
            ["status"] = ModelExplanationContract.GenerationFailedStatus, ["error"] = "ValueError",
        });
        var (registry, manifest) = await RegisterAsync(failed);
        using (registry)
        {
            var load = await new ModelExplainabilityService(registry).LoadExplanationAsync(manifest.ModelId);
            Assert.Equal(ExplanationAvailability.Failed, load.Availability);
            Assert.Null(load.Explanation);
        }

        var (plainRegistry, plain) = await RegisterAsync(Source(".explain.json")); // no explanation and nothing recorded
        using (plainRegistry)
        {
            Assert.Equal(ExplanationAvailability.NotGenerated,
                (await new ModelExplainabilityService(plainRegistry).LoadExplanationAsync(plain.ModelId)).Availability);
        }
    }

    [Fact]
    public void ExplanationLimits_BoundTheCachedGenerations()
    {
        Assert.Equal(32, ModelExplanationLimits.MaxCachedGenerations);
    }

    [Fact]
    public async Task Service_KeepsAtMostTheBoundedNumberOfGenerations_EvictingTheLeastRecentlyUsed()
    {
        var (registry, manifest) = await RegisterAsync(Source());
        using (registry)
        {
            var counting = new CountingRegistry(registry, manifest.ModelId);
            var service = new ModelExplainabilityService(counting, cacheCapacity: 2);
            var a = (await service.LoadExplanationAsync("alias-a")).Explanation;
            var b = (await service.LoadExplanationAsync("alias-b")).Explanation;
            Assert.Equal(2, counting.Acquires);
            Assert.Same(a, (await service.LoadExplanationAsync("alias-a")).Explanation); // a is now more recent than b
            Assert.Equal(2, counting.Acquires);

            await service.LoadExplanationAsync("alias-c"); // evicts b, the least recently used
            Assert.Equal(3, counting.Acquires);
            Assert.Same(a, (await service.LoadExplanationAsync("alias-a")).Explanation); // still cached
            Assert.Equal(3, counting.Acquires);
            var reloaded = (await service.LoadExplanationAsync("alias-b")).Explanation; // read again
            Assert.Equal(4, counting.Acquires);
            Assert.NotSame(b, reloaded);
            Assert.NotNull(reloaded);
        }
    }

    [Fact]
    public async Task Service_BoundsTheCalibrationCacheToo()
    {
        var (registry, manifest) = await RegisterAsync(Source());
        using (registry)
        {
            var counting = new CountingRegistry(registry, manifest.ModelId);
            var service = new ModelExplainabilityService(counting, cacheCapacity: 1);
            var a = (await service.LoadCalibrationAsync("alias-a")).Calibration;
            Assert.Same(a, (await service.LoadCalibrationAsync("alias-a")).Calibration);
            Assert.Equal(1, counting.Acquires);
            await service.LoadCalibrationAsync("alias-b"); // evicts a
            Assert.Equal(2, counting.Acquires);
            Assert.NotSame(a, (await service.LoadCalibrationAsync("alias-a")).Calibration);
            Assert.Equal(3, counting.Acquires);
        }
    }

    [Fact]
    public async Task Service_SharesOneLoadAmongConcurrentRequestsForTheSameGeneration()
    {
        var (registry, manifest) = await RegisterAsync(Source());
        using (registry)
        {
            var counting = new CountingRegistry(registry, manifest.ModelId);
            var service = new ModelExplainabilityService(counting);
            var requests = Enumerable.Range(0, 6).Select(_ => service.LoadExplanationAsync("gated-x")).ToArray();
            Assert.True(counting.Entered.Wait(TimeSpan.FromSeconds(30)));
            counting.Gate.Set(); // every request joined the load held at the gate before it is released
            var loads = await Task.WhenAll(requests);
            Assert.Equal(1, counting.Acquires);
            Assert.All(loads, load => Assert.Equal(ExplanationAvailability.Available, load.Availability));
            Assert.All(loads, load => Assert.Same(loads[0].Explanation, load.Explanation));
        }
    }

    [Fact]
    public async Task Service_CancellingOneRequest_DoesNotCancelTheSharedLoad()
    {
        var (registry, manifest) = await RegisterAsync(Source());
        using (registry)
        {
            var counting = new CountingRegistry(registry, manifest.ModelId);
            var service = new ModelExplainabilityService(counting);
            using var cts = new CancellationTokenSource();
            var cancelled = service.LoadExplanationAsync("gated-y", cts.Token);
            var patient = service.LoadExplanationAsync("gated-y");
            Assert.True(counting.Entered.Wait(TimeSpan.FromSeconds(30)));
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
            counting.Gate.Set();
            Assert.Equal(ExplanationAvailability.Available, (await patient).Availability);
            Assert.Equal(1, counting.Acquires);

            using var already = new CancellationTokenSource();
            already.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.LoadExplanationAsync("gated-y", already.Token));
        }
    }

    [Fact]
    public async Task Service_NegativeCachesFailedOrEmptyLoad_AndDoesNotReacquire()
    {
        var (registry, manifest) = await RegisterAsync(Source(".explain.json"));
        using (registry)
        {
            var counting = new CountingRegistry(registry, manifest.ModelId);
            var service = new ModelExplainabilityService(counting);
            Assert.Equal(ExplanationAvailability.NotGenerated, (await service.LoadExplanationAsync("alias-n")).Availability);
            Assert.Equal(ExplanationAvailability.NotGenerated, (await service.LoadExplanationAsync("alias-n")).Availability);
            Assert.Equal(1, counting.Acquires); // Negative caching: the second request is served from cache
        }
    }

    [Fact]
    public async Task Service_NegativeCachesInvalidExplanation_AndDoesNotReacquire()
    {
        var model = Source();
        File.WriteAllText(model + ".explain.json", "invalid json content");
        var (registry, manifest) = await RegisterAsync(model);
        using (registry)
        {
            var counting = new CountingRegistry(registry, manifest.ModelId);
            var service = new ModelExplainabilityService(counting);
            var first = await service.LoadExplanationAsync(manifest.ModelId);
            Assert.Equal(ExplanationAvailability.Invalid, first.Availability);
            var second = await service.LoadExplanationAsync(manifest.ModelId);
            Assert.Equal(ExplanationAvailability.Invalid, second.Availability);
            Assert.Equal(1, counting.Acquires); // Served from negative cache
        }
    }

    [Fact]
    public async Task Service_NegativeCachesUnavailableCalibration_AndDoesNotReacquire()
    {
        string Plain(string status)
        {
            var folder = Path.Combine(_directory, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            var model = Path.Combine(folder, "analysis_fixture.onnx");
            foreach (var suffix in new[] { "", ".metrics.json", ".analysis.json" })
                File.Copy(Path.Combine("Assets", "analysis_fixture.onnx") + suffix, model + suffix);
            if (status.Length > 0)
                Edit(model + ".metrics.json", doc => doc["calibration"] = new JsonObject { ["status"] = status });
            return model;
        }

        var (registry, manifest) = await RegisterAsync(Plain("insufficient_samples"));
        using (registry)
        {
            var counting = new CountingRegistry(registry, manifest.ModelId);
            var service = new ModelExplainabilityService(counting);
            var first = await service.LoadCalibrationAsync(manifest.ModelId);
            Assert.Equal(CalibrationAvailability.Unavailable, first.Availability);
            var second = await service.LoadCalibrationAsync(manifest.ModelId);
            Assert.Equal(CalibrationAvailability.Unavailable, second.Availability);
            Assert.Equal(1, counting.Acquires); // Served from negative cache
        }
    }

    [Fact]
    public async Task Service_ReportsABoundaryCalibrationAsDisabled_AlsoFromTheCache()
    {
        var model = Source();
        Edit(model + ".calibration.json", doc => doc["status"] = ModelCalibrationContract.BoundaryStatus);
        var (registry, manifest) = await RegisterAsync(model);
        using (registry)
        {
            var service = new ModelExplainabilityService(registry);
            for (int attempt = 0; attempt < 2; attempt++) // the second read is served from the cache
            {
                var load = await service.LoadCalibrationAsync(manifest.ModelId);
                Assert.Equal(CalibrationAvailability.Disabled, load.Availability);
                Assert.Equal(ModelCalibrationContract.BoundaryStatus, load.Reason);
                Assert.False(load.Calibration!.IsEnabled);
            }
        }
    }

    [Fact]
    public async Task Service_ReportsRequestedButUnavailableCalibration_AndNoneWhenNeverRequested()
    {
        // an ONNX that declares no calibration reference, whose run recorded why calibration was not applied
        string Plain(string status)
        {
            var folder = Path.Combine(_directory, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            var model = Path.Combine(folder, "analysis_fixture.onnx");
            foreach (var suffix in new[] { "", ".metrics.json", ".analysis.json" })
                File.Copy(Path.Combine("Assets", "analysis_fixture.onnx") + suffix, model + suffix);
            if (status.Length > 0)
                Edit(model + ".metrics.json", doc => doc["calibration"] = new JsonObject { ["status"] = status });
            return model;
        }
        var (requestedRegistry, requested) = await RegisterAsync(Plain("insufficient_samples"));
        using (requestedRegistry)
        {
            var load = await new ModelExplainabilityService(requestedRegistry).LoadCalibrationAsync(requested.ModelId);
            Assert.Equal(CalibrationAvailability.Unavailable, load.Availability);
            Assert.Equal("insufficient_samples", load.Reason);
            Assert.Null(load.Calibration);
        }
        var (plainRegistry, plain) = await RegisterAsync(Plain(""));
        using (plainRegistry)
        {
            var load = await new ModelExplainabilityService(plainRegistry).LoadCalibrationAsync(plain.ModelId);
            Assert.Equal(CalibrationAvailability.None, load.Availability);
        }
    }

    // ---------------------------------------------------------------- prediction

    private static CandleData[] Candles(int count) => Enumerable.Range(0, count)
        .Select(i => new CandleData(new DateTime(2024, 1, 1).AddDays(i), 100m, 105m, 95m, 100m + i * 0.5m + (i % 3), 1000))
        .ToArray();

    private static float[] RawProbabilities(string model, CandleData[] candles)
    {
        var tensor = new float[10 * 5];
        new MLDataProcessor().NormalizeCandles(candles, candles.Length - 10, 10, tensor);
        using var session = new InferenceSession(model);
        using var input = OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance, tensor.AsMemory(), new long[] { 1, 10, 5 });
        using var results = session.Run(new RunOptions(), new[] { "input" }, new[] { input }, new[] { "output" });
        return results[0].GetTensorDataAsSpan<float>().ToArray();
    }

    private static int Argmax(IReadOnlyList<float> values)
    {
        int best = 0;
        for (int i = 1; i < values.Count; i++) if (values[i] > values[best]) best = i;
        return best;
    }

    [Fact]
    public async Task PredictAsync_AppliesTheCalibrationAfterTheSoftmax_AndMarksTheConfidenceCalibrated()
    {
        var model = Source();
        var candles = Candles(20);
        var raw = RawProbabilities(model, candles);
        var expected = new float[3];
        LoadCalibration(model).Apply(raw, expected);
        using var service = new PredictionService(
            new PredictionServiceTests.TestSettings(model, predictionWindowSize: 10), new MLDataProcessor());
        var result = await service.PredictAsync(candles);
        Assert.False(result.IsFallback);
        Assert.Equal(ConfidenceType.CalibratedProbability, result.ConfidenceType);
        for (int i = 0; i < 3; i++) Assert.Equal(expected[i], result.Scores[i].Score, 5);
        Assert.Equal(Argmax(raw), Argmax(result.Scores.Select(s => s.Score).ToArray()));
        Assert.Equal(expected.Max(), result.Confidence, 5);
        Assert.NotEqual(raw[Argmax(raw)], expected[Argmax(raw)], 3); // the temperature really changed the distribution
    }

    [Fact]
    public async Task PredictAsync_ADeclaredButMissingOrMismatchedCalibrationFailsClosed()
    {
        var missing = Source(".calibration.json");
        using (var service = new PredictionService(
                   new PredictionServiceTests.TestSettings(missing, predictionWindowSize: 10), new MLDataProcessor()))
            Assert.True((await service.PredictAsync(Candles(20))).IsFallback);

        var mismatched = Source();
        Edit(mismatched + ".calibration.json", doc => doc["model_sha256"] = new string('0', 64));
        using (var service = new PredictionService(
                   new PredictionServiceTests.TestSettings(mismatched, predictionWindowSize: 10), new MLDataProcessor()))
            Assert.True((await service.PredictAsync(Candles(20))).IsFallback);
    }

    [Fact]
    public async Task PredictAsync_AModelWithoutACalibrationReferenceKeepsTheRawClassProbability()
    {
        var plain = Path.Combine("Assets", "trend_predictor_goodmeta.onnx");
        using var service = new PredictionService(
            new PredictionServiceTests.TestSettings(plain, predictionWindowSize: 10), new MLDataProcessor());
        var result = await service.PredictAsync(Candles(20));
        Assert.False(result.IsFallback);
        Assert.Equal(ConfidenceType.ClassProbability, result.ConfidenceType);
    }

    [Fact]
    public async Task PredictAsync_ABoundaryCalibrationIsNotApplied_AndTheConfidenceStaysTheClassProbability()
    {
        var model = Source();
        Edit(model + ".calibration.json", doc => doc["status"] = ModelCalibrationContract.BoundaryStatus);
        var candles = Candles(20);
        var raw = RawProbabilities(model, candles);
        using var service = new PredictionService(
            new PredictionServiceTests.TestSettings(model, predictionWindowSize: 10), new MLDataProcessor());
        var result = await service.PredictAsync(candles);
        Assert.False(result.IsFallback);
        Assert.Equal(ConfidenceType.ClassProbability, result.ConfidenceType);
        for (int i = 0; i < 3; i++) Assert.Equal(raw[i], result.Scores[i].Score, 5);
    }

    private sealed class FixedEnsembleSettings(EnsembleSpec? spec) : IEnsembleSettingsManager
    {
        public EnsembleSpec? Current { get; } = spec;
        public Task LoadAsync() => Task.CompletedTask;
        public Task SaveAsync(EnsembleSpec? value) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Ensemble_CalibratesEachMemberBeforeTheLinearMixture()
    {
        var model = Source();
        var settings = new PredictionServiceTests.TestSettings(model, predictionWindowSize: 10);
        var (registry, manifest) = await RegisterAsync(model);
        using (registry)
        {
            var candles = Candles(20);
            var raw = RawProbabilities(model, candles);
            var expected = new float[3];
            LoadCalibration(model).Apply(raw, expected);
            using var single = new PredictionService(settings, new MLDataProcessor());
            var spec = new EnsembleSpec(new[] { new EnsembleMember(manifest.ModelId, 1.0) });
            using var ensemble = new EnsemblePredictionService(single, new FixedEnsembleSettings(spec), registry,
                settings, new MLDataProcessor());
            var result = await ensemble.PredictAsync(candles);
            Assert.False(result.IsFallback);
            for (int i = 0; i < 3; i++) Assert.Equal(expected[i], result.Scores[i].Score, 4);
            Assert.NotEqual(raw[Argmax(raw)], result.Scores[Argmax(raw)].Score, 3);
        }
    }

    [Fact]
    public async Task Ensemble_ABoundaryCalibrationOfAMemberIsNotApplied()
    {
        var model = Source();
        Edit(model + ".calibration.json", doc => doc["status"] = ModelCalibrationContract.BoundaryStatus);
        var settings = new PredictionServiceTests.TestSettings(model, predictionWindowSize: 10);
        var (registry, manifest) = await RegisterAsync(model);
        using (registry)
        {
            var candles = Candles(20);
            var raw = RawProbabilities(model, candles);
            using var single = new PredictionService(settings, new MLDataProcessor());
            var spec = new EnsembleSpec(new[] { new EnsembleMember(manifest.ModelId, 1.0) });
            using var ensemble = new EnsemblePredictionService(single, new FixedEnsembleSettings(spec), registry,
                settings, new MLDataProcessor());
            var result = await ensemble.PredictAsync(candles);
            Assert.False(result.IsFallback);
            for (int i = 0; i < 3; i++) Assert.Equal(raw[i], result.Scores[i].Score, 5);
        }
    }

    // ---------------------------------------------------------------- training configuration

    private static TrainingJobConfig Config(bool calibration, TargetType target = TargetType.Classification) => new()
    {
        Symbols = new[] { "AAA" }, Architecture = "lstm", WindowSize = 30, Horizon = 5,
        Calibration = calibration, TargetType = target,
    };

    [Fact]
    public void TrainingJobConfig_CalibrationIsOptInClassificationOnly_AndRoundTrips()
    {
        Config(calibration: false).Validate();
        Config(calibration: true).Validate();
        Assert.Throws<InvalidOperationException>(() =>
            Config(calibration: true, target: TargetType.Regression).Validate());
        Assert.True(JsonDocument.Parse(TrainingConfigJson.Serialize(Config(true))).RootElement
            .TryGetProperty("calibration", out var flag) && flag.GetBoolean());
    }

    public void Dispose()
    {
        TestDirectory.Delete(_directory);
    }
}
