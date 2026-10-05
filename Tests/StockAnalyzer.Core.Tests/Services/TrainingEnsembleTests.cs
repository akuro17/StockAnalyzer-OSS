#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// T13 extended learning on the C# side: the config mirror, the verified ensemble manifest, the registration that turns it into an
/// <see cref="EnsembleSpec"/>, and the mixture parity with the Python trainer. The manifests and member generations under
/// <c>Assets/ensemble_fixture_*</c> are written by the production Python code (<c>generate_ensemble_fixture.py</c>).
/// </summary>
[Collection("Non-Parallel ONNX Tests")]
public sealed class TrainingEnsembleTests : IDisposable
{
    private const string Stem = "ensemble_fixture";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sa_t13_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static string SolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "StockAnalyzer.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return directory!.FullName;
    }

    /// <summary>A private copy of the fixture bundle (both members + their metrics + both manifests).</summary>
    private string Bundle()
    {
        var folder = Path.Combine(_directory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        foreach (var name in new[]
        {
            $"{Stem}_m01.onnx", $"{Stem}_m01.onnx.metrics.json", $"{Stem}_m02.onnx", $"{Stem}_m02.onnx.metrics.json",
            $"{Stem}_bagging.ensemble.json", $"{Stem}_stacking.ensemble.json",
        })
            File.Copy(Path.Combine("Assets", name), Path.Combine(folder, name));
        return folder;
    }

    private static string Manifest(string folder, string method) => Path.Combine(folder, $"{Stem}_{method}.ensemble.json");

    private static void Edit(string path, Action<JsonNode> change)
    {
        var node = JsonNode.Parse(File.ReadAllText(path))!;
        change(node);
        File.WriteAllText(path, node.ToJsonString());
    }

    private static TrainingJobConfig Job(Func<TrainingJobConfig, TrainingJobConfig>? change = null)
    {
        var config = new TrainingJobConfig { Symbols = new[] { "AAA" }, Architecture = "lstm", WindowSize = 20, Horizon = 5 };
        return change is null ? config : change(config);
    }

    // ------------------------------------------------------------------------------- config wire + validation

    [Fact]
    public void Config_DefaultsKeepTheSingleModelRun_NotWritten_AndRoundTripTheNewKeys()
    {
        // a job that uses none of the newer features carries none of their keys (older training packages reject unknown keys)
        var plain = JsonDocument.Parse(TrainingConfigJson.Serialize(Job())).RootElement;
        foreach (var key in new[] { "calibration", "ensemble_method", "ensemble_members", "stacking_bases", "pretraining",
                     "cpcv", "cpcv_groups", "cpcv_test_groups", "stacking_weight_source" })
        {
            Assert.False(plain.TryGetProperty(key, out _), key);
        }

        var restored = TrainingConfigJson.DeserializeConfig(TrainingConfigJson.Serialize(Job()));
        Assert.Equal(TrainingEnsembleMethod.None, restored.EnsembleMethod);
        Assert.Equal(TrainingPretraining.None, restored.Pretraining);
        Assert.Equal(TrainingEnsembleContract.DefaultBaggingMembers, restored.EnsembleMembers);
        Assert.Empty(restored.StackingBases);
        Assert.False(restored.Calibration);
        Job().Validate();

        // each key is written once it leaves its default, and only then (the default member count is omitted, another is written)
        var calibrated = JsonDocument.Parse(TrainingConfigJson.Serialize(Job(c => c with { Calibration = true }))).RootElement;
        Assert.True(calibrated.GetProperty("calibration").GetBoolean());
        var defaultBagging = JsonDocument.Parse(TrainingConfigJson.Serialize(
            Job(c => c with { EnsembleMethod = TrainingEnsembleMethod.Bagging }))).RootElement;
        Assert.Equal("bagging", defaultBagging.GetProperty("ensemble_method").GetString());
        Assert.False(defaultBagging.TryGetProperty("ensemble_members", out _));
        var sevenBagging = Job(c => c with { EnsembleMethod = TrainingEnsembleMethod.Bagging, EnsembleMembers = 7 });
        Assert.Equal(7, JsonDocument.Parse(TrainingConfigJson.Serialize(sevenBagging)).RootElement
            .GetProperty("ensemble_members").GetInt32());
        Assert.Equal(7, TrainingConfigJson.DeserializeConfig(TrainingConfigJson.Serialize(sevenBagging)).EnsembleMembers);

        var stacking = Job(c => c with
        {
            EnsembleMethod = TrainingEnsembleMethod.Stacking, StackingBases = new[] { "pytorch:lstm", "lightgbm" },
        });
        var wire = TrainingConfigJson.Serialize(stacking);
        Assert.Contains("\"ensemble_method\": \"stacking\"", wire);
        Assert.Contains("\"pytorch:lstm\"", wire);
        var back = TrainingConfigJson.DeserializeConfig(wire);
        Assert.Equal(TrainingEnsembleMethod.Stacking, back.EnsembleMethod);
        Assert.Equal(new[] { "pytorch:lstm", "lightgbm" }, back.StackingBases);

        var pretrained = Job(c => c with { Pretraining = TrainingPretraining.MaskedReconstruction });
        Assert.Contains("\"pretraining\": \"masked_reconstruction\"", TrainingConfigJson.Serialize(pretrained));
        Assert.Equal(TrainingPretraining.MaskedReconstruction,
            TrainingConfigJson.DeserializeConfig(TrainingConfigJson.Serialize(pretrained)).Pretraining);
        Assert.ThrowsAny<JsonException>(() => TrainingConfigJson.DeserializeConfig(
            wire.Replace("\"stacking\"", "\"boosting\"")));
        Assert.ThrowsAny<JsonException>(() => TrainingConfigJson.DeserializeConfig(
            TrainingConfigJson.Serialize(pretrained).Replace("\"masked_reconstruction\"", "1")));
    }

    [Fact]
    public void Config_Validate_AcceptsTheSupportedEnsemblesAndPretraining()
    {
        Job(c => c with { EnsembleMethod = TrainingEnsembleMethod.Bagging, EnsembleMembers = 2 }).Validate();
        Job(c => c with
        {
            EnsembleMethod = TrainingEnsembleMethod.Bagging, EnsembleMembers = TrainingResourceOverrides.MaximumEnsembleMembers,
        }).Validate();
        Job(c => c with
        {
            EnsembleMethod = TrainingEnsembleMethod.Stacking,
            StackingBases = new[] { "pytorch:lstm", "pytorch:cnn", "lightgbm", "tensorflow:gru" },
            Framework = TrainingFramework.LightGBM, Architecture = "gbdt",
        }).Validate();
        Job(c => c with { Pretraining = TrainingPretraining.MaskedReconstruction }).Validate();
        Job(c => c with
        {
            Pretraining = TrainingPretraining.MaskedReconstruction, InitializationMode = TrainingInitializationMode.Resume,
            CheckpointPath = "run.json",
        }).Validate();
    }

    [Fact]
    public void Config_StackingWeightSource_IsWrittenOnlyWhenItIsNotTheDefault_AndRoundTrips()
    {
        var stacking = Job(c => c with
        {
            EnsembleMethod = TrainingEnsembleMethod.Stacking, StackingBases = new[] { "pytorch:lstm", "lightgbm" },
        });
        // the default source is never written, so a past-out-of-fold stacking job stays readable by an older training package
        Assert.False(JsonDocument.Parse(TrainingConfigJson.Serialize(stacking)).RootElement
            .TryGetProperty("stacking_weight_source", out _));
        var meta = stacking with { StackingWeightSource = StackingWeightSource.ReleaseMetaSegment };
        meta.Validate();
        var wire = TrainingConfigJson.Serialize(meta);
        Assert.Contains("\"stacking_weight_source\": \"release_meta_segment\"", wire);
        Assert.Equal(StackingWeightSource.ReleaseMetaSegment, TrainingConfigJson.DeserializeConfig(wire).StackingWeightSource);
        Assert.Equal(StackingWeightSource.PastOutOfFold,
            TrainingConfigJson.DeserializeConfig(TrainingConfigJson.Serialize(stacking)).StackingWeightSource);
        Assert.ThrowsAny<JsonException>(() => TrainingConfigJson.DeserializeConfig(
            wire.Replace("release_meta_segment", "elsewhere")));
        Assert.ThrowsAny<JsonException>(() => TrainingConfigJson.DeserializeConfig(
            wire.Replace("\"release_meta_segment\"", "1")));
        Assert.Equal("release_meta_segment", JsonNamingPolicy.SnakeCaseLower.ConvertName(nameof(StackingWeightSource.ReleaseMetaSegment)));
    }

    public static IEnumerable<object[]> InvalidConfigs()
    {
        static object[] Case(string name, Func<TrainingJobConfig, TrainingJobConfig> change) => new object[] { name, change };
        yield return Case("bagging one member", c => c with { EnsembleMethod = TrainingEnsembleMethod.Bagging, EnsembleMembers = 1 });
        yield return Case("bagging too many", c => c with
        {
            EnsembleMethod = TrainingEnsembleMethod.Bagging, EnsembleMembers = TrainingResourceOverrides.MaximumEnsembleMembers + 1,
        });
        yield return Case("bagging with bases", c => c with
        {
            EnsembleMethod = TrainingEnsembleMethod.Bagging, StackingBases = new[] { "lightgbm" },
        });
        yield return Case("bases without stacking", c => c with { StackingBases = new[] { "lightgbm", "pytorch:cnn" } });
        yield return Case("stacking one base", c => c with
        {
            EnsembleMethod = TrainingEnsembleMethod.Stacking, StackingBases = new[] { "lightgbm" },
        });
        yield return Case("stacking duplicate bases", c => c with
        {
            EnsembleMethod = TrainingEnsembleMethod.Stacking, StackingBases = new[] { "pytorch:lstm", "pytorch:lstm" },
        });
        yield return Case("stacking base without architecture", c => c with
        {
            EnsembleMethod = TrainingEnsembleMethod.Stacking, StackingBases = new[] { "pytorch", "lightgbm" },
        });
        yield return Case("stacking unknown framework", c => c with
        {
            EnsembleMethod = TrainingEnsembleMethod.Stacking, StackingBases = new[] { "xgboost:a", "lightgbm" },
        });
        yield return Case("regression ensemble", c => c with
        {
            EnsembleMethod = TrainingEnsembleMethod.Bagging, TargetType = TargetType.Regression,
        });
        yield return Case("ensemble with calibration", c => c with
        {
            EnsembleMethod = TrainingEnsembleMethod.Bagging, Calibration = true,
        });
        yield return Case("ensemble with pretraining", c => c with
        {
            EnsembleMethod = TrainingEnsembleMethod.Bagging, Pretraining = TrainingPretraining.MaskedReconstruction,
        });
        yield return Case("ensemble with resume", c => c with
        {
            EnsembleMethod = TrainingEnsembleMethod.Bagging, InitializationMode = TrainingInitializationMode.Resume,
            CheckpointPath = "run.json",
        });
        yield return Case("pretraining on lightgbm", c => c with
        {
            Pretraining = TrainingPretraining.MaskedReconstruction, Framework = TrainingFramework.LightGBM, Architecture = "gbdt",
        });
        yield return Case("pretraining on tensorflow", c => c with
        {
            Pretraining = TrainingPretraining.MaskedReconstruction, Framework = TrainingFramework.TensorFlow,
        });
        yield return Case("pretraining with fine-tune", c => c with
        {
            Pretraining = TrainingPretraining.MaskedReconstruction, InitializationMode = TrainingInitializationMode.FineTune,
            CheckpointPath = "best.json", ParentModelId = new string('a', 64), ParentModelHash = new string('b', 64),
        });
        yield return Case("meta segment without an ensemble", c => c with
        {
            StackingWeightSource = StackingWeightSource.ReleaseMetaSegment,
        });
        yield return Case("meta segment with bagging", c => c with
        {
            EnsembleMethod = TrainingEnsembleMethod.Bagging, StackingWeightSource = StackingWeightSource.ReleaseMetaSegment,
        });
        yield return Case("undefined weight source", c => c with
        {
            EnsembleMethod = TrainingEnsembleMethod.Stacking, StackingBases = new[] { "pytorch:lstm", "lightgbm" },
            StackingWeightSource = (StackingWeightSource)9,
        });
        yield return Case("undefined ensemble method", c => c with { EnsembleMethod = (TrainingEnsembleMethod)9 });
        yield return Case("undefined pretraining", c => c with { Pretraining = (TrainingPretraining)9 });
    }

    [Theory]
    [MemberData(nameof(InvalidConfigs))]
    public void Config_Validate_RejectsEveryUnsupportedCombination(string name, Func<TrainingJobConfig, TrainingJobConfig> change)
    {
        var config = change(Job());
        var error = Assert.Throws<InvalidOperationException>(config.Validate);
        Assert.StartsWith("TrainingJobConfig: ", error.Message, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(name));
    }

    [Theory]
    [InlineData("pytorch:LSTM", true, TrainingFramework.PyTorch, "lstm")]
    [InlineData("tensorflow:gru", true, TrainingFramework.TensorFlow, "gru")]
    [InlineData("lightgbm", true, TrainingFramework.LightGBM, "gbdt")]
    [InlineData("pytorch", false, TrainingFramework.PyTorch, "")]
    [InlineData("lightgbm:gbdt", false, TrainingFramework.PyTorch, "")]
    [InlineData(" pytorch:lstm", false, TrainingFramework.PyTorch, "")]
    [InlineData("pytorch:", false, TrainingFramework.PyTorch, "")]
    [InlineData("", false, TrainingFramework.PyTorch, "")]
    [InlineData("catboost:x", false, TrainingFramework.PyTorch, "")]
    public void TryParseBase_MatchesThePythonRules(string text, bool valid, TrainingFramework framework, string architecture)
    {
        Assert.Equal(valid, TrainingEnsembleContract.TryParseBase(text, out var parsedFramework, out var parsedArchitecture,
            out var error));
        if (valid)
        {
            Assert.Equal(framework, parsedFramework);
            Assert.Equal(architecture, parsedArchitecture);
        }
        else
        {
            Assert.False(string.IsNullOrEmpty(error));
        }
    }

    [Fact]
    public void Constants_MatchPythonParityFixture()
    {
        using var parity = JsonDocument.Parse(File.ReadAllText(Path.Combine(SolutionRoot(), "Tests", "EnsembleMixtureParity.json")));
        var root = parity.RootElement;
        Assert.Equal(TrainingEnsembleManifest.SchemaVersion, root.GetProperty("schema_version").GetInt32());
        Assert.Equal(TrainingEnsembleManifest.Kind, root.GetProperty("kind").GetString());
        Assert.Equal(TrainingEnsembleManifest.MixtureRule, root.GetProperty("mixture").GetString());
        Assert.Equal(EnsembleSpec.SumTolerance, root.GetProperty("sum_tolerance").GetDouble());
        Assert.Equal(TrainingEnsembleContract.MinimumMembers, root.GetProperty("min_members").GetInt32());
        Assert.Equal(TrainingEnsembleContract.DefaultBaggingMembers, root.GetProperty("default_bagging_members").GetInt32());
        Assert.Equal(TrainingResourceOverrides.MaximumEnsembleMembers, root.GetProperty("max_ensemble_members").GetInt32());
        var layout = root.GetProperty("layout");
        Assert.Equal(TrainingEnsembleManifest.FileSuffix, layout.GetProperty("ensemble_file_suffix").GetString());
        Assert.Equal(TrainingCheckpointContract.DirectoryName, layout.GetProperty("checkpoint_directory").GetString());
        Assert.Equal(TrainingEnsembleContract.LightGbmArchitecture, layout.GetProperty("lightgbm_architecture").GetString());
        // what Python names, the host's rollback recognizes -- and nothing that merely looks similar
        Assert.True(TrainingEnsembleArtifactLayout.IsMemberFileName(layout.GetProperty("member_stem_example").GetString() + ".onnx"));
        Assert.True(TrainingEnsembleArtifactLayout.IsMemberCheckpointDirectory(
            layout.GetProperty("member_run_id_example").GetString(), "run"));
    }

    [Theory]
    [InlineData("grp_m01.onnx", true)]
    [InlineData("a_b_m10.onnx", true)]
    [InlineData("grp_m01.onnx.metrics.json", false)]
    [InlineData("grp.onnx", false)]
    [InlineData("grp_mx.onnx", false)]
    [InlineData("_m01.onnx", false)]
    [InlineData("sub/grp_m01.onnx", false)]
    [InlineData("", false)]
    public void MemberFileName_RecognizesOnlyTheNamesTheTrainerGives(string name, bool expected) =>
        Assert.Equal(expected, TrainingEnsembleArtifactLayout.IsMemberFileName(name));

    [Theory]
    [InlineData("run-m01", true)]
    [InlineData("run-m1", true)]
    [InlineData("run", false)]
    [InlineData("run-mx", false)]
    [InlineData("runx-m01", false)]
    [InlineData("other-m01", false)]
    public void MemberCheckpointDirectory_MatchesOnlyThisRunsMembers(string name, bool expected) =>
        Assert.Equal(expected, TrainingEnsembleArtifactLayout.IsMemberCheckpointDirectory(name, "run"));

    [Fact]
    public void Mixture_ReproducesThePythonCases()
    {
        using var parity = JsonDocument.Parse(File.ReadAllText(Path.Combine(SolutionRoot(), "Tests", "EnsembleMixtureParity.json")));
        var cases = parity.RootElement.GetProperty("cases").EnumerateArray().ToArray();
        Assert.NotEmpty(cases);
        foreach (var item in cases)
        {
            var weights = item.GetProperty("weights").EnumerateArray().Select(w => w.GetDouble()).ToArray();
            var rows = item.GetProperty("rows").EnumerateArray()
                .Select(row => row.EnumerateArray().Select(v => (float)v.GetDouble()).ToArray()).ToArray();
            var expected = item.GetProperty("mixed").EnumerateArray().Select(v => v.GetDouble()).ToArray();
            var spec = new EnsembleSpec(weights.Select((w, i) => new EnsembleMember("model-" + i, w)).ToArray());
            var weighted = new double[expected.Length];
            var actual = new float[expected.Length];
            EnsemblePredictionService.Aggregate(spec, rows, weighted, actual);
            for (int j = 0; j < expected.Length; j++) Assert.Equal(expected[j], actual[j], 6);
            Assert.Equal(1.0, actual.Sum(), 5);
        }
    }

    // ------------------------------------------------------------------------------- manifest loader

    [Fact]
    public void Manifest_LoadsBothMethods_WithVerifiedFilesAndWeights()
    {
        var folder = Bundle();
        var bagging = TrainingEnsembleManifest.Load(Manifest(folder, "bagging"));
        Assert.Equal(TrainingEnsembleMethod.Bagging, bagging.Method);
        Assert.Equal(new[] { 0.5, 0.5 }, bagging.Members.Select(m => m.Weight).ToArray());
        Assert.Equal("Up,Down,Neutral", bagging.ClassOrder);
        Assert.Equal(5, bagging.Horizon);
        Assert.All(bagging.Members, m =>
        {
            Assert.True(File.Exists(m.OnnxPath));
            Assert.True(File.Exists(m.MetricsPath));
            Assert.Equal(folder, Path.GetDirectoryName(m.OnnxPath));
            Assert.Equal(64, m.Sha256.Length);
        });

        var stacking = TrainingEnsembleManifest.Load(Manifest(folder, "stacking"));
        Assert.Equal(TrainingEnsembleMethod.Stacking, stacking.Method);
        Assert.Equal(1.0, stacking.Members.Sum(m => m.Weight), 9);
        Assert.True(stacking.Members[0].Weight > stacking.Members[1].Weight, "the better member earns the larger fitted weight");

        using var document = JsonDocument.Parse(File.ReadAllText(Manifest(folder, "stacking")));
        var fit = document.RootElement.GetProperty("weights_fit");
        Assert.Equal("simplex_nll_em", fit.GetProperty("method").GetString());
        Assert.Equal(0, fit.GetProperty("row_overlap").GetInt32());
        Assert.Equal(new[] { 0 }, fit.GetProperty("fit_folds").EnumerateArray().Select(f => f.GetInt32()).ToArray());
    }

    [Fact]
    public void Manifest_WithTheWeightTransferRecordAndEmDiagnostics_LoadsLikeAnOlderOne()
    {
        // The fixture manifests predate the audit keys (the older-manifest case); the keys the trainer now adds are ignored by the loader.
        var folder = Bundle();
        var legacy = TrainingEnsembleManifest.Load(Manifest(folder, "stacking"));
        Edit(Manifest(folder, "stacking"), node =>
        {
            node["weight_fit_population"] = "previous_outer_oof";
            node["deployment_population"] = "release_fold_models";
            node["weight_transfer_warning"] = true;
            var fit = node["weights_fit"]!.AsObject();
            fit["converged"] = false;
            fit["iterations"] = 10000;
            fit["final_delta"] = 3.2e-12;
            fit["initial_nll"] = 1.2;
            fit["final_nll"] = 1.1;
            fit["kkt_violation"] = 4.5e-9;
        });

        var annotated = TrainingEnsembleManifest.Load(Manifest(folder, "stacking"));
        Assert.Equal(legacy.Method, annotated.Method);
        Assert.Equal(legacy.RunId, annotated.RunId);
        Assert.Equal(legacy.Members.Select(m => (m.Index, m.Sha256, m.Weight)),
            annotated.Members.Select(m => (m.Index, m.Sha256, m.Weight)));
        using var document = JsonDocument.Parse(File.ReadAllText(Manifest(folder, "stacking")));
        Assert.True(document.RootElement.GetProperty("weight_transfer_warning").GetBoolean());
        Assert.False(document.RootElement.GetProperty("weights_fit").GetProperty("converged").GetBoolean());
    }

    [Fact]
    public void Manifest_WithMetaSegmentWeights_LoadsLikeAnOlderOne()
    {
        // the C# loader reads only what deployment needs: the meta-segment audit record is ignored, the weights and files are checked as ever
        var folder = Bundle();
        var legacy = TrainingEnsembleManifest.Load(Manifest(folder, "stacking"));
        Edit(Manifest(folder, "stacking"), node =>
        {
            node["weight_source"] = "release_meta_segment";
            node["weight_fit_population"] = "release_meta_segment";
            node["deployment_population"] = "release_fold_models";
            var fit = node["weights_fit"]!.AsObject();
            fit["population"] = "release_meta_segment";
            fit["fit_folds"] = new JsonArray();
            fit["weight_shift_l1"] = 0.42;
            fit["past_oof"] = new JsonObject { ["status"] = "unavailable", ["reason"] = "insufficient_oof", ["fit_samples"] = 10 };
        });
        var annotated = TrainingEnsembleManifest.Load(Manifest(folder, "stacking"));
        Assert.Equal(legacy.Method, annotated.Method);
        Assert.Equal(legacy.Members.Select(m => (m.Index, m.Sha256, m.Weight)), annotated.Members.Select(m => (m.Index, m.Sha256, m.Weight)));
    }

    public static IEnumerable<object[]> BrokenManifests()
    {
        static object[] Case(string name, Action<string, JsonNode> change) => new object[] { name, change };
        yield return Case("schema", (_, n) => n["schema_version"] = 2);
        yield return Case("kind", (_, n) => n["kind"] = "other");
        yield return Case("method none", (_, n) => n["method"] = "none");
        yield return Case("method unknown", (_, n) => n["method"] = "boosting");
        yield return Case("regression target", (_, n) => n["target_type"] = "regression");
        yield return Case("mixture rule", (_, n) => n["mixture"] = "softmax");
        yield return Case("class order", (_, n) => n["class_order"] = "Up,Up,Down");
        yield return Case("one member", (_, n) => n["members"]!.AsArray().RemoveAt(1));
        yield return Case("weights sum", (_, n) => n["members"]![0]!["weight"] = 0.7);
        yield return Case("negative weight", (_, n) =>
        {
            n["members"]![0]!["weight"] = -0.5;
            n["members"]![1]!["weight"] = 1.5;
            n["weights"] = new JsonArray(-0.5, 1.5);
        });
        yield return Case("weight list", (_, n) => n["weights"] = new JsonArray(0.4, 0.6));
        yield return Case("traversal", (_, n) => n["members"]![0]!["file"] = "../ensemble_fixture_m01.onnx");
        yield return Case("absolute name", (_, n) => n["members"]![0]!["file"] = Path.GetFullPath("Assets/ensemble_fixture_m01.onnx"));
        yield return Case("metrics name", (_, n) => n["members"]![0]!["metrics_file"] = "other.metrics.json");
        yield return Case("hash shape", (_, n) => n["members"]![0]!["sha256"] = "XYZ");
        yield return Case("hash value", (_, n) => n["members"]![0]!["sha256"] = new string('0', 64));
        yield return Case("duplicate member", (_, n) =>
        {
            n["members"]![1]!["file"] = n["members"]![0]!["file"]!.GetValue<string>();
            n["members"]![1]!["metrics_file"] = n["members"]![0]!["metrics_file"]!.GetValue<string>();
            n["members"]![1]!["sha256"] = n["members"]![0]!["sha256"]!.GetValue<string>();
        });
        yield return Case("index order", (_, n) => n["members"]![0]!["index"] = 1);
        yield return Case("missing field", (_, n) => n.AsObject().Remove("run_id"));
        yield return Case("missing file", (folder, _) => File.Delete(Path.Combine(folder, $"{Stem}_m02.onnx")));
        yield return Case("missing metrics", (folder, _) => File.Delete(Path.Combine(folder, $"{Stem}_m02.onnx.metrics.json")));
        yield return Case("tampered model", (folder, _) =>
            File.AppendAllText(Path.Combine(folder, $"{Stem}_m01.onnx"), "x"));
    }

    [Theory]
    [MemberData(nameof(BrokenManifests))]
    public void Manifest_FailsClosedOnEveryViolation(string name, Action<string, JsonNode> change)
    {
        var folder = Bundle();
        var path = Manifest(folder, "bagging");
        Edit(path, node => change(folder, node));
        Assert.ThrowsAny<Exception>(() => TrainingEnsembleManifest.Load(path));
        Assert.False(string.IsNullOrWhiteSpace(name));
    }

    [Fact]
    public void Manifest_RejectsMissingFileAndInvalidJson()
    {
        Assert.Throws<FileNotFoundException>(() => TrainingEnsembleManifest.Load(Path.Combine(_directory, "none.ensemble.json")));
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "bad.ensemble.json");
        File.WriteAllText(path, "{ not json");
        Assert.Throws<InvalidDataException>(() => TrainingEnsembleManifest.Load(path));
    }

    // ------------------------------------------------------------------------------- registration

    [Theory]
    [InlineData("bagging")]
    [InlineData("stacking")]
    public async Task RegisterEnsemble_RegistersEveryMemberWithoutActivatingAny_AndBuildsTheSpec(string method)
    {
        var folder = Bundle();
        using var registry = new ModelGenerationRegistry(Path.Combine(_directory, "registry_" + method));
        var service = new ModelDeploymentService(registry: registry);
        var registration = await service.RegisterEnsembleAsync(Manifest(folder, method));

        Assert.Equal(method == "bagging" ? TrainingEnsembleMethod.Bagging : TrainingEnsembleMethod.Stacking, registration.Method);
        Assert.Equal(2, registration.Generations.Count);
        Assert.Equal(registration.Generations.Select(g => g.ModelId), registration.Spec.Members.Select(m => m.ModelId));
        Assert.Equal(2, registry.List().Count);
        Assert.Null(registry.ActiveId);
        Assert.All(registry.List(), generation => Assert.False(generation.IsActive));
        registration.Spec.Validate();
        EnsembleSettingsManager.ValidateGenerations(registration.Spec, registry);
        var manifest = TrainingEnsembleManifest.Load(Manifest(folder, method));
        Assert.Equal(manifest.Members.Select(m => m.Weight), registration.Spec.Members.Select(m => m.Weight));

        // a second registration of the same run resolves to the same content-addressed generations
        var again = await service.RegisterEnsembleAsync(Manifest(folder, method));
        Assert.Equal(registration.Generations.Select(g => g.ModelId), again.Generations.Select(g => g.ModelId));
        Assert.Equal(2, registry.List().Count);
    }

    [Fact]
    public async Task RegisterEnsemble_TamperedMemberFailsBeforeAnythingIsRegistered()
    {
        var folder = Bundle();
        File.AppendAllText(Path.Combine(folder, $"{Stem}_m02.onnx"), "x");
        using var registry = new ModelGenerationRegistry(Path.Combine(_directory, "registry_tampered"));
        var service = new ModelDeploymentService(registry: registry);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.RegisterEnsembleAsync(Manifest(folder, "bagging")));
        Assert.Empty(registry.List());
    }

    [Theory]
    [InlineData("m01")] // the first member is bad: nothing may be registered
    [InlineData("m02")] // the last member is bad: the earlier member must not stay registered
    public async Task RegisterEnsemble_AMemberTheRegistryRejects_RegistersNoMember(string badMember)
    {
        var folder = Bundle();
        // The manifest loader only checks that the metrics file exists; the registry's own evidence checks reject this one.
        Edit(Path.Combine(folder, $"{Stem}_{badMember}.onnx.metrics.json"), node => node.AsObject().Clear());
        var root = Path.Combine(_directory, "registry_rejected_" + badMember);
        using var registry = new ModelGenerationRegistry(root);
        var service = new ModelDeploymentService(registry: registry);

        await Assert.ThrowsAnyAsync<Exception>(() => service.RegisterEnsembleAsync(Manifest(folder, "bagging")));

        Assert.Empty(registry.List());
        Assert.Empty(Directory.EnumerateDirectories(root)); // no published generation and no leftover staging directory
    }

    [Fact]
    public async Task VerifyCandidate_RunsTheRegistrationChecksButPublishesNothing()
    {
        var folder = Bundle();
        var root = Path.Combine(_directory, "registry_verify");
        using var registry = new ModelGenerationRegistry(root);
        var onnx = Path.Combine(folder, $"{Stem}_m01.onnx");

        var verified = await registry.VerifyCandidateAsync(onnx);
        Assert.Empty(registry.List());
        Assert.Empty(Directory.EnumerateDirectories(root));
        Assert.Null(registry.GetManifest(verified.ModelId));

        var registered = await registry.RegisterAsync(onnx);
        Assert.Equal(registered.ModelId, verified.ModelId); // the manifest verification promised is the one registration produces
        Assert.Equal(registered.ModelHash, verified.ModelHash);
        Assert.Single(registry.List());

        File.AppendAllText(onnx, "x"); // a changed model fails verification just as it fails registration
        await Assert.ThrowsAnyAsync<Exception>(() => registry.VerifyCandidateAsync(onnx));
        await Assert.ThrowsAnyAsync<FileNotFoundException>(() => registry.VerifyCandidateAsync(Path.Combine(folder, "missing.onnx")));
    }

    [Fact]
    public async Task VerifyCandidate_TheInterfaceDefaultRefusesSilentSupport()
    {
        IModelGenerationRegistry bare = new BareRegistry();
        await Assert.ThrowsAsync<NotSupportedException>(() => bare.VerifyCandidateAsync("x.onnx"));
    }

    private sealed class BareRegistry : IModelGenerationRegistry
    {
        public string? ActiveId => null;
        public string? PreviousId => null;
        public IReadOnlyList<ModelGenerationSummary> List() => Array.Empty<ModelGenerationSummary>();
        public Task<ModelGenerationManifest> RegisterAsync(string onnxPath, string? metricsPath = null,
            System.Threading.CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> ActivateAsync(string modelId, bool manual = false, bool confirmLowerScore = false,
            System.Threading.CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> ConfirmActivationAsync(string modelId, string? expectedActiveId,
            System.Threading.CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> RollbackAsync(bool confirmLowerScore = false, System.Threading.CancellationToken ct = default) =>
            throw new NotSupportedException();
        public ModelGenerationLease? AcquireActive() => null;
    }

    [Fact]
    public async Task RegisterEnsemble_WithoutARegistryIsAnExplicitError_AndTheInterfaceDefaultRefusesSilentSupport()
    {
        var service = new ModelDeploymentService();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RegisterEnsembleAsync(Manifest(Bundle(), "bagging")));
        IModelDeploymentService bare = new BareDeployment();
        await Assert.ThrowsAsync<NotSupportedException>(() => bare.RegisterEnsembleAsync("x.ensemble.json"));
    }

    private sealed class BareDeployment : IModelDeploymentService
    {
        public Task<string> DeployAsync(string onnxSourcePath, string? metricsSourcePath = null,
            System.Threading.CancellationToken ct = default) => throw new NotSupportedException();

        public Task<ModelGenerationManifest> RegisterGenerationAsync(string onnxSourcePath, string? metricsSourcePath = null,
            System.Threading.CancellationToken ct = default) => throw new NotSupportedException();
    }

    // ------------------------------------------------------------------------------- protocol line

    [Theory]
    [InlineData("ARTIFACT:ensemble:C:\\runs\\a.ensemble.json", "ensemble", "C:\\runs\\a.ensemble.json")]
    [InlineData("ARTIFACT:ensemble_member:C:\\runs\\a_m01.onnx", "ensemble_member", "C:\\runs\\a_m01.onnx")]
    public void ProtocolLine_ParsesTheEnsembleArtifactKinds(string line, string kind, string path)
    {
        Assert.True(TrainingProtocolLine.TryParseArtifact(line, out var parsedKind, out var parsedPath));
        Assert.Equal(kind, parsedKind);
        Assert.Equal(path, parsedPath);
    }
}
