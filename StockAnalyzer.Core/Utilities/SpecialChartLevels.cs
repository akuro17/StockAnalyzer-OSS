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

        bool isUp = IsUpColumn(opens, closes, index);
        return TryDescribeColumn(isUp, isUp ? highs[index] : lows[index], boxSize, reversalAmount, out reversalPrice, out updatePrice);
    }

    /// <summary>
    /// The same two prices from the direction and the END of a column (the top of an X column, the bottom of an O column): the form the incremental overlay engine has, which knows its
    /// direction exactly. <see cref="TryDescribe"/> delegates here, so there is one formula. False (both 0) for a non-positive box size or fewer boxes than
    /// <see cref="ChartConstants.MinPnfReversalAmount"/>.
    /// </summary>
    public static bool TryDescribeColumn(bool isUp, decimal columnEnd, decimal boxSize, int reversalAmount, out decimal reversalPrice, out decimal updatePrice)
    {
        reversalPrice = 0m;
        updatePrice = 0m;
        if (boxSize <= 0m || reversalAmount < ChartConstants.MinPnfReversalAmount) return false;

        decimal reversalDistance = boxSize * reversalAmount;
        if (isUp)
        {
            reversalPrice = columnEnd - reversalDistance;
            updatePrice = columnEnd + boxSize;
        }
        else
        {
            reversalPrice = columnEnd + reversalDistance;
            updatePrice = columnEnd - boxSize;
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

        reversalPrice = ReversalPrice(last.IsBullish, last.Close, threshold);
        return true;
    }

    /// <summary>The reversal price from the direction and the end price of the current column: an up column reverses at its end minus the threshold, a down column at its end plus it. The single formula of the Data tab and of the Kagi overlay engine.</summary>
    public static decimal ReversalPrice(bool isUp, decimal columnEnd, decimal threshold) => isUp ? columnEnd - threshold : columnEnd + threshold;
}

/// <summary>
/// The single definition of the "Reversal" and "Update" price of a Renko brick. Before the block chart overlays the same two formulas existed only inline in the Data tab view model;
/// the overlays (<c>RenkoBarEngine</c>) use this class, and the Data tab keeps its own inline copy until it is switched over in a separate task (a parity test pins both to the same numbers).
/// <list type="bullet">
/// <item>Up brick: Reversal = brick bottom - R x size, Update = brick top + size. Down brick: Reversal = brick top + R x size, Update = brick bottom - size.</item>
/// <item>R (<paramref name="reversalBricks"/>) is measured from the brick EDGE nearest to the reversal (the bottom of an up brick, the top of a down brick), as the calculator does
/// (<c>close &lt;= bottom - R x size</c> reverses an up trend, both boundaries inclusive).</item>
/// </list>
/// Nothing here allocates or throws: false means the value is not defined for the input.
/// </summary>
public static class RenkoLevels
{
    public static bool TryDescribe(bool isUp, decimal brickHigh, decimal brickLow, decimal size, int reversalBricks, out decimal reversalPrice, out decimal updatePrice)
    {
        reversalPrice = 0m;
        updatePrice = 0m;
        if (size <= 0m || reversalBricks < ChartConstants.MinRenkoReversalBricks) return false;

        decimal reversalDistance = size * reversalBricks;
        if (isUp)
        {
            reversalPrice = brickLow - reversalDistance;
            updatePrice = brickHigh + size;
        }
        else
        {
            reversalPrice = brickHigh + reversalDistance;
            updatePrice = brickLow - size;
        }

        return true;
    }
}
