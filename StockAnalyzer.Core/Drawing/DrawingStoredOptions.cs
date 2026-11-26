using System.Text.Json;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Drawing.Values;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Drawing;

/// <summary>
/// Reads the options a saved chart object kept, from the <c>StyleAndParameters</c> of its record. The names are the camel-cased property names of the chart objects
/// (the codec of StockAnalyzer.Avalonia writes every public settable property that way); a test in StockAnalyzer.Avalonia.Tests saves real objects and reads them back
/// through this class, so a renamed property cannot go unnoticed. An option that is missing or of another kind keeps the default a new chart object starts with.
/// </summary>
public static class DrawingStoredOptions
{
    /// <summary>Saved by <c>LongShortPositionObject</c> only (the property with a private setter): the tool the object is, LongPosition or ShortPosition.</summary>
    public const string ToolKey = "type";

    public const string ShowExtensionsKey = "showExtensions";
    public const string PitchforkKindKey = "pitchforkType";
    public const string StdDevMultiplierKey = "stdDevMultiplier";
    public const string ExtendRightKey = "extendRight";
    public const string PriceSourceKey = "priceSource";
    public const string ShowBand1Key = "showBand1";
    public const string Band1MultiplierKey = "band1Multiplier";
    public const string ShowBand2Key = "showBand2";
    public const string Band2MultiplierKey = "band2Multiplier";
    public const string PivotPeriodKey = "period";
    /// <summary>The property <c>ExtendMode</c> of two kinds of object: the pivot lines (<see cref="PivotLineExtendMode"/>) and the trend line (<see cref="LineExtendMode"/>).</summary>
    public const string ExtendModeKey = "extendMode";

    public const string PivotExtendModeKey = ExtendModeKey;
    public const string ChannelModeKey = "channelMode";
    public const string IsVisibleKey = "isVisible";
    public const string IndicatorValueAtEntryKey = "indicatorValueAtEntry";
    public const string ShowEqualDistanceKey = "showEqualDistance";
    public const string ShowEqualPercentageKey = "showEqualPercentage";
    public const string ShowDoubleReturnKey = "showDoubleReturn";
    public const string ShowEProjectionKey = "showEProjection";
    public const string ShowNTProjectionKey = "showNTProjection";

    // DistributionRange keys
    public const string DataModeKey = "dataMode";
    public const string BinningRuleKey = "binningRule";
    public const string FixedBinCountKey = "fixedBinCount";
    public const string ReferenceDistributionKey = "referenceDistribution";
    public const string OverrideStudentTDegreesOfFreedomKey = "overrideStudentTDegreesOfFreedom";
    public const string StudentTDegreesOfFreedomKey = "studentTDegreesOfFreedom";
    public const string GevShapeParameterKey = "gevShapeParameter";
    public const string BandwidthMultiplierKey = "bandwidthMultiplier";

    /// <summary>The tool name a saved object gives itself (<see cref="ToolKey"/>), or null.</summary>
    public static string? ReadTool(IReadOnlyDictionary<string, object?> parameters) => Text(parameters, ToolKey);

    /// <summary>False only when the object was saved hidden (<c>IsVisible</c> false); a missing or other value is visible.</summary>
    public static bool IsVisible(IReadOnlyDictionary<string, object?> parameters) => Bool(parameters, IsVisibleKey) ?? true;

    /// <summary>The extend mode of a saved trend line; <see cref="LineExtendMode.None"/> when missing.</summary>
    public static LineExtendMode ReadLineExtendMode(IReadOnlyDictionary<string, object?> parameters) => Enumeration<LineExtendMode>(parameters, ExtendModeKey) ?? LineExtendMode.None;

    /// <summary>The channel mode of a saved parallel channel; <see cref="ChannelMode.Parallel"/> when missing.</summary>
    public static ChannelMode ReadChannelMode(IReadOnlyDictionary<string, object?> parameters) => Enumeration<ChannelMode>(parameters, ChannelModeKey) ?? ChannelMode.Parallel;

    /// <summary>The options of a saved object.</summary>
    public static DrawingValueOptions Read(IReadOnlyDictionary<string, object?> parameters)
    {
        return new DrawingValueOptions
        {
            ShowExtensions = Bool(parameters, ShowExtensionsKey) ?? false,
            PitchforkKind = Enumeration<PitchforkKind>(parameters, PitchforkKindKey) ?? PitchforkKind.Andrews,
            StdDevMultiplier = Decimal(parameters, StdDevMultiplierKey) ?? RegressionTrendValues.DefaultStdDevMultiplier,
            ExtendRight = Bool(parameters, ExtendRightKey) ?? false,
            PriceSource = Enumeration<PriceType>(parameters, PriceSourceKey) ?? PriceType.Typical,
            ShowBand1 = Bool(parameters, ShowBand1Key) ?? false,
            Band1Multiplier = Decimal(parameters, Band1MultiplierKey) ?? AnchoredVwapValues.DefaultBand1Multiplier,
            ShowBand2 = Bool(parameters, ShowBand2Key) ?? false,
            Band2Multiplier = Decimal(parameters, Band2MultiplierKey) ?? AnchoredVwapValues.DefaultBand2Multiplier,
            PivotPeriod = Enumeration<PivotPeriod>(parameters, PivotPeriodKey) ?? PivotPeriod.Day,
            PivotExtendMode = Enumeration<PivotLineExtendMode>(parameters, PivotExtendModeKey) ?? PivotLineExtendMode.None,
            IndicatorValueAtEntry = Decimal(parameters, IndicatorValueAtEntryKey),
            ProjectionTargets = new ProjectionTargetSelection(
                Bool(parameters, ShowEqualDistanceKey) ?? true,
                Bool(parameters, ShowEqualPercentageKey) ?? true,
                Bool(parameters, ShowDoubleReturnKey) ?? true,
                Bool(parameters, ShowEProjectionKey) ?? true,
                Bool(parameters, ShowNTProjectionKey) ?? true),
            DistributionDataMode = Enumeration<DistributionDataMode>(parameters, DataModeKey) ?? DistributionDataMode.LogReturn,
            DistributionPriceSource = Enumeration<PriceType>(parameters, PriceSourceKey) ?? PriceType.Close,
            DistributionBinningRule = Enumeration<DistributionBinningRule>(parameters, BinningRuleKey) ?? DistributionBinningRule.FreedmanDiaconis,
            DistributionFixedBinCount = Int(parameters, FixedBinCountKey) ?? DistributionRangeParameters.DefaultFixedBinCount,
            DistributionReferenceDistribution = Enumeration<ReferenceDistributionType>(parameters, ReferenceDistributionKey) ?? ReferenceDistributionType.Normal,
            DistributionOverrideStudentTDegreesOfFreedom = Bool(parameters, OverrideStudentTDegreesOfFreedomKey) ?? false,
            DistributionStudentTDegreesOfFreedom = Decimal(parameters, StudentTDegreesOfFreedomKey) ?? DistributionRangeParameters.DefaultStudentTDegreesOfFreedom,
            DistributionGevShapeParameter = Decimal(parameters, GevShapeParameterKey) ?? DistributionRangeParameters.DefaultGevShapeParameter,
            DistributionBandwidthMultiplier = Double(parameters, BandwidthMultiplierKey) ?? DistributionRangeParameters.DefaultBandwidthMultiplier,
        };
    }

    private static int? Int(IReadOnlyDictionary<string, object?> parameters, string key)
        => parameters.TryGetValue(key, out object? value) && value is JsonElement { ValueKind: JsonValueKind.Number } element && element.TryGetInt32(out int number) ? number : null;

    private static double? Double(IReadOnlyDictionary<string, object?> parameters, string key)
        => parameters.TryGetValue(key, out object? value) && value is JsonElement { ValueKind: JsonValueKind.Number } element && element.TryGetDouble(out double number) ? number : null;

    private static bool? Bool(IReadOnlyDictionary<string, object?> parameters, string key)
        => parameters.TryGetValue(key, out object? value) && value is JsonElement { ValueKind: JsonValueKind.True or JsonValueKind.False } element ? element.GetBoolean() : null;

    private static decimal? Decimal(IReadOnlyDictionary<string, object?> parameters, string key)
        => parameters.TryGetValue(key, out object? value) && value is JsonElement { ValueKind: JsonValueKind.Number } element && element.TryGetDecimal(out decimal number) ? number : null;

    private static string? Text(IReadOnlyDictionary<string, object?> parameters, string key)
        => parameters.TryGetValue(key, out object? value) && value is JsonElement { ValueKind: JsonValueKind.String } element ? element.GetString() : null;

    private static T? Enumeration<T>(IReadOnlyDictionary<string, object?> parameters, string key) where T : struct, Enum
        => Enum.TryParse(Text(parameters, key), ignoreCase: false, out T parsed) ? parsed : null;
}
