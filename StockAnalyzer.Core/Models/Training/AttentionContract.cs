using System;
using System.Collections.Immutable;

namespace StockAnalyzer.Core.Models.Training;

/// <summary>
/// Contract of the attention sidecar <c>&lt;model&gt;.onnx.attention.onnx</c> and of its chart overlay (T24). Python mirrors the sidecar values
/// (<c>attention_export.py</c>); <c>Tests/AttentionParity.json</c>, written by the Python generator, keeps both sides identical.
/// </summary>
/// <remarks>
/// The sidecar is an optional derived file beside the production model: it is not a required registry sidecar, not part of the generation identity
/// and has no key in the production metadata. The registry only carries it into the generation folder (like the explanation).
/// </remarks>
public static class AttentionContract
{
    /// <summary>The training architecture that produces the sidecar (mirrors <c>attention_export.ARCH</c>; parity-tested against the Python value).</summary>
    public const string Architecture = "attn_lstm";

    /// <summary>True when <paramref name="architecture"/> names the attention architecture (the trainer trims and lower-cases the name).</summary>
    public static bool IsAttentionArchitecture(string? architecture) =>
        string.Equals(architecture?.Trim(), Architecture, StringComparison.OrdinalIgnoreCase);

    /// <summary>Appended to the production model path: <c>&lt;model&gt;.onnx</c> + this = the sidecar.</summary>
    public const string SidecarSuffix = ".attention.onnx";

    public const string MetadataPrefix = "com.stockanalyzer.attention.";
    public const string SchemaVersionKey = MetadataPrefix + "schema_version";
    public const string ParentSha256Key = MetadataPrefix + "parent_sha256";
    public const string WindowSizeKey = MetadataPrefix + "window_size";
    public const string HeadsKey = MetadataPrefix + "heads";
    public const string FeatureContractHashKey = MetadataPrefix + "feature_contract_hash";
    public const string LayoutKey = MetadataPrefix + "layout";
    public const string MaskKey = MetadataPrefix + "mask";
    public const string UnitKey = MetadataPrefix + "unit";

    /// <summary>Every metadata key a sidecar must carry (and the only ones allowed in the attention namespace).</summary>
    public static readonly ImmutableArray<string> BindingKeys = ImmutableArray.Create(
        SchemaVersionKey, ParentSha256Key, WindowSizeKey, HeadsKey, FeatureContractHashKey, LayoutKey, MaskKey, UnitKey);

    public const int SchemaVersion = 1;
    public const string Layout = "batch_head_query_key";
    public const string Mask = "causal_lower_triangular";
    public const string Unit = "attention_weight";

    public const string InputName = "input";
    public const string OutputName = "attention";
    public const int InputRank = 3;
    public const int OutputRank = 4;

    /// <summary>Safety bound on the head count (mirrors <c>attention_export.MAX_HEADS</c>).</summary>
    public const int MaxHeads = 16;

    /// <summary>Safety bound on the window (bars) and the length of every preallocated band array of the renderer.</summary>
    public const int MaxWindow = GraphLimits.MaxWindow;

    /// <summary>A softmax row sums to 1 within this (mirrors <c>attention_export.ROW_SUM_ATOL</c>).</summary>
    public const float RowSumTolerance = 1e-4f;

    /// <summary>The attention map needs a query row and at least one neighbour bar to draw a band.</summary>
    public const int MinWindow = 2;

    // Legend geometry: the same semantic tokens as the training-period legend (ModelAnalysisContract), one row below it.
    public const double LegendInset = ModelAnalysisContract.LegendInset;
    public const double LegendSwatchSize = ModelAnalysisContract.LegendSwatchSize;
    public const double LegendGap = ModelAnalysisContract.LegendGap;
    public const double LegendRowOffset = ModelAnalysisContract.LegendSwatchSize + ModelAnalysisContract.LegendGap;

    /// <summary>Why a chart shows no attention. Stable codes; the view model maps them to localization keys.</summary>
    public static class UnsupportedReason
    {
        public const string NoSidecar = "no_sidecar";
        public const string Timeframe = "timeframe";
        public const string NoModel = "no_model";
        public const string ChartType = "chart_type";

        /// <summary>An ensemble none of whose weighted members carries usable attention data.</summary>
        public const string NoAttentionMember = "no_attention_member";
    }
}

/// <summary>
/// Identity of one attention request: a result is only ever shown for the key it was requested for. For an ensemble <see cref="ModelId"/> is the
/// <see cref="StockAnalyzer.Core.Services.EnsembleSpec.Fingerprint"/> of the saved ensemble (members and weights), never a single generation id.
/// </summary>
public sealed record AttentionRequestKey(string ModelId, string Symbol, string Timeframe, DateTime Anchor, long DataRevision);

/// <summary>
/// The attention of the LAST bar's prediction over the model window, heads averaged. <see cref="Relative"/> = <c>w / max(w)</c> (1.0 = the most attended
/// bar of this window); <see cref="Weights"/> are the raw attention weights (dimensionless, not a price and not a probability).
/// For an ensemble the arrays span the longest member window, <see cref="Heads"/> is the largest head count among the members that contributed and
/// <see cref="Weights"/> is the weight-mixed attention (it still sums to 1); <see cref="MemberCount"/> &gt; 0 marks an ensemble snapshot and
/// <see cref="CapableMembers"/> is how many of its members contributed. <see cref="CoverageWeight"/> is the share of the ensemble weight the
/// contributing members carry (<c>sum of contributing weights / sum of all member weights</c>, in [0, 1]); <see cref="WindowsDiffer"/> is true when the
/// contributing members have more than one window length. Both stay at their defaults for a single-model snapshot.
/// </summary>
public sealed record AttentionSnapshot(
    AttentionRequestKey Key,
    int Heads,
    ImmutableArray<DateTime> Timestamps,
    ImmutableArray<float> Weights,
    ImmutableArray<float> Relative,
    int CapableMembers = 0,
    int MemberCount = 0,
    double CoverageWeight = 0d,
    bool WindowsDiffer = false)
{
    public bool IsEnsemble => MemberCount > 0;
}

public enum AttentionStatus
{
    Ready = 0,
    Unsupported = 1,
    Failed = 2,
}

/// <summary>Outcome of an attention computation. <see cref="Snapshot"/> exists only when <see cref="Status"/> is <see cref="AttentionStatus.Ready"/>.</summary>
public sealed record AttentionResult(AttentionStatus Status, string? Reason, string? Detail, AttentionSnapshot? Snapshot)
{
    public static AttentionResult Ready(AttentionSnapshot snapshot) => new(AttentionStatus.Ready, null, null, snapshot);

    public static AttentionResult Unsupported(string reason, string? detail = null) => new(AttentionStatus.Unsupported, reason, detail, null);

    public static AttentionResult Failed(string detail) => new(AttentionStatus.Failed, null, detail, null);
}
