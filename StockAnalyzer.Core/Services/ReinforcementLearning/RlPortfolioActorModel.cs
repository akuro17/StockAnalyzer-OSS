using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Models.Training;

namespace StockAnalyzer.Core.Services.ReinforcementLearning;

/// <summary>
/// Strict CPU tensor boundary of the portfolio actor: one float32 <c>observation[1, L]</c> in, one float32 <c>logits[1, N_max + 1]</c> out
/// (cash first). The model only proposes logits; masks, weights and every money value are recomputed by Core. Metadata and scaler sidecar are
/// validated against <see cref="RlPortfolioContract"/>. The pooled input buffer follows the pattern of <c>RlQModel</c>, which is not shared
/// because its shapes and contract are those of the single-symbol targets.
/// </summary>
internal sealed class RlPortfolioActorModel : IDisposable
{
    private static readonly string[] s_inputNames = { RlPortfolioContract.OnnxInputName };
    private static readonly string[] s_outputNames = { RlPortfolioContract.OnnxOutputName };
    private readonly InferenceSession _session;
    private readonly object _gate = new();
    private readonly RunOptions _runOptions = new();
    private readonly int _observationLength;
    private readonly int _logitCount;
    private float[]? _inputBuffer;
    private OrtValue[]? _inputs;
    private bool _disposed;

    public IReadOnlyDictionary<string, string> Metadata => _session.ModelMetadata.CustomMetadataMap;
    /// <summary>Number of asset slots (N_max) the model was exported for.</summary>
    public int MaxSymbols { get; }
    public FixedScaler Scaler { get; }
    /// <summary>The observation variant the model was exported for (named by its contract-hash metadata).</summary>
    public RlPortfolioObservationLayout Layout { get; private set; } = RlPortfolioObservationLayout.V1;

    public RlPortfolioActorModel(string modelPath)
    {
        using var options = new SessionOptions
        {
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            IntraOpNumThreads = RlContract.CpuThreads,
            InterOpNumThreads = RlContract.CpuThreads,
        };
        _session = new InferenceSession(modelPath, options);
        try
        {
            MaxSymbols = ValidateMetadata();
            _observationLength = checked((int)RlPortfolioResources.ObservationLength(MaxSymbols, Layout));
            _logitCount = checked((int)RlPortfolioResources.ActionLength(MaxSymbols));
            ValidateShapes();
            Scaler = RlPortfolioScaler.Load(modelPath, Metadata);
            _inputBuffer = ArrayPool<float>.Shared.Rent(_observationLength);
            _inputs = new[] { OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance,
                _inputBuffer.AsMemory(0, _observationLength), new long[] { 1, _observationLength }) };
        }
        catch { ReleaseNative(); throw; }
    }

    private int ValidateMetadata()
    {
        string Required(string key) => Metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value : throw new InvalidDataException($"RL portfolio ONNX metadata {key} is required.");
        if (!RlPortfolioObservationLayout.TryFromContractHash(Required(RlPortfolioContract.MetadataContractHash), out var layout)
            || Required(RlPortfolioContract.MetadataTargetType) != RlPortfolioContract.TargetType
            || Required(PredictionModelMetadata.WindowSizeKey) != RlContract.WindowLength.ToString(CultureInfo.InvariantCulture)
            || Required(RlPortfolioContract.MetadataChannels) != layout.MarketChannelCount.ToString(CultureInfo.InvariantCulture))
            throw new InvalidDataException("RL portfolio ONNX mathematical/observation contract is incompatible.");
        Layout = layout;
        if (!ContentHash.IsLowerHex64(Required(RlPortfolioContract.MetadataUniverseSha256))
            || !ContentHash.IsLowerHex64(Required(RlPortfolioContract.MetadataCalendarSha256)))
            throw new InvalidDataException("RL portfolio ONNX identity hashes are malformed.");
        _ = Required(RlPortfolioContract.MetadataEvaluationSpecification);
        _ = Required(PredictionModelMetadata.ScalerReferenceKey);
        if (!int.TryParse(Required(RlPortfolioContract.MetadataMaxSymbols), NumberStyles.None, CultureInfo.InvariantCulture, out int maxSymbols)
            || maxSymbols < RlPortfolioContract.MinimumSymbols)
            throw new InvalidDataException("RL portfolio ONNX n_max is invalid.");
        return maxSymbols;
    }

    private void ValidateShapes()
    {
        if (_session.InputMetadata.Count != 1 || _session.OutputMetadata.Count != 1
            || !_session.InputMetadata.TryGetValue(RlPortfolioContract.OnnxInputName, out var input)
            || !_session.OutputMetadata.TryGetValue(RlPortfolioContract.OnnxOutputName, out var output)
            || input.ElementType != typeof(float) || output.ElementType != typeof(float)
            || !input.Dimensions.SequenceEqual(new[] { 1, _observationLength })
            || !output.Dimensions.SequenceEqual(new[] { 1, _logitCount }))
            throw new InvalidDataException($"RL portfolio ONNX requires fixed float32 {RlPortfolioContract.OnnxInputName}[1,{_observationLength}] "
                + $"and {RlPortfolioContract.OnnxOutputName}[1,{_logitCount}].");
    }

    /// <summary>Logits (cash first, then one per asset slot) for one observation. Every output value must be finite.</summary>
    public float[] Infer(float[] observation)
    {
        ValidateObservation(observation);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            observation.AsSpan().CopyTo(_inputBuffer);
            using var results = _session.Run(_runOptions, s_inputNames, _inputs!, s_outputNames);
            var output = results[0];
            var info = output.GetTensorTypeAndShape();
            var shape = info.Shape;
            if (info.ElementDataType != TensorElementType.Float || shape.Length != 2 || shape[0] != 1 || shape[1] != _logitCount)
                throw new InvalidDataException($"RL portfolio output {RlPortfolioContract.OnnxOutputName}[1,{_logitCount}] changed its fixed tensor shape.");
            var logits = output.GetTensorDataAsSpan<float>().ToArray();
            if (logits.Any(value => !float.IsFinite(value))) throw new InvalidDataException("RL portfolio output logits must be finite.");
            return logits;
        }
    }

    /// <summary>One decision: observation -> logits -> quantized weights. <paramref name="slotMask"/> has one flag per slot (N_max).</summary>
    public RlPortfolioWeightVector Decide(float[] observation, bool[] slotMask, int maxWeightPercent)
    {
        ArgumentNullException.ThrowIfNull(slotMask);
        if (slotMask.Length != MaxSymbols) throw new ArgumentException("One mask flag per asset slot is required.", nameof(slotMask));
        return RlPortfolioWeights.FromLogits(Infer(observation), slotMask, maxWeightPercent);
    }

    /// <summary>
    /// Compares the model with a native reference file <c>{ "observations": [[..]], "logits": [[..]] }</c> within the RL tolerances, and requires
    /// identical greedy weight vectors (an ML tolerance never waives agreement of the decision).
    /// </summary>
    public void ValidateParity(string parityPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(parityPath));
        var observations = doc.RootElement.GetProperty("observations").Deserialize<float[][]>();
        var references = doc.RootElement.GetProperty(RlPortfolioContract.OnnxOutputName).Deserialize<float[][]>();
        if (observations is null || references is null || observations.Length == 0 || observations.Length != references.Length)
            throw new InvalidDataException("Nonempty paired native observations and logits are required for C# parity.");
        var allSlots = Enumerable.Repeat(true, MaxSymbols).ToArray();
        for (int row = 0; row < observations.Length; row++)
        {
            if (references[row] is null || references[row].Length != _logitCount || references[row].Any(v => !float.IsFinite(v)))
                throw new InvalidDataException("Native parity logits shape or values are invalid.");
            float[] actual = Infer(observations[row]);
            for (int i = 0; i < actual.Length; i++)
                if (Math.Abs((double)actual[i] - references[row][i]) > RlContract.AbsoluteTolerance + RlContract.RelativeTolerance * Math.Abs((double)references[row][i]))
                    throw new InvalidDataException($"C# ONNX portfolio logits parity failed at row {row}, logit {i}.");
            var expectedWeights = RlPortfolioWeights.FromLogits(references[row], allSlots, RlPortfolioLimits100);
            var actualWeights = RlPortfolioWeights.FromLogits(actual, allSlots, RlPortfolioLimits100);
            if (!expectedWeights.Assets.SequenceEqual(actualWeights.Assets) || expectedWeights.Cash != actualWeights.Cash)
                throw new InvalidDataException($"C# ONNX greedy weight parity failed at row {row}; ML tolerance does not waive weight agreement.");
        }
    }

    private static int RlPortfolioLimits100 => StockAnalyzer.Core.Models.Settings.RlPortfolioLimits.MaximumWeightPercentCeiling;

    internal void ValidateObservation(float[] observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        bool valid = observation.Length == _observationLength;
        for (int i = 0; valid && i < observation.Length; i++) valid = float.IsFinite(observation[i]);
        if (!valid) throw new InvalidDataException($"RL portfolio observation must contain exactly {_observationLength} finite float32 values.");
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
