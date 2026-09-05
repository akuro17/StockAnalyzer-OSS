using System;
using System.Collections.Generic;

namespace StockAnalyzer.Core.Models.Training;

/// <summary>
/// Which raw per-bar price series a <see cref="FeatureChannelKind.Price"/> channel emits. There is
/// no fixed OHLCV base in <see cref="PredictionFeatureMode.ComposedFeatures"/>: Open/High/Low/Close
/// are picked individually and Volume is selected as an indicator, so the feature set is fully
/// composable and the channel count is the sum of the selected channels' output widths.
/// </summary>
/// <summary>
/// Legacy price field enum. Retained for backward compatibility.
/// Prefer <see cref="StockAnalyzer.Core.Models.PriceType"/>.
/// </summary>
[Obsolete("Use StockAnalyzer.Core.Models.PriceType instead.")]
public enum PriceField : byte
{
    /// <summary>Bar open price.</summary>
    Open = 0,

    /// <summary>Bar high price.</summary>
    High = 1,

    /// <summary>Bar low price.</summary>
    Low = 2,

    /// <summary>Bar close price.</summary>
    Close = 3,
}

/// <summary>
/// Per-channel normalization applied while composing a
/// <see cref="PredictionFeatureMode.ComposedFeatures"/> input tensor. Chosen independently for each
/// channel so a price series and an oscillator never share one scale (Domain Invariant: a value's
/// semantic unit must stay stable). The concrete numeric definitions are realized by the feature
/// exporter / <c>ComposeFeatures</c> and are out of scope for this DTO.
/// </summary>
public enum ChannelNormalization : byte
{
    /// <summary>Value written through unchanged (already unit-stable, e.g. a bounded oscillator).</summary>
    None = 0,

    /// <summary>Min-max scaled to <c>[0, 1]</c> over the look-back window, this channel only.</summary>
    WindowMinMax = 1,

    /// <summary>Population Z-Score over the look-back window, this channel only.</summary>
    WindowZScore = 2,
}

/// <summary>Discriminates the two <see cref="FeatureChannel"/> shapes for JSON round-tripping.</summary>
public enum FeatureChannelKind : byte
{
    /// <summary>A raw price series; <see cref="FeatureChannel.Price"/> is set.</summary>
    Price = 0,

    /// <summary>A registered indicator; <see cref="FeatureChannel.Indicator"/> is set.</summary>
    Indicator = 1,
}

/// <summary>
/// One ordered input channel of a <see cref="PredictionFeatureMode.ComposedFeatures"/> model.
/// A channel is either a raw price series (<see cref="Kind"/> = <see cref="FeatureChannelKind.Price"/>,
/// <see cref="Price"/> set) or a registered indicator (<see cref="FeatureChannelKind.Indicator"/>,
/// <see cref="Indicator"/> set). <see cref="Params"/> carries non-default indicator settings as
/// string key/value pairs, mirroring <see cref="TrainingJobConfig.Hyperparameters"/> so the wire
/// document stays serializer-agnostic; the feature exporter parses and range-checks each value.
/// </summary>
public sealed record FeatureChannel
{
    /// <summary>Selects which of <see cref="Price"/> / <see cref="Indicator"/> is meaningful.</summary>
    public required FeatureChannelKind Kind { get; init; }

    /// <summary>Price series for a <see cref="FeatureChannelKind.Price"/> channel; otherwise <see langword="null"/>.</summary>
    public PriceType? Price { get; init; }

    /// <summary>Indicator type for a <see cref="FeatureChannelKind.Indicator"/> channel; otherwise <see langword="null"/>.</summary>
    public IndicatorType? Indicator { get; init; }

    /// <summary>
    /// Non-default indicator settings as string key/value pairs (empty = registry defaults). Ignored
    /// for a <see cref="FeatureChannelKind.Price"/> channel.
    /// </summary>
    public IReadOnlyDictionary<string, string> Params { get; init; } = new Dictionary<string, string>();

    /// <summary>Normalization applied to this channel when composing the input tensor.</summary>
    public ChannelNormalization Normalization { get; init; } = ChannelNormalization.None;

    /// <summary>
    /// <see langword="true"/> when the discriminator and its payload are internally consistent and
    /// every enum value is defined. Does not consult the indicator registry (a DTO concern-boundary);
    /// the exporter performs the registry-backed existence check.
    /// </summary>
    public bool IsValid(out string? error)
    {
        if (!Enum.IsDefined(typeof(ChannelNormalization), Normalization))
        {
            error = $"Normalization '{Normalization}' is not a defined ChannelNormalization value.";
            return false;
        }

        switch (Kind)
        {
            case FeatureChannelKind.Price:
                if (Price is null)
                {
                    error = "A Price channel requires a Price value.";
                    return false;
                }

                if (Indicator is not null)
                {
                    error = "A Price channel must not also carry an Indicator value.";
                    return false;
                }

                if (!Enum.IsDefined(typeof(PriceType), Price.Value))
                {
                    error = $"Price '{Price.Value}' is not a defined PriceType value.";
                    return false;
                }

                error = null;
                return true;

            case FeatureChannelKind.Indicator:
                if (Indicator is null)
                {
                    error = "An Indicator channel requires an Indicator value.";
                    return false;
                }

                if (Price is not null)
                {
                    error = "An Indicator channel must not also carry a Price value.";
                    return false;
                }

                if (!Enum.IsDefined(typeof(IndicatorType), Indicator.Value))
                {
                    error = $"Indicator '{Indicator.Value}' is not a defined IndicatorType value.";
                    return false;
                }

                if (Params is null)
                {
                    error = "An Indicator channel's Params map must not be null (use an empty map).";
                    return false;
                }

                error = null;
                return true;

            default:
                error = $"Kind '{Kind}' is not a defined FeatureChannelKind value.";
                return false;
        }
    }
}

/// <summary>
/// The ordered set of input channels for a <see cref="PredictionFeatureMode.ComposedFeatures"/>
/// model. The wire form of this record is echoed verbatim into the ONNX
/// <c>metadata_props["feature_spec"]</c> and cross-checked at inference time by
/// <see cref="StockAnalyzer.Core.Models.PredictionModelMetadata.Validate"/>, because tensor-shape
/// validation alone cannot tell one channel composition from another of equal width.
/// </summary>
public sealed record FeatureSpec
{
    /// <summary>The <see cref="SchemaVersion"/> written by the current build.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// Schema identifier captured at save time (defaults to <see cref="CurrentSchemaVersion"/> for a
    /// freshly composed spec). Recorded so a future compatibility check has something to compare
    /// against if a stored <see cref="FeatureChannel.Params"/> diff's meaning ever depends on the
    /// registry defaults of the build that saved it; no such check is performed today - a spec loads
    /// identically regardless of this value.
    /// </summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Input channels in tensor-channel order. Must be non-empty.</summary>
    public required IReadOnlyList<FeatureChannel> Channels { get; init; }

    /// <summary><see langword="true"/> when <see cref="Channels"/> is non-empty and every channel is valid.</summary>
    public bool IsValid(out string? error)
    {
        if (Channels is null || Channels.Count == 0)
        {
            error = "FeatureSpec requires at least one channel.";
            return false;
        }

        for (int i = 0; i < Channels.Count; i++)
        {
            if (Channels[i] is null)
            {
                error = $"FeatureSpec channel [{i}] is null.";
                return false;
            }

            if (!Channels[i].IsValid(out var channelError))
            {
                error = $"FeatureSpec channel [{i}]: {channelError}";
                return false;
            }
        }

        error = null;
        return true;
    }
}
