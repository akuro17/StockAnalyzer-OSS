using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Utilities;

/// <summary>
/// The single definition of the "Reversal" and "Update" price of a Point and Figure column, shown by the Data tab. All values are prices (decimal); nothing here allocates
/// or throws: <c>false</c> means that the value is not defined for the given input (the Data tab then shows nothing).
/// <list type="bullet">
/// <item>An X (up) column is stored with Open = bottom and Close = top, an O (down) column with Open = top and Close = bottom (see <c>PointAndFigureCalculator</c>).</item>
/// <item>X column: Reversal = top - R x box, Update = top + box. O column: Reversal = bottom + R x box, Update = bottom - box. R is the number of boxes of a reversal.</item>
/// <item>Reversal is the boundary of the reversal condition of the calculator (X: <c>low &lt;= top - box x R</c>, O: <c>high &gt;= bottom + box x R</c>, both inclusive). Update is the
/// nominal price of the next box, whatever the rounding mode of the chart is.</item>
/// </list>
/// </summary>
public static class PointAndFigureLevels
{
    /// <summary>
    /// Describes the column at <paramref name="index"/>. The four spans are the columns of one chart and must have the same length. Returns false when a span differs in
    /// length, the index is outside them, <paramref name="boxSize"/> is not positive or <paramref name="reversalAmount"/> is below the fewest boxes of a reversal.
    /// </summary>
    public static bool TryDescribe(
        ReadOnlySpan<decimal> opens, ReadOnlySpan<decimal> highs,
        ReadOnlySpan<decimal> lows, ReadOnlySpan<decimal> closes,
        int index, decimal boxSize, int reversalAmount,
        out decimal reversalPrice, out decimal updatePrice)
    {
        reversalPrice = 0m;
        updatePrice = 0m;

        int count = opens.Length;
        if (highs.Length != count || lows.Length != count || closes.Length != count) return false;
        if (index < 0 || index >= count) return false;
        if (boxSize <= 0m || reversalAmount < ChartConstants.MinPnfReversalAmount) return false;

        decimal reversalDistance = boxSize * reversalAmount;
        if (IsUpColumn(opens, closes, index))
        {
            reversalPrice = highs[index] - reversalDistance;
            updatePrice = highs[index] + boxSize;
        }
        else
        {
            reversalPrice = lows[index] + reversalDistance;
            updatePrice = lows[index] - boxSize;
        }

        return true;
    }

    /// <summary>
    /// The direction of a column: Close above Open is an X column, Close below Open an O column. A column of one box has Open == Close; the calculator makes X and O columns
    /// strictly alternate, so it goes the way of the nearest column before it that has a direction, flipped once for every column between them. When no column up to
    /// <paramref name="index"/> has a direction, the column is an X column.
    /// </summary>
    private static bool IsUpColumn(ReadOnlySpan<decimal> opens, ReadOnlySpan<decimal> closes, int index)
    {
        for (int k = index; k >= 0; k--)
        {
            if (closes[k] == opens[k]) continue;

            bool directionOfK = closes[k] > opens[k];
            bool sameParity = ((index - k) & 1) == 0;
            return sameParity ? directionOfK : !directionOfK;
        }

        return true;
    }
}

/// <summary>
/// The single definition of the "Reversal" price of a Kagi column, shown by the Data tab (the column is <see cref="KagiColumns"/>). The value is the price a close has to reach
/// to reverse the column: an up column reverses at its high minus the reversal amount, a down column at its low plus the reversal amount (the boundary of the reversal condition
/// of the calculator, which is inclusive and uses closes only). Nothing here allocates or throws.
/// </summary>
public static class KagiLevels
{
    /// <summary>
    /// Describes the column that contains the segment at <paramref name="index"/>. Returns false when the segments are missing, the index is outside them, the threshold is not
    /// positive or the column is the flat first segment (the anchor), which has no direction.
    /// </summary>
    public static bool TryGetReversalPrice(IReadOnlyList<CoreCandleData>? segments, int index, decimal threshold, out decimal reversalPrice)
    {
        reversalPrice = 0m;
        if (segments == null || index < 0 || index >= segments.Count || threshold <= 0m) return false;

        var (_, columnEnd) = KagiColumns.ColumnBounds(segments, index);
        var last = segments[columnEnd];
        if (last.Open == last.Close) return false;

        reversalPrice = last.IsBullish ? last.Close - threshold : last.Close + threshold;
        return true;
    }
}
