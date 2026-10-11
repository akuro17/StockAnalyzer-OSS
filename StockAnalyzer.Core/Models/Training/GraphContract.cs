using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace StockAnalyzer.Core.Models.Training;

/// <summary>
/// Contract of the multi-symbol graph model (T25: one correlation graph per date, one prediction per node). Python mirrors every value
/// (<c>train_gnn.py</c>); <c>Tests/GraphParity.json</c>, written by the Python generator, keeps both sides identical.
/// </summary>
/// <remarks>
/// The graph model is NOT a model of the single-symbol prediction contract (rank-4 input, rank-3 output, its own metadata namespace):
/// <c>PredictionService</c> and the model registry refuse it, and <see cref="Services.GraphPredictionService"/> consumes it on its own.
/// </remarks>
public static class GraphContract
{
    public const int ContractVersion = 1;
    public const string MetadataPrefix = "com.stockanalyzer.graph.";
    public const string ContractVersionKey = MetadataPrefix + "contract_version";
    public const string NodeOrderKey = MetadataPrefix + "node_order";
    public const string WindowKey = MetadataPrefix + "window";
    public const string HorizonKey = MetadataPrefix + "horizon";
    public const string CorrWindowKey = MetadataPrefix + "corr_window";
    public const string EdgeThresholdKey = MetadataPrefix + "edge_threshold";
    public const string MinNodesKey = MetadataPrefix + "min_nodes";
    public const string FeatureModeKey = MetadataPrefix + "feature_mode";
    public const string ChannelsKey = MetadataPrefix + "channels";
    public const string ClassOrderKey = MetadataPrefix + "class_order";
    public const string TimeframeKey = MetadataPrefix + "timeframe";

    /// <summary>Every metadata key a graph model must carry (and the only ones allowed in the graph namespace).</summary>
    public static readonly ImmutableArray<string> ContractKeys = ImmutableArray.Create(
        ContractVersionKey, NodeOrderKey, WindowKey, HorizonKey, CorrWindowKey, EdgeThresholdKey, MinNodesKey,
        FeatureModeKey, ChannelsKey, ClassOrderKey, TimeframeKey);

    public const string InputNodeFeatures = "node_features";
    public const string InputAdjacency = "adjacency";
    public const string InputNodeMask = "node_mask";
    public const string OutputName = "output";
    public const int NodeFeaturesRank = 4;
    public const int AdjacencyRank = 3;
    public const int NodeMaskRank = 2;
    public const int OutputRank = 3;

    public const int ClassCount = 3;
    public const string ClassOrder = "Up,Down,Neutral";
    public const string Timeframe = "daily";

    /// <summary>A Pearson correlation needs two observations (the period rule of <c>IndicatorCalculationHelper.CalculateRollingPearsonCorrelation</c>).</summary>
    public const int MinCorrWindow = 2;

    /// <summary>Published run folder: <c>&lt;DataRoot&gt;/TrainingArtifacts/Graph/&lt;runId&gt;/</c> with these two files.</summary>
    public const string ArtifactKind = "Graph";
    public const string ModelFileName = "graph.onnx";
    public const string DocumentFileName = "graph.json";
    public const int DocumentSchemaVersion = 1;

    /// <summary>Tensor width of a per-window feature mode; <see langword="null"/> for a mode the graph model does not support (composed features).</summary>
    public static int? ChannelsOf(PredictionFeatureMode mode) => mode switch
    {
        PredictionFeatureMode.OhlcvMinMax => 5,
        PredictionFeatureMode.LogReturn => 1,
        PredictionFeatureMode.ZScoreStandardized => 5,
        PredictionFeatureMode.ZScoreOhlcvJoint => 5,
        PredictionFeatureMode.LogReturnOhlc => 4,
        _ => null,
    };
}

/// <summary>Safety bounds of the graph model (code contract, never configuration). Python mirrors them in <c>train_gnn.Limits</c>.</summary>
public static class GraphLimits
{
    /// <summary>Nodes (symbols) of one graph.</summary>
    public const int MaxNodes = 64;

    /// <summary>Bars of the feature window and of the correlation window (each is bounded by this value).</summary>
    public const int MaxWindow = 1024;

    /// <summary>Largest float32 tensor (bytes) one inference may allocate.</summary>
    public const long MaxTensorBytes = 512L * 1024 * 1024;
}

/// <summary>Stable reasons a graph prediction is unavailable. A result with a reason never carries a probability: it is not a zero prediction.</summary>
public static class GraphUnavailableReason
{
    public const string NotLoaded = "not_loaded";
    public const string NoData = "no_data";
    public const string UnknownSymbol = "unknown_symbol";
    public const string InvalidHistory = "invalid_history";
    public const string AsOfNotInData = "as_of_not_in_data";
    public const string InsufficientNodes = "insufficient_nodes";
    public const string InvalidOutput = "invalid_output";
    public const string InferenceFailed = "inference_failed";
}

public enum GraphPredictionStatus
{
    Available = 0,
    Unavailable = 1,
}

/// <summary>One node of a graph prediction. A masked node (no feature window at the as-of date) has no probabilities.</summary>
public sealed record GraphNodePrediction(string Symbol, bool IsMasked, ImmutableArray<float> Probabilities);

/// <summary>Immutable result of <see cref="Services.IGraphPredictionService.PredictAsync"/>. <see cref="Detail"/> is a diagnostic, not display text.</summary>
public sealed record GraphPredictionResult(
    GraphPredictionStatus Status,
    string? Reason,
    string? Detail,
    DateTime? AsOf,
    ImmutableArray<string> ClassOrder,
    ImmutableArray<GraphNodePrediction> Nodes)
{
    public bool IsAvailable => Status == GraphPredictionStatus.Available;

    public static GraphPredictionResult Unavailable(string reason, string? detail = null, DateTime? asOf = null) =>
        new(GraphPredictionStatus.Unavailable, reason, detail, asOf, ImmutableArray<string>.Empty, ImmutableArray<GraphNodePrediction>.Empty);
}

/// <summary>The verified contract of a loaded graph model.</summary>
public sealed record GraphModelInfo(
    string RunDirectory,
    string ModelSha256,
    ImmutableArray<string> NodeOrder,
    int Window,
    int Horizon,
    int CorrWindow,
    double EdgeThreshold,
    int MinNodes,
    PredictionFeatureMode FeatureMode,
    int Channels,
    ImmutableArray<string> ClassOrder);
