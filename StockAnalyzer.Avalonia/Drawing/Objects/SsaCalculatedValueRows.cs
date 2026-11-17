using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Drawing;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;

namespace StockAnalyzer.Avalonia.Drawing.Objects;

/// <summary>
/// SSoT for the SSA diagnostic rows shown in the Data Window by every SSA drawing object
/// (separability of the reconstructed components and the nu^2 forecast-stability indicator).
/// </summary>
internal static class SsaCalculatedValueRows
{
    public const string SeparabilityKey = "SSA Separability";
    public const string StabilityKey = "SSA Stability (ν²)";

    /// <summary>Separability score row: percent value with the <see cref="SsaDiagnostics.GetSeparabilityGrade"/> label.</summary>
    public static DrawingCalculatedValue Separability(double separabilityScore)
    {
        string grade = SsaDiagnostics.GetSeparabilityGrade(separabilityScore);
        return new DrawingCalculatedValue(
            SeparabilityKey,
            SeparabilityKey,
            IndicatorCalculationHelper.ToDecimalOrNull(separabilityScore),
            string.Create(DrawingValueFormat.Culture, $"{separabilityScore:F1}% ({grade})"),
            separabilityScore >= SsaDiagnostics.GoodSeparabilityScore ? IndicatorColor.Bullish : IndicatorColor.Bearish);
    }

    /// <summary>Forecast-stability row: nu^2 with a Stable / Caution label.</summary>
    public static DrawingCalculatedValue Stability(double nuSquared, bool isStable)
    {
        return new DrawingCalculatedValue(
            StabilityKey,
            StabilityKey,
            IndicatorCalculationHelper.ToDecimalOrNull(nuSquared),
            string.Create(DrawingValueFormat.Culture, $"{nuSquared:F3} ({(isStable ? "Stable" : "Caution")})"),
            isStable ? IndicatorColor.Bullish : IndicatorColor.Bearish);
    }
}
