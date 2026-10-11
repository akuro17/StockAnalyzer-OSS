using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
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
using StockAnalyzer.Core.Tests.Models.Training;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// T25 graph consumer: input construction and model output against the Python reference (<c>Tests/GraphParity.json</c>), contract refusals,
/// separation from the single-symbol prediction path, cancellation and disposal.
/// </summary>
public sealed class GraphPredictionServiceTests : IDisposable
{
    private const double AdjacencyTolerance = 1e-6;
    private const double FeatureTolerance = 1e-5;
    private const double OutputTolerance = 1e-4;
    private static readonly string FixtureDirectory = Path.Combine(AppContext.BaseDirectory, "Assets", "graph_fixture");

    private readonly List<string> _temporary = new();

    public void Dispose()
    {
        foreach (var path in _temporary)
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
    }

    public static IEnumerable<object[]> CaseNames()
    {
        using var parity = GraphContractTests.Parity();
        foreach (var item in parity.RootElement.GetProperty("cases").EnumerateArray()) yield return new object[] { item.GetProperty("name").GetString()! };
    }

    public static IEnumerable<object[]> ModeNames()
    {
        using var parity = GraphContractTests.Parity();
        foreach (var item in parity.RootElement.GetProperty("mode_cases").EnumerateArray()) yield return new object[] { item.GetProperty("feature_mode").GetString()! };
    }

    private static Dictionary<string, IReadOnlyList<CandleData>> Histories(JsonElement histories)
    {
        var result = new Dictionary<string, IReadOnlyList<CandleData>>(StringComparer.Ordinal);
        foreach (var node in histories.EnumerateObject())
        {
            result[node.Name] = node.Value.EnumerateArray().Select(row =>
            {
                var c = row.EnumerateArray().ToArray();
                return new CandleData(DateTime.ParseExact(c[0].GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture), c[1].GetDecimal(), c[2].GetDecimal(),
                    c[3].GetDecimal(), c[4].GetDecimal(), c[5].GetInt64());
            }).ToArray();
        }

        return result;
    }

    private static JsonElement Case(JsonDocument parity, string name) =>
        parity.RootElement.GetProperty("cases").EnumerateArray().Single(c => c.GetProperty("name").GetString() == name);

    private static float[] Floats(JsonElement array) => array.EnumerateArray().Select(v => (float)v.GetDouble()).ToArray();

    private static GraphModelInfo InfoFor(JsonDocument parity, string featureMode, int channels)
    {
        var spec = parity.RootElement.GetProperty("spec");
        var nodes = parity.RootElement.GetProperty("node_order").EnumerateArray().Select(n => n.GetString()!).ToArray();
        return new GraphModelInfo("test", "hash", nodes.ToImmutableArray(), spec.GetProperty("window").GetInt32(), spec.GetProperty("horizon").GetInt32(),
            spec.GetProperty("corr_window").GetInt32(), spec.GetProperty("edge_threshold").GetDouble(), spec.GetProperty("min_nodes").GetInt32(),
            PredictionModelMetadata.ParseFeatureMode(featureMode)!.Value, channels, GraphContract.ClassOrder.Split(',').ToImmutableArray());
    }

    private static IReadOnlyList<IReadOnlyList<CandleData>> PerNode(GraphModelInfo info, Dictionary<string, IReadOnlyList<CandleData>> histories) =>
        info.NodeOrder.Select(n => histories.TryGetValue(n, out var h) ? h : Array.Empty<CandleData>()).ToArray();

    private static void AssertClose(float[] expected, float[] actual, double tolerance, string what)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++) Assert.True(Math.Abs(expected[i] - actual[i]) <= tolerance, $"{what}[{i}]: expected {expected[i]}, actual {actual[i]}");
    }

    private async Task<GraphPredictionService> LoadedService(string directory = null)
    {
        var service = new GraphPredictionService(new MLDataProcessor());
        await service.LoadAsync(directory ?? FixtureDirectory);
        return service;
    }

    private string CopyFixture()
    {
        var target = Path.Combine(Path.GetTempPath(), "graph_fixture_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(FixtureDirectory)) File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        _temporary.Add(target);
        return target;
    }

    // --- Input construction (pure) ---------------------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void Builder_MatchesPythonReference(string name)
    {
        using var parity = GraphContractTests.Parity();
        var item = Case(parity, name);
        var expected = item.GetProperty("expected");
        var info = InfoFor(parity, parity.RootElement.GetProperty("spec").GetProperty("feature_mode").GetString()!, 5);
        var input = GraphInputBuilder.Build(info, PerNode(info, Histories(item.GetProperty("histories"))), DateTime.Parse(item.GetProperty("as_of").GetString()!, CultureInfo.InvariantCulture),
            new MLDataProcessor(), out var failure);

        var status = expected.GetProperty("status").GetString();
        if (status == "unavailable" && expected.GetProperty("reason").GetString() == GraphUnavailableReason.AsOfNotInData)
        {
            Assert.Null(input);
            Assert.Equal(GraphUnavailableReason.AsOfNotInData, failure);
            return;
        }

        Assert.NotNull(input);
        AssertClose(Floats(expected.GetProperty("mask")), input!.NodeMask, 0, "mask");
        Assert.Equal((int)Floats(expected.GetProperty("mask")).Sum(), input.ValidNodes);
        if (status != "available") return;   // below min_nodes the Python reference lists only the mask
        AssertClose(Floats(expected.GetProperty("adjacency")), input.Adjacency, AdjacencyTolerance, "adjacency");
        AssertClose(Floats(expected.GetProperty("node_features")), input.NodeFeatures, FeatureTolerance, "node_features");
    }

    [Theory]
    [MemberData(nameof(ModeNames))]
    public void Builder_PerFeatureMode_MatchesPythonFeatures(string mode)
    {
        using var parity = GraphContractTests.Parity();
        var item = parity.RootElement.GetProperty("mode_cases").EnumerateArray().Single(c => c.GetProperty("feature_mode").GetString() == mode);
        var full = Case(parity, "full_as_of_last");
        var info = InfoFor(parity, mode, item.GetProperty("channels").GetInt32());
        var input = GraphInputBuilder.Build(info, PerNode(info, Histories(full.GetProperty("histories"))), DateTime.Parse(item.GetProperty("as_of").GetString()!, CultureInfo.InvariantCulture),
            new MLDataProcessor(), out _);
        Assert.NotNull(input);
        AssertClose(Floats(item.GetProperty("mask")), input!.NodeMask, 0, "mask");
        AssertClose(Floats(item.GetProperty("adjacency")), input.Adjacency, AdjacencyTolerance, "adjacency");
        AssertClose(Floats(item.GetProperty("node_features")), input.NodeFeatures, FeatureTolerance, $"node_features({mode})");
    }

    [Fact]
    public void Builder_RowsAfterTheAsOfDate_AreNeverRead()
    {
        using var parity = GraphContractTests.Parity();
        var item = Case(parity, "full_as_of_last");
        var info = InfoFor(parity, "ohlcv_minmax", 5);
        var histories = Histories(item.GetProperty("histories"));
        var asOf = histories["AAA"][25].Timestamp;
        var truncated = histories.ToDictionary(p => p.Key, p => (IReadOnlyList<CandleData>)p.Value.Where(c => c.Timestamp <= asOf).ToArray());
        var tampered = histories.ToDictionary(p => p.Key, p => (IReadOnlyList<CandleData>)p.Value
            .Select(c => c.Timestamp > asOf ? c with { Close = c.Close * 7m, Open = c.Open * 7m, High = c.High * 7m, Low = c.Low * 7m } : c).ToArray());
        var processor = new MLDataProcessor();
        var left = GraphInputBuilder.Build(info, PerNode(info, truncated), asOf, processor, out _)!;
        var right = GraphInputBuilder.Build(info, PerNode(info, tampered), asOf, processor, out _)!;
        Assert.Equal(left.NodeFeatures, right.NodeFeatures);
        Assert.Equal(left.Adjacency, right.Adjacency);
        Assert.Equal(left.NodeMask, right.NodeMask);
    }

    // --- Model output ----------------------------------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(CaseNames))]
    public async Task Predict_MatchesPythonOutput(string name)
    {
        using var parity = GraphContractTests.Parity();
        var item = Case(parity, name);
        var expected = item.GetProperty("expected");
        using var service = await LoadedService();
        var result = await service.PredictAsync(Histories(item.GetProperty("histories")), DateTime.Parse(item.GetProperty("as_of").GetString()!, CultureInfo.InvariantCulture));

        if (expected.GetProperty("status").GetString() == "unavailable")
        {
            Assert.False(result.IsAvailable);
            Assert.Equal(expected.GetProperty("reason").GetString(), result.Reason);
            Assert.Empty(result.Nodes);
            return;
        }

        Assert.True(result.IsAvailable, result.Reason);
        Assert.Equal(new[] { "Up", "Down", "Neutral" }, result.ClassOrder);
        var mask = Floats(expected.GetProperty("mask"));
        var output = Floats(expected.GetProperty("output"));
        Assert.Equal(mask.Length, result.Nodes.Length);
        for (int i = 0; i < mask.Length; i++)
        {
            Assert.Equal(mask[i] == 0f, result.Nodes[i].IsMasked);
            if (mask[i] == 0f)
            {
                Assert.Empty(result.Nodes[i].Probabilities);
                continue;
            }

            AssertClose(output.Skip(i * 3).Take(3).ToArray(), result.Nodes[i].Probabilities.ToArray(), OutputTolerance, $"{name}.node{i}");
            Assert.InRange(result.Nodes[i].Probabilities.Sum(), 1f - 1e-4f, 1f + 1e-4f);
        }
    }

    [Fact]
    public async Task Predict_DefaultsToTheNewestDate()
    {
        using var parity = GraphContractTests.Parity();
        var item = Case(parity, "full_as_of_last");
        using var service = await LoadedService();
        var result = await service.PredictAsync(Histories(item.GetProperty("histories")));
        Assert.True(result.IsAvailable);
        Assert.Equal(DateTime.Parse(item.GetProperty("as_of").GetString()!, CultureInfo.InvariantCulture), result.AsOf);
    }

    // --- Refusals and expected failures ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Predict_BeforeLoading_IsUnavailableNotAZeroPrediction()
    {
        using var parity = GraphContractTests.Parity();
        using var service = new GraphPredictionService(new MLDataProcessor());
        Assert.Null(service.Model);
        var result = await service.PredictAsync(Histories(Case(parity, "full_as_of_last").GetProperty("histories")));
        Assert.Equal(GraphUnavailableReason.NotLoaded, result.Reason);
        Assert.Empty(result.Nodes);
    }

    [Theory]
    [InlineData("ZZZ")]
    [InlineData("aaa")]
    [InlineData("AAA ")]
    public async Task Predict_RefusesASymbolThatIsNotANodeOfTheModel(string symbol)
    {
        using var parity = GraphContractTests.Parity();
        var histories = Histories(Case(parity, "full_as_of_last").GetProperty("histories"));
        histories[symbol] = histories["AAA"];
        using var service = await LoadedService();
        var result = await service.PredictAsync(histories);
        Assert.Equal(GraphUnavailableReason.UnknownSymbol, result.Reason);
        Assert.Equal(symbol, result.Detail);
    }

    [Fact]
    public async Task Predict_RefusesANonChronologicalHistory_AndAnEmptyRequest()
    {
        using var parity = GraphContractTests.Parity();
        var histories = Histories(Case(parity, "full_as_of_last").GetProperty("histories"));
        var reversed = histories.ToDictionary(p => p.Key, p => p.Key == "BBB" ? (IReadOnlyList<CandleData>)p.Value.Reverse().ToArray() : p.Value);
        var duplicated = histories.ToDictionary(p => p.Key, p => p.Key == "CCC" ? (IReadOnlyList<CandleData>)p.Value.Concat(new[] { p.Value[^1] }).ToArray() : p.Value);
        using var service = await LoadedService();
        Assert.Equal(GraphUnavailableReason.InvalidHistory, (await service.PredictAsync(reversed)).Reason);
        Assert.Equal(GraphUnavailableReason.InvalidHistory, (await service.PredictAsync(duplicated)).Reason);
        Assert.Equal(GraphUnavailableReason.NoData, (await service.PredictAsync(new Dictionary<string, IReadOnlyList<CandleData>>())).Reason);
        Assert.Equal(GraphUnavailableReason.NoData, (await service.PredictAsync(new Dictionary<string, IReadOnlyList<CandleData>> { ["AAA"] = Array.Empty<CandleData>() })).Reason);
    }

    [Fact]
    public async Task Load_ExposesTheVerifiedContract()
    {
        using var parity = GraphContractTests.Parity();
        using var service = await LoadedService();
        var info = service.Model;
        Assert.NotNull(info);
        Assert.Equal(new[] { "AAA", "BBB", "CCC" }, info!.NodeOrder);
        var spec = parity.RootElement.GetProperty("spec");
        Assert.Equal(spec.GetProperty("window").GetInt32(), info.Window);
        Assert.Equal(spec.GetProperty("corr_window").GetInt32(), info.CorrWindow);
        Assert.Equal(spec.GetProperty("edge_threshold").GetDouble(), info.EdgeThreshold);
        Assert.Equal(spec.GetProperty("min_nodes").GetInt32(), info.MinNodes);
        Assert.Equal(PredictionFeatureMode.OhlcvMinMax, info.FeatureMode);
        Assert.Equal(5, info.Channels);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(FixtureDirectory, "graph.onnx")))).ToLowerInvariant(), info.ModelSha256);
    }

    [Fact]
    public async Task Load_RefusesAModelThatDoesNotMatchItsRecordedHash()
    {
        var directory = CopyFixture();
        var bytes = File.ReadAllBytes(Path.Combine(directory, "graph.onnx"));
        bytes[bytes.Length / 2] ^= 0x01;
        File.WriteAllBytes(Path.Combine(directory, "graph.onnx"), bytes);
        using var service = new GraphPredictionService(new MLDataProcessor());
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => service.LoadAsync(directory));
        Assert.Contains("SHA-256", error.Message);
        Assert.Null(service.Model);
    }

    [Theory]
    [InlineData("window", "7")]
    [InlineData("node_order", "[\"AAA\",\"BBB\"]")]
    public async Task Load_RefusesADocumentThatDisagreesWithTheModel(string key, string value)
    {
        var directory = CopyFixture();
        var path = Path.Combine(directory, "graph.json");
        var document = JsonNode.Parse(File.ReadAllText(path))!;
        document["contract"]![GraphContract.MetadataPrefix + key] = value;
        File.WriteAllText(path, document.ToJsonString());
        using var service = new GraphPredictionService(new MLDataProcessor());
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => service.LoadAsync(directory));
        Assert.Contains("disagree", error.Message);
    }

    [Theory]
    [InlineData("kind", "\"Meta\"")]
    [InlineData("schema_version", "2")]
    public async Task Load_RefusesAnotherKindOrSchema(string key, string json)
    {
        var directory = CopyFixture();
        var path = Path.Combine(directory, "graph.json");
        var document = JsonNode.Parse(File.ReadAllText(path))!;
        document[key] = JsonNode.Parse(json);
        File.WriteAllText(path, document.ToJsonString());
        using var service = new GraphPredictionService(new MLDataProcessor());
        await Assert.ThrowsAsync<InvalidDataException>(() => service.LoadAsync(directory));
    }

    [Fact]
    public async Task Load_RefusesMissingFiles_AndAMissingFolder()
    {
        var directory = CopyFixture();
        File.Delete(Path.Combine(directory, "graph.json"));
        using var service = new GraphPredictionService(new MLDataProcessor());
        await Assert.ThrowsAsync<FileNotFoundException>(() => service.LoadAsync(directory));
        File.Delete(Path.Combine(directory, "graph.onnx"));
        await Assert.ThrowsAsync<FileNotFoundException>(() => service.LoadAsync(directory));
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => service.LoadAsync(Path.Combine(directory, "missing")));
    }

    [Fact]
    public async Task Load_RefusesASingleSymbolPredictionModel_EvenWithAMatchingHash()
    {
        var directory = CopyFixture();
        var single = Path.Combine(AppContext.BaseDirectory, "Assets", "trend_predictor_goodmeta.onnx");
        File.Copy(single, Path.Combine(directory, "graph.onnx"), overwrite: true);
        var path = Path.Combine(directory, "graph.json");
        var document = JsonNode.Parse(File.ReadAllText(path))!;
        document["model"]!["sha256"] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(single))).ToLowerInvariant();
        File.WriteAllText(path, document.ToJsonString());
        using var service = new GraphPredictionService(new MLDataProcessor());
        await Assert.ThrowsAsync<InvalidDataException>(() => service.LoadAsync(directory));
    }

    [Fact]
    public async Task Load_ARefusedReplacement_KeepsTheLoadedModelServing()
    {
        using var parity = GraphContractTests.Parity();
        var broken = CopyFixture();
        File.WriteAllText(Path.Combine(broken, "graph.json"), "{}");
        using var service = await LoadedService();
        await Assert.ThrowsAsync<InvalidDataException>(() => service.LoadAsync(broken));
        Assert.NotNull(service.Model);
        Assert.True((await service.PredictAsync(Histories(Case(parity, "full_as_of_last").GetProperty("histories")))).IsAvailable);
    }

    // --- Separation from the single-symbol prediction path (NFR-02, G8) ---------------------------------------------------------------------

    [Fact]
    public void SingleSymbolPrediction_RefusesTheGraphModel()
    {
        using var session = new InferenceSession(Path.Combine(FixtureDirectory, "graph.onnx"));
        var input = session.InputMetadata[GraphContract.InputNodeFeatures];
        var output = session.OutputMetadata[GraphContract.OutputName];
        var error = Assert.Throws<InvalidOperationException>(() => PredictionService.ValidateModelContract(input, output, 6, 5, 3, GraphContract.InputNodeFeatures, GraphContract.OutputName));
        Assert.Contains("rank", error.Message);
        Assert.ThrowsAny<Exception>(() => PredictionModelMetadata.ReadOutputContract(session.ModelMetadata.CustomMetadataMap));
    }

    [Fact]
    public void GraphService_IsNotAPredictionService()
    {
        Assert.False(typeof(IPredictionService).IsAssignableFrom(typeof(GraphPredictionService)));
        Assert.False(typeof(PredictionService).IsAssignableFrom(typeof(GraphPredictionService)));
        Assert.False(typeof(GraphPredictionService).BaseType == typeof(PredictionService));
    }

    // --- Cancellation and disposal ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Predict_ACancelledToken_ThrowsAndDeliversNothing()
    {
        using var parity = GraphContractTests.Parity();
        var histories = Histories(Case(parity, "full_as_of_last").GetProperty("histories"));
        using var service = await LoadedService();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.PredictAsync(histories, null, cancelled.Token));
    }

    [Fact]
    public async Task Dispose_IsIdempotent_AndLaterCallsAreRefused()
    {
        var service = await LoadedService();
        service.Dispose();
        service.Dispose();
        Assert.Null(service.Model);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.PredictAsync(new Dictionary<string, IReadOnlyList<CandleData>>()));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.LoadAsync(FixtureDirectory));
    }

    [Fact]
    public async Task Dispose_DuringAnInference_LetsTheRunningInferenceFinish()
    {
        using var parity = GraphContractTests.Parity();
        var histories = Histories(Case(parity, "full_as_of_last").GetProperty("histories"));
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        histories["AAA"] = new BlockingList(histories["AAA"], entered, release);
        var service = await LoadedService();
        var running = service.PredictAsync(histories);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(30)), "the inference never started");
        service.Dispose();                    // the model is retired while an inference holds it
        release.Set();
        var result = await running;
        Assert.True(result.IsAvailable, result.Reason);
    }

    /// <summary>A history whose first read blocks until released, so a test can dispose the service while an inference is in flight.</summary>
    private sealed class BlockingList : IReadOnlyList<CandleData>
    {
        private readonly IReadOnlyList<CandleData> _inner;
        private readonly ManualResetEventSlim _entered;
        private readonly ManualResetEventSlim _release;
        private int _blocked;

        public BlockingList(IReadOnlyList<CandleData> inner, ManualResetEventSlim entered, ManualResetEventSlim release)
        {
            _inner = inner;
            _entered = entered;
            _release = release;
        }

        public int Count => _inner.Count;

        public CandleData this[int index]
        {
            get
            {
                if (Interlocked.Exchange(ref _blocked, 1) == 0)
                {
                    _entered.Set();
                    _release.Wait(TimeSpan.FromSeconds(30));
                }

                return _inner[index];
            }
        }

        public IEnumerator<CandleData> GetEnumerator() => _inner.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
