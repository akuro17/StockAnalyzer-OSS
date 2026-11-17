using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;

namespace StockAnalyzer.Core.Drawing.Values;

/// <summary>
/// Data-tab values of the Gann square of nine: Target = (Sqrt(Price) +/- Factor)^2 for the factors of the 45-degree steps up to 360 degrees
/// (the single definition; the GUI object and the MCP tool both call it). Sqrt and Pow are evaluated in double, as the GUI always did, and each result is narrowed once through
/// <see cref="IndicatorCalculationHelper.ToDecimalOrNull"/>: a level the decimal range cannot hold is left out of the list (never an exception, never a wrong number).
/// </summary>
public static class GannSquareOfNineValues
{
    private static readonly double[] FactorsArray = { 0.25, 0.5, 0.75, 1.0, 1.25, 1.5, 1.75, 2.0 };
    private static readonly string[] LabelsArray = { "45°", "90°", "135°", "180°", "225°", "270°", "315°", "360°" };

    /// <summary>The factor table: 0.25 per 45 degrees, 2.0 at 360 degrees (also the table the GUI draws).</summary>
    public static ReadOnlySpan<double> Factors => FactorsArray;

    /// <summary>The angle labels, one per factor.</summary>
    public static ReadOnlySpan<string> Labels => LabelsArray;

    /// <summary>Returns no values for a negative base price (no square root); a zero price gives the base item and the resistances.</summary>
    public static IReadOnlyList<DrawingCalculatedValue> Calculate(DrawingAnchor start, IndicatorColor color)
    {
        decimal basePrice = start.Price;
        if (basePrice < 0m)
        {
            return Array.Empty<DrawingCalculatedValue>();
        }

        double baseSqrt = Math.Sqrt((double)basePrice);
        var values = new List<DrawingCalculatedValue>(FactorsArray.Length * 2 + 1)
        {
            new("Base", "Base Price", basePrice, string.Create(DrawingValueFormat.Culture, $"{basePrice:F3}"), IndicatorColor.Gray),
        };

        for (int i = 0; i < FactorsArray.Length; i++)
        {
            if (IndicatorCalculationHelper.ToDecimalOrNull(Math.Pow(baseSqrt + FactorsArray[i], 2)) is { } priceUp)
            {
                values.Add(new DrawingCalculatedValue($"Res_{LabelsArray[i]}", $"Resistance +{LabelsArray[i]}", priceUp, string.Create(DrawingValueFormat.Culture, $"{priceUp:F3}"), color));
            }
        }

        for (int i = 0; i < FactorsArray.Length; i++)
        {
            double sqrtDown = baseSqrt - FactorsArray[i];
            if (sqrtDown > 0 && IndicatorCalculationHelper.ToDecimalOrNull(Math.Pow(sqrtDown, 2)) is { } priceDown)
            {
                values.Add(new DrawingCalculatedValue($"Sup_{LabelsArray[i]}", $"Support -{LabelsArray[i]}", priceDown, string.Create(DrawingValueFormat.Culture, $"{priceDown:F3}"), SupportColor));
            }
        }

        return values;
    }

    /// <summary>The colour the support items have always had (Material blue 500).</summary>
    private static readonly IndicatorColor SupportColor = IndicatorColor.FromRgb(33, 150, 243);
}
