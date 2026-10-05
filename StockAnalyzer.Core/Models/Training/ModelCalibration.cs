using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text.Json;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Core.Models.Training;

/// <summary>
/// Constants of the approved D12 temperature calibration (12-4=A, 12-5=A, 12-6=A). Python mirrors them
/// (<c>calibration.py</c>); <c>Tests/ModelCalibrationParity.json</c> keeps both sides identical.
/// </summary>
public static class ModelCalibrationContract
{
    public const string ReferenceKey = "com.stockanalyzer.calibration.ref";
    public const string SidecarSuffix = ".calibration.json";
    public const string SidecarKind = "calibration";
    public const int SchemaVersion = 1;
    public const string Method = "temperature";
    public const string Score = "log_probability";
    public const string AvailableStatus = "available";

    /// <summary>
    /// The fitted temperature sits on the hard-band boundary (no interior optimum, for example perfectly separated data). The sidecar
    /// keeps its normal form for diagnosis but the runtime never applies it.
    /// </summary>
    public const string BoundaryStatus = "temperature_at_boundary";
    public const string NllDefinition = "probability_clamped_epsilon";
    public const string EvaluationPopulation = "release_outer_test";
    public const string FitPopulation = "calibration";
    public const double Epsilon = 1e-15;
    public const int EceBins = 10;
}

/// <summary>
/// Safety bounds of the calibration (code contract, never configuration): a temperature outside the hard band, or a
/// calibration segment below the minimum sample count, is not a valid calibration.
/// </summary>
public static class ModelCalibrationLimits
{
    public const double TemperatureHardMin = 0.01;
    public const double TemperatureHardMax = 100.0;
    public const double TemperatureStandardMin = 0.25;
    public const double TemperatureStandardMax = 4.0;
    public const int MinimumSamples = 100;

    public static bool IsWithinHardBand(double temperature)
        => double.IsFinite(temperature) && temperature >= TemperatureHardMin && temperature <= TemperatureHardMax;

    public static bool IsWithinStandardBand(double temperature)
        => double.IsFinite(temperature) && temperature >= TemperatureStandardMin && temperature <= TemperatureStandardMax;
}

public sealed record CalibrationPeriod(string Symbol, string Kind, DateTime AnchorStart, DateTime AnchorEnd,
    int SampleCount, string AnchorRevision);

/// <summary><see cref="EceEqualMass"/>: top-label ECE over equal-count confidence bins (absent in older sidecars).</summary>
public sealed record CalibrationScores(double Nll, double Brier, double Ece, double? EceEqualMass = null);

/// <summary>One equal-width confidence bin; the mean confidence and accuracy are absent when the bin is empty.</summary>
public sealed record ReliabilityBin(int Count, double? MeanConfidence, double? Accuracy);

public sealed record CalibrationReliability(ImmutableArray<ReliabilityBin> Before, ImmutableArray<ReliabilityBin> After);

public sealed record CalibrationMetrics(int SampleCount, CalibrationScores Before, CalibrationScores After,
    CalibrationReliability? Reliability = null);

public sealed record CalibrationEvaluation(CalibrationMetrics CalibrationSet, CalibrationMetrics OuterTest);

public sealed record CalibrationFit(string Population, int SampleCount, ImmutableArray<CalibrationPeriod> Periods,
    string FitDataRevision, int EceBins);

/// <summary>
/// Immutable post-hoc temperature calibration bound to exactly one ONNX generation:
/// <c>s_j = log(max(p_j, ε))</c>, <c>q = softmax(s / T)</c>. For <c>T &gt; 0</c> the transform is monotone in every
/// probability, so the predicted class never changes.
/// </summary>
public sealed record ModelCalibration(int SchemaVersion, string ModelSha256, string DataRevision,
    string FeatureContractHash, string ClassOrder, string Method, string Score, double Epsilon,
    double Temperature, string Status, CalibrationFit Fit, CalibrationEvaluation Evaluation,
    string? NllDefinition = null, string? EvaluationPopulation = null, bool? EvaluationIsOos = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    };

    public static string SidecarPath(string modelPath) => modelPath + ModelCalibrationContract.SidecarSuffix;

    /// <summary>True when predictions apply the temperature; a boundary solution is recorded but not applied.</summary>
    public bool IsEnabled => Status == ModelCalibrationContract.AvailableStatus;

    /// <summary>Loads the sidecar of an ONNX file; the model hash is computed from the file.</summary>
    public static ModelCalibration Load(string modelPath, IReadOnlyDictionary<string, string> metadata)
        => Load(modelPath, metadata, ContentHash.OfFile(modelPath), expectedDataRevision: null);

    /// <summary>
    /// Loads and validates the sidecar. <paramref name="expectedDataRevision"/> is checked only when supplied (the
    /// registry owns the evaluation revision; the runtime does not).
    /// </summary>
    public static ModelCalibration Load(string modelPath, IReadOnlyDictionary<string, string> metadata,
        string expectedModelHash, string? expectedDataRevision)
    {
        if (metadata.GetValueOrDefault(ModelCalibrationContract.ReferenceKey) != Path.GetFileName(modelPath) + ModelCalibrationContract.SidecarSuffix)
            throw new InvalidDataException("ONNX calibration reference must name its own generation sidecar.");
        var path = SidecarPath(modelPath);
        if (!File.Exists(path)) throw new FileNotFoundException("Required calibration sidecar is missing.", path);
        ModelCalibration calibration;
        try
        {
            calibration = JsonSerializer.Deserialize<ModelCalibration>(File.ReadAllText(path), JsonOptions)
                ?? throw new InvalidDataException("Model calibration is empty.");
        }
        catch (JsonException ex) { throw new InvalidDataException("Model calibration is not valid JSON.", ex); }
        calibration.Validate(metadata, expectedModelHash, expectedDataRevision);
        return calibration;
    }

    public void Validate(IReadOnlyDictionary<string, string> metadata, string modelHash, string? dataRevision)
    {
        static bool Hash(string? text) => ContentHash.IsLowerHex64(text);
        static void Require([DoesNotReturnIf(false)] bool value) { if (!value) throw new InvalidDataException("Model calibration contract is invalid."); }
        Require(SchemaVersion == ModelCalibrationContract.SchemaVersion && Hash(ModelSha256) && Hash(DataRevision)
            && ModelSha256 == modelHash && (dataRevision is null || DataRevision == dataRevision)
            && FeatureContractHash == FixedScaler.ComputeContractHash(metadata)
            && !string.IsNullOrWhiteSpace(ClassOrder)
            && ClassOrder == metadata.GetValueOrDefault(PredictionModelMetadata.ClassOrderKey)
            && Method == ModelCalibrationContract.Method && Score == ModelCalibrationContract.Score
            && Epsilon == ModelCalibrationContract.Epsilon
            && Status is ModelCalibrationContract.AvailableStatus or ModelCalibrationContract.BoundaryStatus
            && (NllDefinition is null || NllDefinition == ModelCalibrationContract.NllDefinition)
            && (EvaluationPopulation is null || EvaluationPopulation == ModelCalibrationContract.EvaluationPopulation)
            && (EvaluationIsOos is null or true)
            && ModelCalibrationLimits.IsWithinHardBand(Temperature)
            && Fit is not null && Evaluation is not null);
        Require(Fit.Population == ModelCalibrationContract.FitPopulation && Fit.EceBins == ModelCalibrationContract.EceBins
            && Fit.SampleCount >= ModelCalibrationLimits.MinimumSamples && Hash(Fit.FitDataRevision)
            && !Fit.Periods.IsDefaultOrEmpty);
        long counted = 0;
        foreach (var period in Fit.Periods)
        {
            Require(period is not null && !string.IsNullOrWhiteSpace(period.Symbol)
                && period.Kind == ModelCalibrationContract.FitPopulation && period.SampleCount > 0
                && period.AnchorStart != default && period.AnchorStart <= period.AnchorEnd && Hash(period.AnchorRevision));
            counted += period.SampleCount;
        }
        Require(counted == Fit.SampleCount && Fit.Periods.Select(p => p.Symbol).Distinct(StringComparer.Ordinal).Count() == Fit.Periods.Length);
        Require(ValidMetrics(Evaluation.CalibrationSet) && ValidMetrics(Evaluation.OuterTest)
            && Evaluation.CalibrationSet.SampleCount == Fit.SampleCount);

        static bool ValidMetrics(CalibrationMetrics? m) => m is not null && m.SampleCount > 0
            && ValidScores(m.Before) && ValidScores(m.After) && (m.Reliability is null || ValidReliability(m.Reliability, m.SampleCount));
        static bool ValidScores(CalibrationScores? s) => s is not null && double.IsFinite(s.Nll) && s.Nll >= 0
            && double.IsFinite(s.Brier) && s.Brier >= 0 && double.IsFinite(s.Ece) && s.Ece is >= 0 and <= 1
            && (s.EceEqualMass is not { } equalMass || (double.IsFinite(equalMass) && equalMass is >= 0 and <= 1));
        static bool ValidReliability(CalibrationReliability r, int sampleCount) => ValidBins(r.Before, sampleCount) && ValidBins(r.After, sampleCount);
        static bool ValidBins(ImmutableArray<ReliabilityBin> bins, int sampleCount)
        {
            if (bins.IsDefault || bins.Length != ModelCalibrationContract.EceBins) return false;
            long total = 0;
            foreach (var bin in bins)
            {
                static bool Unit(double? v) => v is { } x && double.IsFinite(x) && x is >= 0 and <= 1;
                if (bin is null || bin.Count < 0 || (bin.Count > 0) != (bin.MeanConfidence.HasValue && bin.Accuracy.HasValue)
                    || (bin.Count > 0 && !(Unit(bin.MeanConfidence) && Unit(bin.Accuracy)))
                    || (bin.Count == 0 && (bin.MeanConfidence.HasValue || bin.Accuracy.HasValue))) return false;
                total += bin.Count;
            }
            return total == sampleCount;
        }
    }

    public ImmutableArray<string> Classes => ClassOrder.Split(',').ToImmutableArray();

    /// <summary>
    /// Writes <c>softmax(log(max(p, ε)) / T)</c> into <paramref name="calibrated"/>. The input must be a valid
    /// probability row (finite, in [0,1], summing to one within the shared tolerance).
    /// </summary>
    public void Apply(ReadOnlySpan<float> probabilities, Span<float> calibrated)
    {
        if (probabilities.Length != calibrated.Length || probabilities.Length == 0)
            throw new ArgumentException("Calibration input and output must have the same non-zero width.");
        double sum = 0, maxScore = double.NegativeInfinity;
        Span<double> scores = probabilities.Length <= IMLDataProcessor.MaxStackAllocClassCount ? stackalloc double[probabilities.Length] : new double[probabilities.Length];
        for (int i = 0; i < probabilities.Length; i++)
        {
            float p = probabilities[i];
            if (!float.IsFinite(p) || p < 0f || p > 1f)
                throw new InvalidDataException("Calibration input contains a probability outside [0,1].");
            sum += p;
            scores[i] = Math.Log(Math.Max((double)p, Epsilon)) / Temperature;
            if (scores[i] > maxScore) maxScore = scores[i];
        }
        if (Math.Abs(sum - 1.0) > IMLDataProcessor.SoftmaxSumTolerance)
            throw new InvalidDataException("Calibration input probabilities do not sum to one.");
        double total = 0;
        for (int i = 0; i < scores.Length; i++)
        {
            scores[i] = Math.Exp(scores[i] - maxScore);
            total += scores[i];
        }
        for (int i = 0; i < scores.Length; i++) calibrated[i] = (float)(scores[i] / total);
    }
}
