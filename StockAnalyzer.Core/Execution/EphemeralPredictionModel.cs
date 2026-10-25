using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.ML.OnnxRuntime;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
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
/// Runs an ephemeral model (a leased artifact) over bars. The contract is read from the model itself, so no configuration is needed, and the input is
/// built by the same <see cref="IMLDataProcessor"/> the application's prediction service uses.
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
    private readonly PredictionFeatureMode _mode;
    private readonly FeatureSpec? _featureSpec;
    private readonly FixedScaler? _scaler;
    private readonly ModelCalibration? _calibration;
    private readonly IIndicatorFactory? _indicatorFactory;
    private readonly int _leadBars;

    private EphemeralPredictionModel(
        InferenceSession session,
        IMLDataProcessor processor,
        EphemeralModelContract contract,
        string inputName,
        string outputName,
        int featuresPerBar,
        PredictionFeatureMode mode,
        FeatureSpec? featureSpec,
        FixedScaler? scaler,
        ModelCalibration? calibration,
        IIndicatorFactory? indicatorFactory,
        int leadBars)
    {
        _session = session;
        _processor = processor;
        Contract = contract;
        _inputName = inputName;
        _outputName = outputName;
        _featuresPerBar = featuresPerBar;
        _inputShape = new long[] { 1, contract.WindowSize, featuresPerBar };
        _mode = mode;
        _featureSpec = featureSpec;
        _scaler = scaler;
        _calibration = calibration;
        _indicatorFactory = indicatorFactory;
        _leadBars = leadBars;
    }

    public EphemeralModelContract Contract { get; }

    /// <summary>Number of lead/warmup bars before the inference window required for lag offsets, log returns, or indicator calculation.</summary>
    public int LeadBars => _leadBars;

    /// <exception cref="EphemeralModelContractException">The model cannot be run under the contract.</exception>
    public static EphemeralPredictionModel Open(ArtifactLease lease, IMLDataProcessor processor) =>
        Open(lease, processor, EphemeralExecutionPolicy.McpFixed);

    /// <exception cref="EphemeralModelContractException">The model cannot be run under the contract or policy.</exception>
    public static EphemeralPredictionModel Open(
        ArtifactLease lease,
        IMLDataProcessor processor,
        EphemeralExecutionPolicy? policy,
        IIndicatorFactory? indicatorFactory = null,
        int maxComposedChannels = 64,
        long maxTensorBytes = 64L * 1024 * 1024)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(processor);
        var effectivePolicy = policy ?? EphemeralExecutionPolicy.McpFixed;
        InferenceSession? session = null;
        try
        {
            session = new InferenceSession(lease.OnnxPath);
            IReadOnlyDictionary<string, string> metadata = session.ModelMetadata.CustomMetadataMap;
            PredictionOutputContract output = PredictionModelMetadata.ReadOutputContract(metadata);
            bool regression = output.Semantic == PredictionOutputSemantic.LogReturn;
            (PredictionFeatureMode mode, int window, FeatureSpec? ownSpec) = PredictionModelMetadata.ReadOwnFeatureContract(metadata);

            if (effectivePolicy.RequiredFeatureMode is { } requiredMode && mode != requiredMode)
            {
                throw new EphemeralModelContractException($"The model uses the feature mode {mode}; only {requiredMode} is supported.");
            }

            bool requiresScaler = PredictionModelMetadata.RequiresFixedScaler(metadata);
            bool hasCalibration = metadata.ContainsKey(ModelCalibrationContract.ReferenceKey);

            if ((!effectivePolicy.AllowFixedScaler && requiresScaler) || (!effectivePolicy.AllowCalibration && hasCalibration))
            {
                throw new EphemeralModelContractException("The model needs a fixed scaler or a calibration, which ephemeral models do not use.");
            }

            FeatureSpec? featureSpec = ownSpec;
            if (mode == PredictionFeatureMode.ComposedFeatures)
            {
                if (featureSpec is null)
                {
                    throw new EphemeralModelContractException("The model uses ComposedFeatures mode but declares no feature specification.");
                }

                PredictionService.ValidateComposedFeatureSpec(featureSpec, maxComposedChannels, indicatorFactory!);
            }

            FixedScaler? scaler = null;
            if (requiresScaler)
            {
                scaler = FixedScaler.Load(lease.OnnxPath, metadata);
                if (featureSpec is not null)
                {
                    PredictionService.ValidateComposedFeatureSpec(featureSpec, maxComposedChannels, indicatorFactory!);
                }
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

            ModelCalibration? calibration = null;
            if (hasCalibration)
            {
                if (regression)
                {
                    throw new EphemeralModelContractException("Calibration cannot be applied to a regression model.");
                }

                calibration = ModelCalibration.Load(lease.OnnxPath, metadata);
                if (calibration.ClassOrder != string.Join(",", labels))
                {
                    throw new EphemeralModelContractException("Model calibration class order disagrees with the configured class labels.");
                }
            }

            string inputName = PredictionService.ResolveNodeName(session.InputMetadata.Keys, null);
            string outputName = PredictionService.ResolveNodeName(session.OutputMetadata.Keys, null);
            int featuresPerBar = scaler?.Statistics.Length ?? PredictionService.ResolveFeaturesPerBar(mode, featureSpec);
            PredictionService.ValidateModelContract(
                session.InputMetadata[inputName], session.OutputMetadata[outputName], window, featuresPerBar, regression ? 1 : labels.Length, inputName, outputName);

            int maxLag = (scaler?.Lags ?? featureSpec?.Lags ?? Array.Empty<int>()).DefaultIfEmpty(0).Max();
            int returnLead = mode is PredictionFeatureMode.LogReturn or PredictionFeatureMode.LogReturnOhlc ? 1 : 0;
            int leadBars = Math.Max(maxLag, returnLead);

            if (featureSpec is not null)
            {
                int indicatorWarmup = 0;
                foreach (var channel in featureSpec.Channels)
                {
                    if (channel.Kind == FeatureChannelKind.Indicator)
                    {
                        int channelWarmup = 14;

                        // Resolve via IIndicatorFactory (SSoT) to capture live registry defaults when available:
                        if (indicatorFactory is not null && channel.Indicator is not null)
                        {
                            try
                            {
                                var paramObj = FeatureChannelConverter.ResolveParameterObject(channel, indicatorFactory, out _);
                                if (paramObj is not null)
                                {
                                    channelWarmup = Math.Max(channelWarmup, paramObj.GetRequiredWarmupBars());
                                    foreach (var prop in paramObj.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                                    {
                                        if (prop.CanRead && (prop.PropertyType == typeof(int) || prop.PropertyType == typeof(int?)))
                                        {
                                            var val = prop.GetValue(paramObj);
                                            if (val is int intVal && intVal > 0)
                                            {
                                                channelWarmup = Math.Max(channelWarmup, intVal);
                                            }
                                        }
                                    }
                                }
                            }
                            catch
                            {
                                // Fallback to explicit params parsing below if factory resolution fails
                            }
                        }

                        if (channel.Params is not null)
                        {
                            foreach (var (_, val) in channel.Params)
                            {
                                if (int.TryParse(val, out var parsedInt) && parsedInt > 0)
                                {
                                    channelWarmup = Math.Max(channelWarmup, parsedInt);
                                }
                            }
                        }
                        indicatorWarmup = Math.Max(indicatorWarmup, channelWarmup);
                    }
                }

                if (indicatorWarmup > 0)
                {
                    leadBars = Math.Max(leadBars, indicatorWarmup + maxLag);
                }
            }

            var contract = new EphemeralModelContract(window, mode.ToString(), regression, labels, output.HorizonBars, lease.Artifact.Sha256);
            var model = new EphemeralPredictionModel(
                session, processor, contract, inputName, outputName, featuresPerBar, mode, featureSpec, scaler, calibration, indicatorFactory, leadBars);
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
    /// no later bar is ever read. <paramref name="history"/> must be strictly chronological and hold at least <c>WindowSize + count - 1 + LeadBars</c> bars.
    /// </summary>
    public IReadOnlyList<EphemeralPrediction> PredictLast(IReadOnlyList<CandleData> history, int count)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        int window = Contract.WindowSize;
        int minRequired = window + count - 1 + _leadBars;
        if (history.Count < minRequired)
        {
            throw new ArgumentException($"At least {minRequired} bars are needed; {history.Count} were given.", nameof(history));
        }

        PredictionService.ValidateChronologicalHistory(history);
        var results = new List<EphemeralPrediction>(count);
        float[] buffer = new float[window * _featuresPerBar];
        for (int offset = 0; offset < count; offset++)
        {
            int end = history.Count - count + 1 + offset;
            int startIndex = end - window;
            try
            {
                PredictionService.PopulateWindowFeatures(
                    history, startIndex, window, buffer.Length, _mode, _featureSpec, _scaler, _processor, _indicatorFactory, buffer);
            }
            catch (Exception ex) when (ex is not EphemeralModelContractException)
            {
                throw new EphemeralModelContractException("The model features could not be populated.", ex);
            }

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

        Span<float> probabilities = classCount <= IMLDataProcessor.MaxStackAllocClassCount ? stackalloc float[classCount] : new float[classCount];
        if (_calibration is { IsEnabled: true } calibration)
        {
            calibration.Apply(values, probabilities);
        }
        else
        {
            values.CopyTo(probabilities);
        }

        (float confidence, _) = _processor.ComputeConfidenceAndEntropy(probabilities);
        var scores = new ClassScore[classCount];
        int best = 0;
        for (int index = 0; index < classCount; index++)
        {
            scores[index] = new ClassScore(Contract.ClassLabels[index], probabilities[index]);
            if (probabilities[index] > probabilities[best])
            {
                best = index;
            }
        }

        return new EphemeralPrediction(scores, Contract.ClassLabels[best], confidence, null);
    }
}
