using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Drawing.Values;

/// <summary>
/// Data-window and MCP calculated values for <c>DistributionRange</c>.
/// All formatting uses <see cref="DrawingValueFormat.Culture"/> (en-US).
/// </summary>
public static class DistributionRangeValues
{
    public const int SampleCountIndex = 0;
    public const int MeanIndex = 1;
    public const int StdDevIndex = 2;
    public const int MedianIndex = 3;
    public const int IqrIndex = 4;
    public const int SkewnessIndex = 5;
    public const int ExcessKurtosisIndex = 6;
    public const int ReferenceDistributionIndex = 7;
    public const int BinCountIndex = 8;
    public const int ValueCount = 9;

    private const string NoValueText = "-";

    public static IReadOnlyList<DrawingCalculatedValue> Calculate(
        DrawingAnchor point1,
        DrawingAnchor point2,
        IReadOnlyList<CoreCandleData>? candles,
        IndicatorColor color,
        DrawingValueOptions options)
    {
        if (candles == null)
        {
            return Array.Empty<DrawingCalculatedValue>();
        }

        var parameters = BuildParameters(options);
        DistributionRangeResult result = DistributionRangeCalculator.Calculate(candles, point1.Time, point2.Time, parameters);
        return FromResult(result, parameters, color);
    }

    /// <summary>
    /// Converts a precomputed <see cref="DistributionRangeResult"/> into the fixed 9-item DataWindow/MCP array.
    /// Used directly by GUI objects to guarantee SSoT numerical and formatting parity.
    /// </summary>
    public static IReadOnlyList<DrawingCalculatedValue> FromResult(
        DistributionRangeResult result,
        DistributionRangeParameters parameters,
        IndicatorColor color)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(parameters);

        var culture = DrawingValueFormat.Culture;
        string scaleFormat = parameters.DataMode == DistributionDataMode.Price ? "F3" : "F6";

        var values = new DrawingCalculatedValue[ValueCount];
        values[SampleCountIndex] = new DrawingCalculatedValue(
            "SampleCount",
            "Samples (N)",
            result.SampleCount,
            result.SampleCount.ToString(culture),
            color);

        values[MeanIndex] = FormatDecimal("Mean", "Mean", result.Mean, scaleFormat, color);
        values[StdDevIndex] = FormatDecimal("StdDev", "StdDev", result.StdDev, scaleFormat, color);
        values[MedianIndex] = FormatDecimal("Median", "Median", result.Median, scaleFormat, color);
        values[IqrIndex] = FormatDecimal("IQR", "IQR", result.Iqr, scaleFormat, color);
        values[SkewnessIndex] = FormatDecimal("Skewness", "Skewness (G1)", result.Skewness, "F3", color);
        values[ExcessKurtosisIndex] = FormatDecimal("ExcessKurtosis", "ExcessKurtosis (G2)", result.ExcessKurtosis, "F3", color);

        string refText = FormatReferenceSummary(result, parameters);
        values[ReferenceDistributionIndex] = new DrawingCalculatedValue(
            "ReferenceDistribution",
            "Reference",
            null,
            refText,
            color);

        int binCount = result.Bins.Count;
        values[BinCountIndex] = new DrawingCalculatedValue(
            "BinCount",
            "Bins",
            binCount > 0 ? binCount : null,
            binCount > 0 ? binCount.ToString(culture) : NoValueText,
            color);

        return values;
    }

    public static DistributionRangeParameters BuildParameters(DrawingValueOptions options) => new()
    {
        DataMode = options.DistributionDataMode,
        PriceSource = options.DistributionPriceSource,
        BinningRule = options.DistributionBinningRule,
        FixedBinCount = options.DistributionFixedBinCount,
        ReferenceDistribution = options.DistributionReferenceDistribution,
        OverrideStudentTDegreesOfFreedom = options.DistributionOverrideStudentTDegreesOfFreedom,
        StudentTDegreesOfFreedom = options.DistributionStudentTDegreesOfFreedom,
        GevShapeParameter = options.DistributionGevShapeParameter,
        BandwidthMultiplier = options.DistributionBandwidthMultiplier,
        IncludeKde = true
    };

    private static DrawingCalculatedValue FormatDecimal(
        string key,
        string label,
        decimal? value,
        string format,
        IndicatorColor color) => new(
            key,
            label,
            value,
            value.HasValue ? value.Value.ToString(format, DrawingValueFormat.Culture) : NoValueText,
            color);

    private static string FormatReferenceSummary(DistributionRangeResult result, DistributionRangeParameters parameters)
    {
        if (result.ReferenceStatus == ReferenceFitStatus.None || parameters.ReferenceDistribution == ReferenceDistributionType.None)
        {
            return "None";
        }

        if (!string.IsNullOrWhiteSpace(result.ReferenceParameterSummary))
        {
            return result.ReferenceParameterSummary;
        }

        return $"{parameters.ReferenceDistribution} ({result.ReferenceStatus})";
    }
}
