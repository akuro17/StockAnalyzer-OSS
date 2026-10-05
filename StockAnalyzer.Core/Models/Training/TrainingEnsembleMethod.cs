using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace StockAnalyzer.Core.Models.Training;

/// <summary>
/// How a training run combines independent members (T13). Wire strings (see <c>TrainingConfigJson</c>):
/// <c>none</c> / <c>bagging</c> / <c>stacking</c>, mirroring <c>ensemble.METHODS</c> in the Python trainer.
/// </summary>
public enum TrainingEnsembleMethod
{
    /// <summary>One model per run (the historical behavior).</summary>
    None = 0,

    /// <summary>
    /// Every member fits a time-series block bootstrap of its own train side; the members are mixed with equal weights.
    /// </summary>
    Bagging = 1,

    /// <summary>
    /// Heterogeneous base models; the mixture weights are fitted on out-of-fold probabilities (never on the outer test).
    /// </summary>
    Stacking = 2,
}

/// <summary>
/// Where a stacking run fits its mixture weights (D2). Wire strings: <c>past_out_of_fold</c> / <c>release_meta_segment</c>
/// (mirror of <c>ensemble.WEIGHT_SOURCES</c>). Only meaningful with <see cref="TrainingEnsembleMethod.Stacking"/>.
/// </summary>
public enum StackingWeightSource
{
    /// <summary>The strictly earlier outer folds' out-of-fold rows (shorter training windows than the deployed members).</summary>
    PastOutOfFold = 0,

    /// <summary>
    /// A time-ordered meta segment carved from the release fold's train side (the same rule as the calibration segment). The
    /// deployed members are trained without it and score it, so the weights are fitted on the deployed models themselves.
    /// </summary>
    ReleaseMetaSegment = 1,
}

/// <summary>
/// Self-supervised encoder pretraining before the supervised stage (T13). Wire strings: <c>none</c> / <c>masked_reconstruction</c>
/// (mirror of <c>onnx_meta.PRETRAINING_METHODS</c>). PyTorch only; the ONNX output contract is unchanged.
/// </summary>
public enum TrainingPretraining
{
    None = 0,
    MaskedReconstruction = 1,
}

/// <summary>
/// Where an ensemble run puts its files (mirror of <c>ensemble.member_stem</c> / <c>member_run_id</c> / <c>ENSEMBLE_FILE_SUFFIX</c>
/// and <c>checkpoints.DIRECTORY_NAME</c>, verified by <c>Tests/EnsembleMixtureParity.json</c>). The host rolls a failed run back by
/// these rules, so it deletes only files that match them.
/// </summary>
public static class TrainingEnsembleArtifactLayout
{
    /// <summary>A member's model file is <c>&lt;group stem&gt;_m&lt;index&gt;.onnx</c>.</summary>
    public const string MemberStemMarker = "_m";

    /// <summary>A member's checkpoint directory is <c>&lt;run id&gt;-m&lt;index&gt;</c>.</summary>
    public const string MemberRunIdMarker = "-m";

    /// <summary>The model file extension of a member.</summary>
    public const string ModelExtension = ".onnx";

    /// <summary>True for a plain file name of the form <c>&lt;stem&gt;_m&lt;digits&gt;.onnx</c>.</summary>
    public static bool IsMemberFileName(string? name) =>
        !string.IsNullOrEmpty(name) && name == Path.GetFileName(name)
        && name.EndsWith(ModelExtension, StringComparison.OrdinalIgnoreCase)
        && Regex.IsMatch(name[..^ModelExtension.Length], $"^.+{Regex.Escape(MemberStemMarker)}[0-9]+$", RegexOptions.CultureInvariant);

    /// <summary>True for exactly <c>&lt;runId&gt;-m&lt;digits&gt;</c>: a member checkpoint directory of that run and of no other.</summary>
    public static bool IsMemberCheckpointDirectory(string? name, string runId) =>
        !string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(runId)
        && Regex.IsMatch(name, $"^{Regex.Escape(runId)}{Regex.Escape(MemberRunIdMarker)}[0-9]+$", RegexOptions.CultureInvariant);
}

/// <summary>
/// Shape rules of an ensemble request, mirrored by <c>ensemble.validate_plan</c> in Python. The member upper bound is the
/// existing single owner <see cref="TrainingResourceOverrides.MaximumEnsembleMembers"/> (the effective, possibly lower, limit is enforced again by the trainer).
/// </summary>
public static class TrainingEnsembleContract
{
    /// <summary>Default member count for a bagging request (mirror of <c>ensemble.DEFAULT_BAGGING_MEMBERS</c>).</summary>
    public const int DefaultBaggingMembers = 5;

    /// <summary>An ensemble needs at least two members.</summary>
    public const int MinimumMembers = 2;

    /// <summary>Architecture name of a LightGBM base (mirror of <c>ensemble.LIGHTGBM_ARCHITECTURE</c>).</summary>
    public const string LightGbmArchitecture = "gbdt";

    /// <summary>
    /// Parses a stacking base <c>framework[:architecture]</c>. LightGBM takes no architecture; PyTorch and TensorFlow require one.
    /// </summary>
    public static bool TryParseBase(string? text, out TrainingFramework framework, out string architecture, out string error)
    {
        framework = TrainingFramework.PyTorch;
        architecture = string.Empty;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(text) || text != text.Trim())
        {
            error = "a stacking base must be a non-empty 'framework[:architecture]' string";
            return false;
        }

        var separator = text.IndexOf(':');
        var name = (separator < 0 ? text : text[..separator]).ToLowerInvariant();
        var rest = separator < 0 ? null : text[(separator + 1)..].Trim().ToLowerInvariant();
        switch (name)
        {
            case "lightgbm":
                if (rest is not null)
                {
                    error = "a lightgbm stacking base takes no architecture";
                    return false;
                }

                framework = TrainingFramework.LightGBM;
                architecture = LightGbmArchitecture;
                return true;
            case "pytorch":
            case "tensorflow":
                if (string.IsNullOrEmpty(rest))
                {
                    error = $"a {name} stacking base requires an architecture ('{name}:<architecture>')";
                    return false;
                }

                framework = name == "pytorch" ? TrainingFramework.PyTorch : TrainingFramework.TensorFlow;
                architecture = rest;
                return true;
            default:
                error = $"stacking base framework '{name}' is not pytorch, tensorflow or lightgbm";
                return false;
        }
    }

    /// <summary>Throws <see cref="InvalidOperationException"/> when the ensemble request is malformed.</summary>
    public static void Validate(TrainingEnsembleMethod method, int members, IReadOnlyList<string> bases, TargetType targetType)
    {
        if (!Enum.IsDefined(method) || bases is null)
            throw new InvalidOperationException("TrainingJobConfig: invalid ensemble request.");
        if (method == TrainingEnsembleMethod.None)
        {
            if (bases.Count != 0)
                throw new InvalidOperationException("TrainingJobConfig: StackingBases requires EnsembleMethod 'Stacking'.");
            return;
        }

        if (targetType != TargetType.Classification)
            throw new InvalidOperationException("TrainingJobConfig: ensembles combine classification generations only.");
        if (method == TrainingEnsembleMethod.Bagging)
        {
            if (bases.Count != 0)
                throw new InvalidOperationException("TrainingJobConfig: StackingBases requires EnsembleMethod 'Stacking'.");
            if (members is < MinimumMembers or > TrainingResourceOverrides.MaximumEnsembleMembers)
                throw new InvalidOperationException(
                    $"TrainingJobConfig: EnsembleMembers must be from {MinimumMembers} to {TrainingResourceOverrides.MaximumEnsembleMembers}.");
            return;
        }

        if (bases.Count is < MinimumMembers or > TrainingResourceOverrides.MaximumEnsembleMembers)
            throw new InvalidOperationException(
                $"TrainingJobConfig: StackingBases needs {MinimumMembers} to {TrainingResourceOverrides.MaximumEnsembleMembers} entries.");
        var parsed = new List<(TrainingFramework, string)>();
        foreach (var text in bases)
        {
            if (!TryParseBase(text, out var framework, out var architecture, out var error))
                throw new InvalidOperationException($"TrainingJobConfig: {error}.");
            parsed.Add((framework, architecture));
        }

        if (parsed.Distinct().Count() != parsed.Count)
            throw new InvalidOperationException("TrainingJobConfig: StackingBases entries must be distinct.");
    }
}
