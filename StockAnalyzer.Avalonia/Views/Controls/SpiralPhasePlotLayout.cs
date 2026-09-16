using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Analysis;

namespace StockAnalyzer.Avalonia.Views.Controls;

internal readonly record struct SpiralPhasePlotLayout(
    double MaximumRadius,
    double DisplayBaseRadius,
    double RadialSpan,
    double[] GridRadii,
    string[] GridLabels,
    string[] AngleLabels,
    string CenterLabel,
    int StartSampleIndex,
    int EndSampleIndex);

internal static class SpiralPhasePlotLayoutBuilder
{
    internal const int MaximumGridCircleCount = 8;
    internal const int RadialLineCount = 8;
    internal const double DisplayOriginPaddingFraction = 0.10d;

    internal static SpiralPhasePlotLayout Build(
        SpiralAnalysisResult result,
        bool includeModelRange,
        decimal? manualPriceStep = null,
        string priceUnit = "",
        string barUnit = "",
        bool useVisibleRangeOrigin = false,
        string displayBaseLabel = "")
    {
        string[] angleLabels = BuildAngleLabels(result.Parameters.BarsPerTurn, barUnit, result.Parameters.IsClockwise);
        double maximum = 0d;
        double minimumDrawable = double.PositiveInfinity;
        int start = -1;
        int end = -1;
        for (int i = 0; i < result.Samples.Count; i++)
        {
            SpiralAnalysisSample sample = result.Samples[i];
            if (sample.Status == SpiralAnalysisSampleStatus.Valid && double.IsFinite(sample.Radius) && sample.Radius > 0d)
            {
                maximum = Math.Max(maximum, sample.Radius);
                minimumDrawable = Math.Min(minimumDrawable, sample.Radius);
                if (start < 0) start = i;
                end = i;
            }
            if (sample.ModelRadius is { } model && double.IsFinite(model) && model > 0d && model <= (double)decimal.MaxValue)
            {
                minimumDrawable = Math.Min(minimumDrawable, model);
                if (includeModelRange) maximum = Math.Max(maximum, model);
            }
        }
        if (maximum <= 0d)
            return new SpiralPhasePlotLayout(0d, 0d, 0d, Array.Empty<double>(), Array.Empty<string>(), angleLabels, FormatPriceLabel(0d, priceUnit), start, end);

        double displayBaseRadius = useVisibleRangeOrigin && double.IsFinite(minimumDrawable)
            ? minimumDrawable * (1d - DisplayOriginPaddingFraction)
            : 0d;
        double radialSpan = maximum - displayBaseRadius;
        if (!double.IsFinite(displayBaseRadius) || displayBaseRadius < 0d || !double.IsFinite(radialSpan) || radialSpan <= 0d)
        {
            displayBaseRadius = 0d;
            radialSpan = maximum;
        }

        double step = manualPriceStep is { } configuredStep && configuredStep > 0m ? (double)configuredStep : CalculateNiceStep(radialSpan / 5d);
        var radii = new List<double>(MaximumGridCircleCount);
        var labels = new List<string>(MaximumGridCircleCount);
        double firstGridRadius = displayBaseRadius > 0d ? (Math.Floor(displayBaseRadius / step) + 1d) * step : step;
        for (double value = firstGridRadius; value <= maximum && radii.Count < MaximumGridCircleCount; value += step) radii.Add(value);
        for (int index = 0; index < radii.Count; index++) labels.Add(FormatPriceLabel(radii[index], priceUnit));
        string centerLabel = displayBaseRadius > 0d && !string.IsNullOrWhiteSpace(displayBaseLabel)
            ? string.Concat(displayBaseLabel, ": ", FormatPriceLabel(displayBaseRadius, priceUnit))
            : FormatPriceLabel(displayBaseRadius, priceUnit);
        return new SpiralPhasePlotLayout(maximum, displayBaseRadius, radialSpan, radii.ToArray(), labels.ToArray(), angleLabels, centerLabel, start, end);
    }

    internal static string FormatPriceLabel(double value, string priceUnit)
    {
        string number = value.ToString("G6", System.Globalization.CultureInfo.CurrentCulture);
        return string.IsNullOrWhiteSpace(priceUnit) ? number : string.Concat(number, " ", priceUnit);
    }

    internal static string[] BuildAngleLabels(uint barsPerTurn, string barUnit, bool isClockwise = false)
    {
        var labels = new string[RadialLineCount];
        for (int index = 0; index < labels.Length; index++)
        {
            int degrees = 360 * index / RadialLineCount;
            int phaseIndex = isClockwise && index != 0 ? RadialLineCount - index : index;
            decimal barPhase = (decimal)barsPerTurn * phaseIndex / RadialLineCount;
            string phase = barPhase.ToString("0.###", System.Globalization.CultureInfo.CurrentCulture);
            labels[index] = string.IsNullOrWhiteSpace(barUnit)
                ? string.Concat(degrees.ToString(System.Globalization.CultureInfo.CurrentCulture), "° / ", phase)
                : string.Concat(degrees.ToString(System.Globalization.CultureInfo.CurrentCulture), "° / ", phase, " ", barUnit);
        }
        return labels;
    }

    internal static double CalculateNiceStep(double value)
    {
        if (!double.IsFinite(value) || value <= 0d) return 0d;
        double power = Math.Pow(10d, Math.Floor(Math.Log10(value)));
        double normalized = value / power;
        return (normalized <= 1d ? 1d : normalized <= 2d ? 2d : normalized <= 5d ? 5d : 10d) * power;
    }
}
