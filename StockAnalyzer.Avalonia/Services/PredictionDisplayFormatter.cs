using System;
using System.Globalization;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Avalonia.Services;

/// <summary>Display stage for a validated semantic ONNX result.</summary>
public static class PredictionDisplayFormatter
{
    public const int DefaultReturnDecimalPlaces = 2;
    public const MidpointRounding DefaultRounding = MidpointRounding.ToEven;
    public const string ReturnLabelKey = "Chart_PredictedForwardReturn";
    public const string ClassLabelKey = "Chart_PredictedClass";
    public const string ClassLabelWithProbabilityKey = "Chart_PredictedClassWithProbability";
    public const string NoDecisionKey = "Chart_PredictedNoDecision";
    public const int DefaultProbabilityDecimalPlaces = 1;

    /// <summary>
    /// Formats a current result. The model framework is deliberately absent from this API.
    /// A null return means the result is unavailable or belongs to another chart timeframe.
    /// </summary>
    /// <param name="decisionThreshold">
    /// The probability a class needs to be named (<c>PredictionConfidenceThreshold</c>). When given, an <see cref="PredictionResult.UnknownLabel"/> result that
    /// carries a probability says it is under that threshold instead of showing the bare label; when null the text is the same as before.
    /// </param>
    public static string? Format(PredictionResult result, TimeframeType chartTimeframe,
        CultureInfo culture, Func<string, string?> localize, float? decisionThreshold = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(culture);
        ArgumentNullException.ThrowIfNull(localize);
        var contract = result.OutputContract;
        if (result.IsFallback || contract is null || contract.Timeframe != chartTimeframe)
            return null;

        if (contract.Semantic == PredictionOutputSemantic.LogReturn)
        {
            if (!result.IsRegression || contract.Unit != PredictionOutputUnit.Dimensionless
                || !double.IsFinite(result.PredictedLogReturn)) return null;
            // Display-only inverse of the model's raw log-return target. Never use r*100.
            double simpleReturnPercent = 100.0 * (Math.Exp(result.PredictedLogReturn) - 1.0);
            if (!double.IsFinite(simpleReturnPercent)) return null;
            double rounded = Math.Round(simpleReturnPercent, DefaultReturnDecimalPlaces, DefaultRounding);
            var template = localize(ReturnLabelKey);
            if (string.IsNullOrWhiteSpace(template)) return null;
            return string.Format(culture, template, contract.HorizonBars,
                rounded.ToString($"F{DefaultReturnDecimalPlaces}", culture));
        }

        if (contract.Semantic == PredictionOutputSemantic.ClassProbabilities
            && !result.IsRegression && !string.IsNullOrWhiteSpace(result.Label))
        {
            if (TryFormatProbabilityPercent(result, culture) is { } probabilityPercent)
            {
                if (decisionThreshold is { } threshold && result.Label == PredictionResult.UnknownLabel
                    && localize(NoDecisionKey) is { } noDecision && !string.IsNullOrWhiteSpace(noDecision))
                    return string.Format(culture, noDecision, contract.HorizonBars, probabilityPercent,
                        Math.Round(100.0 * threshold, DefaultProbabilityDecimalPlaces, DefaultRounding).ToString($"F{DefaultProbabilityDecimalPlaces}", culture));

                var withProbability = localize(ClassLabelWithProbabilityKey);
                if (string.IsNullOrWhiteSpace(withProbability)) return null;
                return string.Format(culture, withProbability, contract.HorizonBars, result.Label, probabilityPercent);
            }

            var template = localize(ClassLabelKey);
            if (string.IsNullOrWhiteSpace(template)) return null;
            return string.Format(culture, template, contract.HorizonBars, result.Label);
        }

        return null;
    }

    /// <summary>
    /// The top-class probability as a percentage text, or null when the result carries no real probability
    /// (an absent, non-finite or out-of-range value is never replaced by a fabricated number).
    /// </summary>
    private static string? TryFormatProbabilityPercent(PredictionResult result, CultureInfo culture)
    {
        if (result.ConfidenceType is not (ConfidenceType.ClassProbability or ConfidenceType.CalibratedProbability)
            || result.ConfidenceValue is not { } probability
            || !float.IsFinite(probability) || probability < 0f || probability > 1f)
            return null;
        double percent = Math.Round(100.0 * probability, DefaultProbabilityDecimalPlaces, DefaultRounding);
        return percent.ToString($"F{DefaultProbabilityDecimalPlaces}", culture);
    }
}
