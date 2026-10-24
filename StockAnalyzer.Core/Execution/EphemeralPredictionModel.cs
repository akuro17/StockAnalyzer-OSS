using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Core.Execution;

/// <summary>The model does not follow the prediction contract the gateway's trainer writes (or cannot be run). Not a client mistake.</summary>
public sealed class EphemeralModelContractException : InvalidOperationException
{
    public EphemeralModelContractException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

/// <summary>What a produced model declares about itself (its <c>metadata_props</c>), without any path.</summary>
public sealed record EphemeralModelContract(
    int WindowSize,
    string FeatureMode,
    bool IsRegression,
    IReadOnlyList<string> ClassLabels,
    int HorizonBars,
    string Sha256);

/// <summary>One prediction. A classifier has <see cref="Scores"/>, <see cref="PredictedClass"/> and <see cref="Confidence"/>; a regressor has <see cref="Value"/> (a log return).</summary>
public sealed record EphemeralPrediction(IReadOnlyList<ClassScore> Scores, string? PredictedClass, float? Confidence, float? Value);

/// <summary>
/// Runs an ephemeral model (a leased artifact) over daily bars. The contract is read from the model itself, so no configuration is needed, and the input is
/// built by the same <see cref="IMLDataProcessor"/> the application's prediction service uses. Only what the gateway's trainer produces is supported
/// (OHLCV min-max features, no fixed scaler, no calibration); anything else is an <see cref="EphemeralModelContractException"/>.
/// The lease must stay open while the model is used.
/// </summary>
public sealed class EphemeralPredictionModel : IDisposable
{
    private readonly InferenceSession _session;
    private readonly RunOptions _runOptions = new();
    private readonly IMLDataProcessor _processor;
    private readonly string _inputName;
    private readonly string _outputName;
    private readonly int _featuresPerBar;
    private readonly long[] _inputShape;

    private EphemeralPredictionModel(InferenceSession session, IMLDataProcessor processor, EphemeralModelContract contract, string inputName, string outputName, int featuresPerBar)
    {
        _session = session;
        _processor = processor;
        Contract = contract;
        _inputName = inputName;
        _outputName = outputName;
        _featuresPerBar = featuresPerBar;
        _inputShape = new long[] { 1, contract.WindowSize, featuresPerBar };
    }

    public EphemeralModelContract Contract { get; }

    /// <exception cref="EphemeralModelContractException">The model cannot be run under the contract.</exception>
    public static EphemeralPredictionModel Open(ArtifactLease lease, IMLDataProcessor processor)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(processor);
        InferenceSession? session = null;
        try
        {
            session = new InferenceSession(lease.OnnxPath);
            IReadOnlyDictionary<string, string> metadata = session.ModelMetadata.CustomMetadataMap;
            PredictionOutputContract output = PredictionModelMetadata.ReadOutputContract(metadata);
            bool regression = output.Semantic == PredictionOutputSemantic.LogReturn;
            (PredictionFeatureMode mode, int window, _) = PredictionModelMetadata.ReadOwnFeatureContract(metadata);
            if (mode != EphemeralFixedValues.Current.FeatureMode)
            {
                throw new EphemeralModelContractException($"The model uses the feature mode {mode}; only {EphemeralFixedValues.Current.FeatureMode} is supported.");
            }

            if (PredictionModelMetadata.RequiresFixedScaler(metadata) || metadata.ContainsKey(ModelCalibrationContract.ReferenceKey))
            {
                throw new EphemeralModelContractException("The model needs a fixed scaler or a calibration, which ephemeral models do not use.");
            }

            string[] labels = Array.Empty<string>();
            if (!regression)
            {
                if (!metadata.TryGetValue(PredictionModelMetadata.ClassOrderKey, out string? classOrder) || !PredictionModelMetadata.HasValidClassOrder(classOrder))
                {
                    throw new EphemeralModelContractException("The model does not declare a valid class order.");
                }

                labels = classOrder.Split(',');
            }

            string inputName = PredictionService.ResolveNodeName(session.InputMetadata.Keys, null);
            string outputName = PredictionService.ResolveNodeName(session.OutputMetadata.Keys, null);
            int featuresPerBar = PredictionService.ResolveFeaturesPerBar(mode, null);
            PredictionService.ValidateModelContract(
                session.InputMetadata[inputName], session.OutputMetadata[outputName], window, featuresPerBar, regression ? 1 : labels.Length, inputName, outputName);

            var contract = new EphemeralModelContract(window, mode.ToString(), regression, labels, output.HorizonBars, lease.Artifact.Sha256);
            var model = new EphemeralPredictionModel(session, processor, contract, inputName, outputName, featuresPerBar);
            session = null;
            return model;
        }
        catch (EphemeralModelContractException)
        {
            throw;
        }
        catch (Exception error) when (error is InvalidOperationException or OnnxRuntimeException or System.IO.InvalidDataException or KeyNotFoundException)
        {
            throw new EphemeralModelContractException("The model does not follow the prediction contract.", error);
        }
        finally
        {
            session?.Dispose();
        }
    }

    /// <summary>
    /// The predictions at the last <paramref name="count"/> bars of <paramref name="history"/> (oldest first), each from the window that ends at its own bar:
    /// no later bar is ever read. <paramref name="history"/> must be strictly chronological and hold at least <c>WindowSize + count - 1</c> bars.
    /// </summary>
    public IReadOnlyList<EphemeralPrediction> PredictLast(IReadOnlyList<CandleData> history, int count)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        int window = Contract.WindowSize;
        if (history.Count < window + count - 1)
        {
            throw new ArgumentException($"At least {window + count - 1} bars are needed; {history.Count} were given.", nameof(history));
        }

        PredictionService.ValidateChronologicalHistory(history);
        var results = new List<EphemeralPrediction>(count);
        float[] buffer = new float[window * _featuresPerBar];
        for (int offset = 0; offset < count; offset++)
        {
            int end = history.Count - count + 1 + offset;
            _processor.NormalizeCandles(history, end - window, window, buffer);
            results.Add(Run(buffer));
        }

        return results;
    }

    public void Dispose()
    {
        _runOptions.Dispose();
        _session.Dispose();
    }

    private EphemeralPrediction Run(float[] buffer)
    {
        try
        {
            using var input = OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance, buffer.AsMemory(), _inputShape);
            using IDisposableReadOnlyCollection<OrtValue> outputs = _session.Run(_runOptions, new[] { _inputName }, new[] { input }, new[] { _outputName });
            ReadOnlySpan<float> values = outputs[0].GetTensorDataAsSpan<float>();
            return Contract.IsRegression ? Regression(values) : Classification(values);
        }
        catch (OnnxRuntimeException error)
        {
            throw new EphemeralModelContractException("The model could not be run.", error);
        }
    }

    private static EphemeralPrediction Regression(ReadOnlySpan<float> values)
    {
        try
        {
            return new EphemeralPrediction(Array.Empty<ClassScore>(), null, null, PredictionService.ExtractRegressionValue(values));
        }
        catch (InvalidOperationException error)
        {
            throw new EphemeralModelContractException(error.Message, error);
        }
    }

    private EphemeralPrediction Classification(ReadOnlySpan<float> values)
    {
        int classCount = Contract.ClassLabels.Count;
        if (values.Length != classCount)
        {
            throw new EphemeralModelContractException($"The model produced {values.Length} output value(s) for {classCount} classes.");
        }

        float sum = 0f;
        for (int index = 0; index < classCount; index++)
        {
            float probability = values[index];
            if (!float.IsFinite(probability) || probability < 0f || probability > 1f)
            {
                throw new EphemeralModelContractException("The model output declared class probabilities but holds invalid values.");
            }

            sum += probability;
        }

        if (MathF.Abs(1.0f - sum) > IMLDataProcessor.SoftmaxSumTolerance)
        {
            throw new EphemeralModelContractException("The model output declared class probabilities but they do not sum to 1.");
        }

        (float confidence, _) = _processor.ComputeConfidenceAndEntropy(values);
        var scores = new ClassScore[classCount];
        int best = 0;
        for (int index = 0; index < classCount; index++)
        {
            scores[index] = new ClassScore(Contract.ClassLabels[index], values[index]);
            if (values[index] > values[best])
            {
                best = index;
            }
        }

        return new EphemeralPrediction(scores, Contract.ClassLabels[best], confidence, null);
    }
}
