using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Training;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// ONNX Runtime implementation of <see cref="IGraphPredictionService"/>. The model is loaded from an explicit run folder (no settings entry, no registry): the
/// <c>graph.json</c> hash, contract and node order are verified against the ONNX file before anything is served.
/// </summary>
public sealed class GraphPredictionService : IGraphPredictionService
{
    private readonly IMLDataProcessor _processor;
    private readonly ILogger<GraphPredictionService> _logger;
    private readonly object _gate = new();
    private RuntimeBundle? _runtime;
    private bool _disposed;

    public GraphPredictionService(IMLDataProcessor processor, ILogger<GraphPredictionService>? logger = null)
    {
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
        _logger = logger ?? NullLogger<GraphPredictionService>.Instance;
    }

    public GraphModelInfo? Model
    {
        get
        {
            lock (_gate) return _runtime?.Info;
        }
    }

    private sealed class RuntimeBundle : IDisposable
    {
        internal RuntimeBundle(InferenceSession session, GraphModelInfo info)
        {
            Session = session;
            Info = info;
            Options = new RunOptions();   // ORT 1.24.3 dereferences the options in native interop: a null throws, so one instance is kept
        }

        internal InferenceSession Session { get; }
        internal GraphModelInfo Info { get; }
        internal RunOptions Options { get; }
        internal int Users { get; set; }
        internal bool Retired { get; set; }

        public void Dispose()
        {
            Options.Dispose();
            Session.Dispose();
        }
    }

    public async Task LoadAsync(string runDirectory, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var loaded = await Task.Run(() => LoadRuntime(runDirectory, cancellationToken), cancellationToken).ConfigureAwait(false);
        RuntimeBundle? previous;
        lock (_gate)
        {
            if (_disposed)
            {
                loaded.Dispose();
                throw new ObjectDisposedException(nameof(GraphPredictionService));
            }

            previous = _runtime;
            _runtime = loaded;
            if (previous is not null)
            {
                previous.Retired = true;
                if (previous.Users != 0) previous = null;   // the last in-flight inference disposes it
            }
        }

        previous?.Dispose();
    }

    public async Task<GraphPredictionResult> PredictAsync(IReadOnlyDictionary<string, IReadOnlyList<CandleData>> histories, DateTime? asOf = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        var runtime = Acquire();
        if (runtime is null) return GraphPredictionResult.Unavailable(GraphUnavailableReason.NotLoaded);
        try
        {
            var result = await Task.Run(() => Predict(runtime, histories, asOf), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();   // a late success after cancellation is not delivered
            return result;
        }
        finally
        {
            Release(runtime);
        }
    }

    private GraphPredictionResult Predict(RuntimeBundle runtime, IReadOnlyDictionary<string, IReadOnlyList<CandleData>> histories, DateTime? asOf)
    {
        var info = runtime.Info;
        if (histories is null) return GraphPredictionResult.Unavailable(GraphUnavailableReason.InvalidHistory, "histories is null");
        var nodeIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < info.NodeOrder.Length; i++) nodeIndex[info.NodeOrder[i]] = i;

        var perNode = new IReadOnlyList<CandleData>[info.NodeOrder.Length];
        for (int i = 0; i < perNode.Length; i++) perNode[i] = Array.Empty<CandleData>();
        foreach (var (symbol, history) in histories)
        {
            if (!nodeIndex.TryGetValue(symbol, out int index))
                return GraphPredictionResult.Unavailable(GraphUnavailableReason.UnknownSymbol, symbol);
            if (history is null) return GraphPredictionResult.Unavailable(GraphUnavailableReason.InvalidHistory, $"{symbol}: history is null");
            CandleData[] snapshot;
            try { snapshot = Snapshot(history); }
            catch (Exception ex) when (ex is ArgumentOutOfRangeException or InvalidOperationException)
            {
                // The caller changed the list while it was copied; refuse instead of mixing two states.
                return GraphPredictionResult.Unavailable(GraphUnavailableReason.InvalidHistory, $"{symbol}: history changed while being read");
            }

            var problem = GraphInputBuilder.ValidateHistory(snapshot);
            if (problem is not null) return GraphPredictionResult.Unavailable(GraphUnavailableReason.InvalidHistory, $"{symbol}: {problem}");
            perNode[index] = snapshot;
        }

        var date = asOf?.Date ?? GraphInputBuilder.LatestDate(perNode);
        if (date is null) return GraphPredictionResult.Unavailable(GraphUnavailableReason.NoData);

        GraphInput? input;
        string? failure;
        try { input = GraphInputBuilder.Build(info, perNode, date.Value, _processor, out failure); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or OverflowException)
        {
            _logger.LogWarning(ex, "Graph input could not be built.");
            return GraphPredictionResult.Unavailable(GraphUnavailableReason.InferenceFailed, "input construction failed", date);
        }

        if (input is null) return GraphPredictionResult.Unavailable(failure ?? GraphUnavailableReason.AsOfNotInData, asOf: date);
        if (input.ValidNodes < info.MinNodes)
            return GraphPredictionResult.Unavailable(GraphUnavailableReason.InsufficientNodes, $"{input.ValidNodes} valid node(s), {info.MinNodes} required", date);

        try
        {
            var probabilities = Run(runtime, input);
            if (probabilities is null) return GraphPredictionResult.Unavailable(GraphUnavailableReason.InvalidOutput, asOf: date);
            var nodes = ImmutableArray.CreateBuilder<GraphNodePrediction>(info.NodeOrder.Length);
            for (int i = 0; i < info.NodeOrder.Length; i++)
            {
                bool masked = input.NodeMask[i] == 0f;
                nodes.Add(new GraphNodePrediction(info.NodeOrder[i], masked,
                    masked ? ImmutableArray<float>.Empty : ImmutableArray.Create(probabilities.AsSpan(i * GraphContract.ClassCount, GraphContract.ClassCount))));
            }

            return new GraphPredictionResult(GraphPredictionStatus.Available, null, null, date, info.ClassOrder, nodes.MoveToImmutable());
        }
        catch (OnnxRuntimeException ex)
        {
            _logger.LogWarning(ex, "Graph inference failed.");
            return GraphPredictionResult.Unavailable(GraphUnavailableReason.InferenceFailed, ex.Message, date);
        }
    }

    /// <summary>Runs the model and returns the <c>[nodes * classes]</c> probabilities, or <see langword="null"/> when a valid node's row is not a probability distribution.</summary>
    private static float[]? Run(RuntimeBundle runtime, GraphInput input)
    {
        int n = runtime.Info.NodeOrder.Length;
        long[] featureShape = { 1, n, runtime.Info.Window, runtime.Info.Channels };
        long[] adjacencyShape = { 1, n, n };
        long[] maskShape = { 1, n };
        using var features = OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance, input.NodeFeatures.AsMemory(), featureShape);
        using var adjacency = OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance, input.Adjacency.AsMemory(), adjacencyShape);
        using var mask = OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance, input.NodeMask.AsMemory(), maskShape);
        using var results = runtime.Session.Run(runtime.Options,
            new[] { GraphContract.InputNodeFeatures, GraphContract.InputAdjacency, GraphContract.InputNodeMask },
            new[] { features, adjacency, mask }, new[] { GraphContract.OutputName });
        if (results.Count != 1 || !results[0].IsTensor) return null;
        var shape = results[0].GetTensorTypeAndShape();
        if (shape.ElementDataType != TensorElementType.Float || shape.Shape.Length != GraphContract.OutputRank
            || shape.Shape[0] != 1 || shape.Shape[1] != n || shape.Shape[2] != GraphContract.ClassCount) return null;
        var values = results[0].GetTensorDataAsSpan<float>().ToArray();
        for (int i = 0; i < n; i++)
        {
            if (input.NodeMask[i] == 0f) continue;   // the output row of a masked node is not meaningful
            float sum = 0f;
            for (int c = 0; c < GraphContract.ClassCount; c++)
            {
                float p = values[i * GraphContract.ClassCount + c];
                if (!float.IsFinite(p) || p < 0f || p > 1f) return null;
                sum += p;
            }

            if (MathF.Abs(sum - 1f) > IMLDataProcessor.SoftmaxSumTolerance) return null;
        }

        return values;
    }

    private static CandleData[] Snapshot(IReadOnlyList<CandleData> history)
    {
        var copy = new CandleData[history.Count];
        for (int i = 0; i < copy.Length; i++) copy[i] = history[i];
        return copy;
    }

    private RuntimeBundle? Acquire()
    {
        lock (_gate)
        {
            if (_disposed || _runtime is null) return null;
            _runtime.Users++;
            return _runtime;
        }
    }

    private void Release(RuntimeBundle runtime)
    {
        bool dispose;
        lock (_gate)
        {
            runtime.Users--;
            dispose = runtime.Users == 0 && runtime.Retired;
        }

        if (dispose) runtime.Dispose();
    }

    /// <summary>Marks the service disposed. An inference that is already running finishes first: the native session is released by its last user.</summary>
    public void Dispose()
    {
        RuntimeBundle? toDispose = null;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (_runtime is not null)
            {
                _runtime.Retired = true;
                if (_runtime.Users == 0) toDispose = _runtime;
                _runtime = null;
            }
        }

        toDispose?.Dispose();
    }

    // --- Loading and contract verification -------------------------------------------------------------------------------------------------

    private static RuntimeBundle LoadRuntime(string runDirectory, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(runDirectory) || !Directory.Exists(runDirectory))
            throw new DirectoryNotFoundException("The graph run folder does not exist.");
        var modelPath = Path.Combine(runDirectory, GraphContract.ModelFileName);
        var documentPath = Path.Combine(runDirectory, GraphContract.DocumentFileName);
        if (!File.Exists(modelPath)) throw new FileNotFoundException("The graph model file is missing.", modelPath);
        if (!File.Exists(documentPath)) throw new FileNotFoundException("The graph document is missing.", documentPath);

        using var document = JsonDocument.Parse(File.ReadAllText(documentPath));
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw Invalid("graph.json is not an object");
        if (!TryInt(root, "schema_version", out int schema) || schema != GraphContract.DocumentSchemaVersion) throw Invalid("unsupported graph.json schema_version");
        if (!TryString(root, "kind", out var kind) || kind != GraphContract.ArtifactKind) throw Invalid("graph.json is not a graph run");
        if (!root.TryGetProperty("model", out var modelBlock) || modelBlock.ValueKind != JsonValueKind.Object
            || !TryString(modelBlock, "file", out var file) || file != GraphContract.ModelFileName
            || !TryString(modelBlock, "sha256", out var recordedHash))
            throw Invalid("graph.json has no valid model block");

        string actualHash;
        using (var stream = File.OpenRead(modelPath)) actualHash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (!string.Equals(actualHash, recordedHash, StringComparison.OrdinalIgnoreCase)) throw Invalid("graph.onnx does not match its recorded SHA-256");
        cancellationToken.ThrowIfCancellationRequested();

        if (!root.TryGetProperty("contract", out var contractBlock) || contractBlock.ValueKind != JsonValueKind.Object)
            throw Invalid("graph.json has no contract block");
        var documentContract = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in contractBlock.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String) throw Invalid($"contract value '{property.Name}' is not a string");
            documentContract[property.Name] = property.Value.GetString()!;
        }

        using var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL, ExecutionMode = ExecutionMode.ORT_SEQUENTIAL };
        var session = new InferenceSession(modelPath, options);
        try
        {
            var metadata = session.ModelMetadata.CustomMetadataMap;
            var graphKeys = metadata.Keys.Where(k => k.StartsWith(GraphContract.MetadataPrefix, StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
            if (!graphKeys.SetEquals(GraphContract.ContractKeys)) throw Invalid("the model's graph metadata keys are not exactly the graph contract");
            if (!documentContract.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(GraphContract.ContractKeys)) throw Invalid("graph.json contract keys are not exactly the graph contract");
            foreach (var key in GraphContract.ContractKeys)
            {
                if (metadata[key] != documentContract[key]) throw Invalid($"graph.json and the model disagree on '{key}'");
            }

            var info = ParseContract(runDirectory, actualHash, metadata);
            VerifyShapes(session, info);
            return new RuntimeBundle(session, info);
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    private static GraphModelInfo ParseContract(string runDirectory, string modelHash, IReadOnlyDictionary<string, string> metadata)
    {
        if (!int.TryParse(metadata[GraphContract.ContractVersionKey], NumberStyles.None, CultureInfo.InvariantCulture, out int version)
            || version != GraphContract.ContractVersion) throw Invalid("unsupported graph contract version");
        if (metadata[GraphContract.TimeframeKey] != GraphContract.Timeframe) throw Invalid("the graph model is not a daily model");
        if (metadata[GraphContract.ClassOrderKey] != GraphContract.ClassOrder) throw Invalid("unexpected class order");

        string[]? nodes;
        try { nodes = JsonSerializer.Deserialize<string[]>(metadata[GraphContract.NodeOrderKey]); }
        catch (JsonException) { throw Invalid("node_order is not a JSON array of strings"); }
        if (nodes is null || nodes.Length < 1 || nodes.Length > GraphLimits.MaxNodes) throw Invalid("node_order has an unsupported size");
        for (int i = 0; i < nodes.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(nodes[i]) || (i > 0 && string.CompareOrdinal(nodes[i - 1], nodes[i]) >= 0))
                throw Invalid("node_order must be non-empty names in strictly ascending order");
        }

        int window = IntOf(metadata, GraphContract.WindowKey, 1, GraphLimits.MaxWindow);
        int horizon = IntOf(metadata, GraphContract.HorizonKey, 1, int.MaxValue);
        int corr = IntOf(metadata, GraphContract.CorrWindowKey, GraphContract.MinCorrWindow, GraphLimits.MaxWindow);
        int minNodes = IntOf(metadata, GraphContract.MinNodesKey, 1, nodes.Length);
        if (!double.TryParse(metadata[GraphContract.EdgeThresholdKey], NumberStyles.Float, CultureInfo.InvariantCulture, out double edge)
            || !double.IsFinite(edge) || edge <= 0.0 || edge > 1.0) throw Invalid("edge_threshold must be in (0, 1]");
        var mode = PredictionModelMetadata.ParseFeatureMode(metadata[GraphContract.FeatureModeKey]);
        var expectedChannels = mode is null ? null : GraphContract.ChannelsOf(mode.Value);
        if (expectedChannels is null) throw Invalid("the feature mode is not a per-window mode");
        int channels = IntOf(metadata, GraphContract.ChannelsKey, 1, int.MaxValue);
        if (channels != expectedChannels) throw Invalid("channels disagree with the feature mode");
        if ((long)nodes.Length * window * channels * sizeof(float) > GraphLimits.MaxTensorBytes) throw Invalid("the input tensor exceeds the safety bound");

        return new GraphModelInfo(runDirectory, modelHash, ImmutableArray.Create(nodes), window, horizon, corr, edge, minNodes, mode!.Value, channels,
            ImmutableArray.Create(GraphContract.ClassOrder.Split(',')));
    }

    private static void VerifyShapes(InferenceSession session, GraphModelInfo info)
    {
        var expected = new (string Name, int[] Dims)[]
        {
            (GraphContract.InputNodeFeatures, new[] { 1, info.NodeOrder.Length, info.Window, info.Channels }),
            (GraphContract.InputAdjacency, new[] { 1, info.NodeOrder.Length, info.NodeOrder.Length }),
            (GraphContract.InputNodeMask, new[] { 1, info.NodeOrder.Length }),
        };
        if (session.InputMetadata.Count != expected.Length) throw Invalid("the model must have exactly the three graph inputs");
        foreach (var (name, dims) in expected)
        {
            if (!session.InputMetadata.TryGetValue(name, out var meta)) throw Invalid($"input '{name}' is missing");
            CheckNode(meta, dims, $"input '{name}'");
        }

        if (session.OutputMetadata.Count != 1 || !session.OutputMetadata.TryGetValue(GraphContract.OutputName, out var output))
            throw Invalid("the model must have exactly the output 'output'");
        CheckNode(output, new[] { 1, info.NodeOrder.Length, GraphContract.ClassCount }, "output 'output'");
    }

    /// <summary>Float32 tensor of the expected rank; the batch dimension is 1 or dynamic; every static dimension equals the contract.</summary>
    private static void CheckNode(NodeMetadata meta, int[] expected, string what)
    {
        if (!meta.IsTensor || meta.ElementType != typeof(float)) throw Invalid($"{what} must be a float32 tensor");
        var dims = meta.Dimensions;
        if (dims is null || dims.Length != expected.Length) throw Invalid($"{what} has rank {dims?.Length ?? 0}, expected {expected.Length}");
        if (dims[0] is not (1 or -1)) throw Invalid($"{what} must have batch dimension 1 or dynamic");
        for (int i = 1; i < dims.Length; i++)
        {
            if (dims[i] > 0 && dims[i] != expected[i]) throw Invalid($"{what} dimension {i} is {dims[i]}, expected {expected[i]}");
        }
    }

    private static int IntOf(IReadOnlyDictionary<string, string> metadata, string key, int min, int max)
    {
        if (!int.TryParse(metadata[key], NumberStyles.None, CultureInfo.InvariantCulture, out int value) || value < min || value > max)
            throw Invalid($"'{key}' must be an integer in [{min}, {max}]");
        return value;
    }

    private static bool TryInt(JsonElement element, string name, out int value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out value);
    }

    private static bool TryString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String) return false;
        value = property.GetString()!;
        return true;
    }

    private static InvalidDataException Invalid(string message) => new($"Invalid graph model: {message}.");
}
