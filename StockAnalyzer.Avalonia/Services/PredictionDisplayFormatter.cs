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

    /// <summary>
    /// Formats a current result. The model framework is deliberately absent from this API.
    /// A null return means the result is unavailable or belongs to another chart timeframe.
    /// </summary>
    public static string? Format(PredictionResult result, TimeframeType chartTimeframe,
        CultureInfo culture, Func<string, string?> localize)
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
            var template = localize(ClassLabelKey);
            if (string.IsNullOrWhiteSpace(template)) return null;
            return string.Format(culture, template, contract.HorizonBars, result.Label);
        }

        return null;
    }
}
