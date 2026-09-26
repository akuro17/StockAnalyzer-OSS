using System;
using StockAnalyzer.Core.Models.Training;

namespace StockAnalyzer.Core.Models.Templates;

/// <summary>
/// Represents a reusable template containing a composed-features channel set (Price / Selected /
/// Indicator channels, each with its own normalization) for a
/// <see cref="PredictionFeatureMode.ComposedFeatures"/> training run. Mirrors
/// <see cref="FilterTemplate"/>'s shape: a single wrapped DTO, no extra metadata beyond
/// <see cref="TemplateBase"/>. Deliberately holds one <see cref="FeatureSpec"/> rather than splitting
/// price/selected/indicator into separate lists, matching the wizard's own composition model where
/// all three are just rows of one ordered channel list.
/// </summary>
public class FeatureSpecTemplate : TemplateBase
{
    public override TemplateType TemplateType => TemplateType.Feature;

    /// <summary>The saved channel composition.</summary>
    public FeatureSpec Spec { get; set; } = new() { Channels = Array.Empty<FeatureChannel>() };
}
