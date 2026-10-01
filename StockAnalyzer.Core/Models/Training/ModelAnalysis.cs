using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace StockAnalyzer.Core.Models.Training;

/// <summary>Approved D09/D18 defaults. Python mirrors these via acceptance fixtures.</summary>
public static class ModelAnalysisContract
{
    public const string ReferenceKey = "com.stockanalyzer.analysis.ref";
    public const int SchemaVersion = 1;
    public const int RegimeCount = 3;
    public const int VolatilityBars = 20;
    public const int StandardizationDdof = 0;
    public const int Seed = 42;
    public const int Initializations = 10;
    public const int MaximumIterations = 300;
    public const double Tolerance = 1e-4;
    public const int PermutationRepeats = 10;
    public const double LegendInset = 8;
    public const double LegendSwatchSize = 12;
    public const double LegendGap = 6;
    public const double PercentScale = 100;
    internal const double ValidationTolerance = 1e-8;
}

public sealed record AnalysisPeriod(string Symbol, string Kind, DateTime AnchorStart,
    DateTime AnchorEnd, int SampleCount, string AnchorRevision);
public sealed record ChannelImportance(string Channel, double Value);

public sealed record AnalysisImportance(string Status, string Method, string Unit, string Population,
    ImmutableArray<ChannelImportance> Values, int? SelectedIteration = null,
    string? Metric = null, int? Repeats = null, int? Seed = null, double? BaselineAccuracy = null);

public sealed record AnalysisRegime(string Status, string Algorithm, int K, string Init, int NInit,
    int Seed, int MaxIter, double Tol, string Solver, int VolatilityBars, int StandardizationDdof,
    ImmutableArray<string> FeatureOrder, int FitSampleCount, ImmutableArray<AnalysisPeriod> FitPeriods, string FitDataRevision,
    ImmutableArray<double> Mean = default, ImmutableArray<double> Std = default,
    ImmutableArray<ImmutableArray<double>> Centroids = default,
    ImmutableArray<ImmutableArray<double>> UnscaledCentroids = default,
    ImmutableArray<int> OriginalComponents = default);

/// <summary>Immutable offline analysis, bound to exactly one ONNX and source revision.</summary>
public sealed record ModelAnalysis(int SchemaVersion, string ModelSha256, string DataRevision,
    string FeatureContractHash, string Timeframe, int WindowSize, ImmutableArray<string> OrderedChannels,
    ImmutableArray<string> BaseChannels, ImmutableArray<AnalysisPeriod> Periods,
    AnalysisRegime Regime, AnalysisImportance Importance)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    };

    public static ModelAnalysis Load(string modelPath, IReadOnlyDictionary<string, string> metadata,
        string expectedModelHash, string expectedDataRevision)
    {
        if (metadata.GetValueOrDefault(ModelAnalysisContract.ReferenceKey) != Path.GetFileName(modelPath) + ".analysis.json")
            throw new InvalidDataException("ONNX analysis reference must name its own generation sidecar.");
        var analysis = JsonSerializer.Deserialize<ModelAnalysis>(File.ReadAllText(modelPath + ".analysis.json"), JsonOptions)
            ?? throw new InvalidDataException("Model analysis is empty.");
        analysis.Validate(metadata, expectedModelHash, expectedDataRevision);
        return analysis;
    }

    public void Validate(IReadOnlyDictionary<string, string> metadata, string modelHash, string dataRevision)
    {
        static bool Hash(string? text) => text is { Length: 64 } && text.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
        static void Require([DoesNotReturnIf(false)] bool value) { if (!value) throw new InvalidDataException("Model analysis contract is invalid."); }
        Require(SchemaVersion == ModelAnalysisContract.SchemaVersion && Hash(ModelSha256) && Hash(DataRevision)
            && ModelSha256 == modelHash && DataRevision == dataRevision
            && FeatureContractHash == FixedScaler.ComputeContractHash(metadata)
            && Timeframe == metadata.GetValueOrDefault(PredictionModelMetadata.OutputTimeframeKey)
            && WindowSize > 0 && WindowSize.ToString(CultureInfo.InvariantCulture) == metadata.GetValueOrDefault("window_size")
            && !BaseChannels.IsDefaultOrEmpty && !OrderedChannels.IsDefaultOrEmpty
            && BaseChannels.All(s => !string.IsNullOrWhiteSpace(s)) && BaseChannels.Distinct().Count() == BaseChannels.Length
            && !Periods.IsDefaultOrEmpty && Regime is not null && Importance is not null);
        var lags = JsonSerializer.Deserialize<int[]>(metadata.GetValueOrDefault("lags") ?? "[]")!;
        Require(TrainingResourceOverrides.ValidLags(lags, out _));
        var expected = FixedScaler.ChannelNames(metadata, lags);
        Require(OrderedChannels.SequenceEqual(expected, StringComparer.Ordinal)
            && expected.Length.ToString(CultureInfo.InvariantCulture) == metadata.GetValueOrDefault("channels")
            && OrderedChannels.Take(BaseChannels.Length).SequenceEqual(BaseChannels.Select(s => "lag0:" + s)));
        foreach (var p in Periods) ValidatePeriod(p);
        foreach (var group in Periods.GroupBy(p => p.Symbol))
        {
            var ordered = group.OrderBy(p => p.AnchorStart).ToArray();
            Require(ordered.Length is 2 or 3 && ordered.Select(p => p.Kind).SequenceEqual(
                ordered.Length == 2 ? new[] { "train", "validation" } : new[] { "train", "validation", "oos" }));
            for (int i = 1; i < ordered.Length; i++) Require(ordered[i - 1].AnchorEnd < ordered[i].AnchorStart);
        }
        Require(Regime.Algorithm == "kmeans" && Regime.K == ModelAnalysisContract.RegimeCount
            && Regime.Init == "k-means++" && Regime.NInit == ModelAnalysisContract.Initializations
            && Regime.Seed == ModelAnalysisContract.Seed && Regime.MaxIter == ModelAnalysisContract.MaximumIterations
            && Regime.Tol == ModelAnalysisContract.Tolerance && Regime.Solver == "lloyd"
            && Regime.VolatilityBars == ModelAnalysisContract.VolatilityBars
            && Regime.StandardizationDdof == ModelAnalysisContract.StandardizationDdof
            && !Regime.FeatureOrder.IsDefault && Regime.FeatureOrder.SequenceEqual(new[] { "one_bar_log_return", "realized_volatility_20" })
            && Regime.FitSampleCount >= 0 && !Regime.FitPeriods.IsDefault && Hash(Regime.FitDataRevision));
        int fitCount = 0;
        foreach (var fit in Regime.FitPeriods)
        {
            ValidatePeriod(fit);
            Require(fit.Kind == "train" && Periods.Any(p => p.Symbol == fit.Symbol && p.Kind == "train"
                && p.AnchorStart <= fit.AnchorStart && p.AnchorEnd >= fit.AnchorEnd && p.SampleCount >= fit.SampleCount));
            fitCount = checked(fitCount + fit.SampleCount);
        }
        Require(fitCount == Regime.FitSampleCount && Regime.FitPeriods.Select(p => p.Symbol).Distinct().Count() == Regime.FitPeriods.Length);
        if (Regime.Status == "available")
        {
            Require(Regime.FitSampleCount >= Regime.K && FinitePair(Regime.Mean) && FinitePair(Regime.Std)
                && Regime.Std.All(s => s >= 0) && !Regime.Centroids.IsDefault && Regime.Centroids.Length == Regime.K
                && !Regime.UnscaledCentroids.IsDefault && Regime.UnscaledCentroids.Length == Regime.K
                && !Regime.OriginalComponents.IsDefault && Regime.OriginalComponents.Order().SequenceEqual(Enumerable.Range(0, Regime.K)));
            for (int i = 0; i < Regime.K; i++)
            {
                Require(FinitePair(Regime.Centroids[i]) && FinitePair(Regime.UnscaledCentroids[i]));
                for (int j = 0; j < 2; j++)
                    Require(Math.Abs(Regime.UnscaledCentroids[i][j] - (Regime.Centroids[i][j]
                        * (Regime.Std[j] == 0 ? 1 : Regime.Std[j]) + Regime.Mean[j])) <= ModelAnalysisContract.ValidationTolerance);
                if (i > 0)
                {
                    var prev = Regime.UnscaledCentroids[i - 1]; var curr = Regime.UnscaledCentroids[i];
                    Require(prev[0] < curr[0] || prev[0] == curr[0] && (prev[1] < curr[1]
                        || prev[1] == curr[1] && Regime.OriginalComponents[i - 1] < Regime.OriginalComponents[i]));
                }
            }
        }
        else Require(Regime.Status == "insufficient_training_observations" && Regime.Mean.IsDefaultOrEmpty
            && Regime.Std.IsDefaultOrEmpty && Regime.Centroids.IsDefaultOrEmpty && Regime.UnscaledCentroids.IsDefaultOrEmpty);
        Require(!Importance.Values.IsDefault && Importance.Values.All(v => v is not null && double.IsFinite(v.Value)));
        if (Importance.Status == "available")
            Require(Importance.Values.Select(v => v.Channel).SequenceEqual(BaseChannels));
        else Require(Importance.Values.IsEmpty);
        if (Importance.Method == "split_gain")
            Require(metadata.GetValueOrDefault("target_type") == "classification"
                && Importance.Status is "available" or "zero_total_gain" && Importance.Unit == "relative_gain_percent"
                && Importance.Population == "training" && Importance.SelectedIteration > 0
                && Importance.Values.All(v => v.Value >= 0 && v.Value <= 100)
                && (Importance.Status != "available" || Math.Abs(Importance.Values.Sum(v => v.Value) - 100) <= ModelAnalysisContract.ValidationTolerance));
        else if (Importance.Method == "grouped_permutation")
            Require(metadata.GetValueOrDefault("target_type") == "classification" && Importance.Status == "available"
                && Importance.Unit == "accuracy_percentage_points" && Importance.Population == "inner_validation"
                && Importance.Metric == "classification_accuracy" && Importance.Repeats == ModelAnalysisContract.PermutationRepeats
                && Importance.Seed == ModelAnalysisContract.Seed && Importance.BaselineAccuracy is >= 0 and <= 1
                && Importance.Values.All(v => v.Value is >= -100 and <= 100));
        else Require(metadata.GetValueOrDefault("target_type") == "regression" && Importance.Method == "unavailable"
            && Importance.Status == "regression_metric_not_adopted" && Importance.Unit == "none" && Importance.Population == "none");

        static bool FinitePair(ImmutableArray<double> values) => !values.IsDefault && values.Length == 2 && values.All(double.IsFinite);
        static void ValidatePeriod(AnalysisPeriod p)
        {
            Require(p is not null && !string.IsNullOrWhiteSpace(p.Symbol) && p.Kind is "train" or "validation" or "oos"
                && p.SampleCount > 0 && p.SampleCount <= TrainingResourceOverrides.MaximumSamples
                && p.AnchorStart != default && p.AnchorStart <= p.AnchorEnd && Hash(p.AnchorRevision));
        }
    }
}
