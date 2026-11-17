using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Drawing.Values;

/// <summary>
/// Data-tab values of the anchored VWAP (the single definition; the GUI object and the MCP tool both call it). The curve is
/// <see cref="VolumeAnalysis.CalculateAnchoredVwap"/> over the bars from the anchor on; the bands are the volume-weighted standard deviation times the multiplier.
/// </summary>
public static class AnchoredVwapValues
{
    /// <summary>The multiplier of the first band pair a new tool starts with.</summary>
    public const decimal DefaultBand1Multiplier = 1.0m;

    /// <summary>The multiplier of the second band pair a new tool starts with.</summary>
    public const decimal DefaultBand2Multiplier = IndicatorDefaultConstants.VwapStdDevMultiplier;

    /// <summary>The smallest band multiplier.</summary>
    public const decimal MinBandMultiplier = IndicatorDefaultConstants.VwapMinStdDevMultiplierDecimal;

    /// <summary>The largest band multiplier.</summary>
    public const decimal MaxBandMultiplier = IndicatorDefaultConstants.VwapMaxStdDevMultiplierDecimal;

    /// <summary>Text of the AnchorDate value; formatted with <see cref="DrawingValueFormat.Culture"/> (en-US, so the '/' stays a '/').</summary>
    public const string AnchorDateFormat = "yyyy/MM/dd";

    /// <summary>
    /// The index of the newest curve point at or before the time; the last point when none is (the GUI has always fallen back to the latest, and keeps doing so). The curve must not be empty.
    /// The fallback is the one place that can return a point later than the time, so it is a contract on the caller: a caller that must stay causal (the MCP tools) passes only bars up to the
    /// evaluation time, and then an anchor after the last bar leaves no curve at all (no values), never a fallback.
    /// </summary>
    public static int MatchedIndex(IReadOnlyList<(DateTime Time, decimal Price)> curve, DateTime timestamp)
    {
        for (int i = curve.Count - 1; i >= 0; i--)
        {
            if (curve[i].Time <= timestamp)
            {
                return i;
            }
        }

        return curve.Count - 1;
    }

    /// <summary>The values at the matched curve point.</summary>
    /// <param name="stdDev">The volume-weighted standard deviation at that point; null while there is no volume yet (the band items then show "-").</param>
    public static IReadOnlyList<DrawingCalculatedValue> Calculate(
        DateTime anchorTime,
        decimal vwapPrice,
        decimal? stdDev,
        bool showBand1,
        decimal band1Multiplier,
        bool showBand2,
        decimal band2Multiplier,
        IndicatorColor color)
    {
        string anchorText = anchorTime.ToString(AnchorDateFormat, DrawingValueFormat.Culture);
        string vwapText = string.Create(DrawingValueFormat.Culture, $"{vwapPrice:F3}");
        if (!showBand1 && !showBand2)
        {
            return new DrawingCalculatedValue[]
            {
                new("AnchorDate", "Anchor Date", null, anchorText, IndicatorColor.Gray),
                new("AnchoredVWAP", "Anchored VWAP", vwapPrice, vwapText, color),
            };
        }

        int extraCount = (showBand1 ? 2 : 0) + (showBand2 ? 2 : 0);
        var values = new DrawingCalculatedValue[2 + extraCount];
        values[0] = new DrawingCalculatedValue("AnchorDate", "Anchor Date", null, anchorText, IndicatorColor.Gray);
        values[1] = new DrawingCalculatedValue("AnchoredVWAP", "Anchored VWAP", vwapPrice, vwapText, color);

        int writeIdx = 2;
        if (showBand1)
        {
            WriteBand(values, ref writeIdx, "Upper1", "Upper 1", "Lower1", "Lower 1", vwapPrice, stdDev, band1Multiplier, color);
        }

        if (showBand2)
        {
            WriteBand(values, ref writeIdx, "Upper2", "Upper 2", "Lower2", "Lower 2", vwapPrice, stdDev, band2Multiplier, color);
        }

        return values;
    }

    /// <summary>
    /// The values calculated from bars (<paramref name="candles"/> oldest first): the curve starts at the first bar at or after the anchor time.
    /// No bar at or after the anchor gives no values. The bars passed decide what is known: bars later than <paramref name="timestamp"/> may be matched by the fallback of <see cref="MatchedIndex"/>,
    /// so a causal caller passes bars up to the evaluation time only.
    /// </summary>
    public static IReadOnlyList<DrawingCalculatedValue> Calculate(
        IReadOnlyList<CoreCandleData> candles,
        DateTime anchorTime,
        DateTime timestamp,
        PriceType priceSource,
        bool showBand1,
        decimal band1Multiplier,
        bool showBand2,
        decimal band2Multiplier,
        IndicatorColor color)
    {
        var relevant = new List<CoreCandleData>();
        for (int i = 0; i < candles.Count; i++)
        {
            if (candles[i].Timestamp >= anchorTime)
            {
                relevant.Add(candles[i]);
            }
        }

        if (relevant.Count == 0)
        {
            return Array.Empty<DrawingCalculatedValue>();
        }

        var curve = new List<(DateTime Time, decimal Price)>(relevant.Count);
        List<decimal?>? deviations = null;
        if (showBand1 || showBand2)
        {
            deviations = new List<decimal?>(relevant.Count);
            foreach (var point in VolumeAnalysis.CalculateAnchoredVwapWithDeviation(relevant, priceSource))
            {
                curve.Add((point.Time, point.Vwap));
                deviations.Add(point.StdDev);
            }
        }
        else
        {
            foreach (var point in VolumeAnalysis.CalculateAnchoredVwap(relevant, priceSource))
            {
                curve.Add((point.Time, point.Vwap));
            }
        }

        int matched = MatchedIndex(curve, timestamp);
        decimal? stdDev = deviations is not null && deviations.Count == curve.Count ? deviations[matched] : null;
        return Calculate(anchorTime, curve[matched].Price, stdDev, showBand1, band1Multiplier, showBand2, band2Multiplier, color);
    }

    private static void WriteBand(
        DrawingCalculatedValue[] target,
        ref int writeIdx,
        string upperKey,
        string upperLabel,
        string lowerKey,
        string lowerLabel,
        decimal vwapPrice,
        decimal? stdDev,
        decimal multiplier,
        IndicatorColor color)
    {
        if (stdDev is { } sigma)
        {
            decimal offset = multiplier * sigma;
            decimal upper = vwapPrice + offset;
            decimal lower = vwapPrice - offset;
            target[writeIdx++] = new DrawingCalculatedValue(upperKey, upperLabel, upper, string.Create(DrawingValueFormat.Culture, $"{upper:F3}"), color);
            target[writeIdx++] = new DrawingCalculatedValue(lowerKey, lowerLabel, lower, string.Create(DrawingValueFormat.Culture, $"{lower:F3}"), color);
        }
        else
        {
            target[writeIdx++] = new DrawingCalculatedValue(upperKey, upperLabel, null, "-", color);
            target[writeIdx++] = new DrawingCalculatedValue(lowerKey, lowerLabel, null, "-", color);
        }
    }
}
