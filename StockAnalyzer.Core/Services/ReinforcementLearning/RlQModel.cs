using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Models.Training;

namespace StockAnalyzer.Core.Services.ReinforcementLearning;

/// <summary>Strict CPU Q/actor tensor boundary. Supervised classifiers and their output parsers are not involved.</summary>
internal sealed class RlQModel : IDisposable
{
    private static readonly long[] s_inputShape = { 1, RlContract.ObservationLength };
    private static readonly string[] s_inputNames = { RlContract.OnnxInputName };
    private readonly InferenceSession _session;
    private readonly bool _continuous;
    // One owner for the pooled input buffer and its native wrapper: Infer and Dispose serialize on _gate, so the session
    // cannot be freed during native work and concurrent calls never share the buffer.
    private readonly object _gate = new();
    private readonly string[] _outputNames;
    private readonly RunOptions _runOptions = new();
    private float[]? _inputBuffer;
    private OrtValue[]? _inputs;
    private bool _disposed;
    private string OutputName => _continuous ? RlContinuousContract.OutputName : RlContract.OnnxOutputName;
    private int OutputCount => _continuous ? RlContinuousContract.ActionCount : RlContract.ActionCount;
    private string OutputDescription => $"{OutputName}[1,{OutputCount}] ({(_continuous ? "normalized actor actions" : "Q values")})";
    public IReadOnlyDictionary<string, string> Metadata => _session.ModelMetadata.CustomMetadataMap;
    public FixedScaler Scaler { get; }

    public RlQModel(string modelPath, bool continuous = false)
    {
        _continuous = continuous;
        _outputNames = new[] { OutputName };
        using var options = new SessionOptions
        {
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            IntraOpNumThreads = RlContract.CpuThreads,
            InterOpNumThreads = RlContract.CpuThreads,
        };
        _session = new InferenceSession(modelPath, options);
        try
        {
            ValidateMetadata();
            Scaler = FixedScaler.Load(modelPath, Metadata);
            if (Scaler.WindowSize != RlContract.WindowLength || Scaler.Statistics.Length != RlContract.MarketChannelCount
                || Scaler.Lags.Length != 0 || Scaler.ClipSigma is not null)
                throw new InvalidDataException("RL scaler must implement the un-clipped five-channel, 64-bar contract.");
            _inputBuffer = ArrayPool<float>.Shared.Rent(RlContract.ObservationLength);
            _inputs = new[] { OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance, _inputBuffer.AsMemory(0, RlContract.ObservationLength), s_inputShape) };
        }
        catch { ReleaseNative(); throw; }
    }

    private void ValidateMetadata()
    {
        if (_session.InputMetadata.Count != 1 || _session.OutputMetadata.Count != 1
            || !_session.InputMetadata.TryGetValue(RlContract.OnnxInputName, out var input)
            || !_session.OutputMetadata.TryGetValue(OutputName, out var output)
            || input.ElementType != typeof(float) || output.ElementType != typeof(float)
            || !input.Dimensions.SequenceEqual(new[] { 1, RlContract.ObservationLength })
            || !output.Dimensions.SequenceEqual(new[] { 1, OutputCount }))
            throw new InvalidDataException($"RL ONNX requires fixed float32 {RlContract.OnnxInputName}[1,{RlContract.ObservationLength}] and {OutputDescription}.");
        string Required(string key) => Metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value : throw new InvalidDataException($"RL ONNX metadata {key} is required.");
        if (Required("rl_contract_hash") != (_continuous ? RlContinuousContract.ContractHash : RlContract.ContractHash)
            || Required("target_type") != (_continuous ? RlContinuousContract.Target : RlFiles.Target)
            || Required("window_size") != RlContract.WindowLength.ToString(CultureInfo.InvariantCulture)
            || Required("channels") != RlContract.MarketChannelCount.ToString(CultureInfo.InvariantCulture)
            || Required("feature_mode") != RlFiles.FeatureMode || Required("timeframe") != nameof(TimeFrame.D1) || Required("normalization") != "fixed_zscore"
            || Required("lags") != "[]" || Metadata.GetValueOrDefault("clip_sigma") != "")
            throw new InvalidDataException("RL ONNX mathematical/observation contract is incompatible.");
        _ = Required("source_revision"); _ = Required("evaluation_revision"); _ = Required("symbol");
        if (Metadata.TryGetValue("temporal_specification_id", out var temporalRevision))
        {
            if (temporalRevision != RlTemporalContract.SpecificationId
                || Required("temporal_producer_revision") != RlTemporalContract.ProducerRevision
                || !Common.ContentHash.IsLowerHex64(Required("temporal_snapshot_sha256"))
                || (_continuous && !Common.ContentHash.IsLowerHex64(Required("direction_temporal_sha256"))))
                throw new RlTemporalException(RlTemporalRejection.UnsupportedVersion, "ONNX temporal qualification metadata is incompatible.");
        }
        if (_continuous) { _ = Required("frozen_direction_model_id"); _ = Required("frozen_direction_sha256"); }
        var feature = JsonSerializer.Deserialize<FeatureSpec>(Required("feature_spec"), TrainingConfigJson.Options)
            ?? throw new InvalidDataException("RL FeatureSpec is missing.");
        if (!feature.IsValid(out _) || feature.Channels.Count != RlContract.MarketChannelCount || feature.Lags is not { Count: 0 })
            throw new InvalidDataException("RL FeatureSpec must declare exactly OHLC plus registered Volume with no lags.");
        PriceType[] prices = { PriceType.Open, PriceType.High, PriceType.Low, PriceType.Close };
        for (int i = 0; i < prices.Length; i++)
            if (feature.Channels[i].Kind != FeatureChannelKind.Price || feature.Channels[i].Price != prices[i]
                || feature.Channels[i].Normalization != ChannelNormalization.None)
                throw new InvalidDataException("RL price channels must be Open, High, Low, Close in order.");
        var volume = feature.Channels[^1];
        if (volume.Kind != FeatureChannelKind.Indicator || volume.Indicator != IndicatorType.Volume
            || volume.Normalization != ChannelNormalization.None || volume.Params.Count != 0)
            throw new InvalidDataException("RL channel five must be the unnormalized registered Volume indicator.");
    }

    public float[] Infer(float[] observation)
    {
        ValidateObservation(observation);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            observation.AsSpan().CopyTo(_inputBuffer);
            using var results = _session.Run(_runOptions, s_inputNames, _inputs!, _outputNames);
            var output = results[0];
            var info = output.GetTensorTypeAndShape();
            var shape = info.Shape;
            if (info.ElementDataType != TensorElementType.Float || shape.Length != 2 || shape[0] != 1 || shape[1] != OutputCount)
                throw new InvalidDataException($"RL output {OutputDescription} changed its fixed tensor shape.");
            // The span is scoped to the native result; the returned array is a new independently owned copy.
            var q = output.GetTensorDataAsSpan<float>().ToArray();
            if (_continuous)
            {
                foreach (float value in q)
                    if (!float.IsFinite(value)) throw new InvalidDataException("Actor output must be finite.");
            }
            else _ = RlContract.SelectAction(q);
            return q;
        }
    }

    internal static void ValidateObservation(float[] observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        bool valid = observation.Length == RlContract.ObservationLength;
        for (int i = 0; valid && i < observation.Length; i++) valid = float.IsFinite(observation[i]);
        if (!valid) throw new InvalidDataException($"RL observation must contain exactly {RlContract.ObservationLength} finite float32 values.");
    }

    public void ValidateParity(string parityPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(parityPath));
        var observations = doc.RootElement.GetProperty("observations").Deserialize<float[][]>();
        var references = doc.RootElement.GetProperty(_continuous ? RlContinuousContract.ParityField : RlContract.OnnxOutputName).Deserialize<float[][]>();
        if (observations is null || references is null || observations.Length == 0 || observations.Length != references.Length)
            throw new InvalidDataException($"Nonempty paired native observations and {OutputDescription} are required for C# parity.");
        for (int row = 0; row < observations.Length; row++)
        {
            ValidateObservation(observations[row]);
            if (references[row] is null || references[row].Length != OutputCount || references[row].Any(value => !float.IsFinite(value)))
                throw new InvalidDataException($"Native parity {OutputDescription} shape or values are invalid.");
            RlAction referenceAction = _continuous ? RlAction.Hold : RlContract.SelectAction(references[row]);
            float[] actual = Infer(observations[row]);
            for (int i = 0; i < actual.Length; i++)
                if (Math.Abs((double)actual[i] - references[row][i]) > RlContract.AbsoluteTolerance + RlContract.RelativeTolerance * Math.Abs((double)references[row][i]))
                    throw new InvalidDataException($"C# ONNX {OutputDescription} parity failed at row {row}, {(_continuous ? "component" : "action")} {i}.");
            if (!_continuous && RlContract.SelectAction(actual) != referenceAction)
                throw new InvalidDataException($"C# ONNX greedy action parity failed at row {row}; ML tolerance does not waive action agreement.");
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            ReleaseNative();
        }
    }

    private void ReleaseNative()
    {
        if (_inputs is not null) foreach (var input in _inputs) input.Dispose();
        _inputs = null;
        _runOptions.Dispose();
        if (_inputBuffer is not null) ArrayPool<float>.Shared.Return(_inputBuffer);
        _inputBuffer = null;
        _session.Dispose();
    }
}
