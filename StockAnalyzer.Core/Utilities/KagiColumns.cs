using System.Collections.Generic;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Utilities;

/// <summary>
/// The single definition of a Kagi column. A Kagi chart is a list of segments; the segments that go in one direction are drawn in one column, so a column can hold
/// several segments (a thin and a thick line of the same direction are separate segments). A flat first segment (Open == Close, the anchor) never shares a column with the
/// segment after it. The renderer, the pointer lookup and the Data tab all take their columns from here.
/// </summary>
public static class KagiColumns
{
    /// <summary>True when the segment at <paramref name="index"/> starts a new column, that is when it is not the first segment and either its direction differs from the segment before it or the segment before it is the flat anchor.</summary>
    public static bool StartsNewColumn(IReadOnlyList<CoreCandleData> segments, int index)
    {
        if (index <= 0)
        {
            return false;
        }

        bool isAnchorTransition = index == 1 && segments[0].Open == segments[0].Close;
        bool isDirectionChange = segments[index].IsBullish != segments[index - 1].IsBullish;
        return isDirectionChange || isAnchorTransition;
    }

    /// <summary>Fills <paramref name="columnOfSegment"/> with the column number of every segment; the first segment is in column <paramref name="firstColumn"/>.</summary>
    public static void BuildColumnMap(IReadOnlyList<CoreCandleData> segments, int firstColumn, List<int> columnOfSegment)
    {
        columnOfSegment.Clear();
        int column = firstColumn;
        for (int i = 0; i < segments.Count; i++)
        {
            if (StartsNewColumn(segments, i))
            {
                column++;
            }

            columnOfSegment.Add(column);
        }
    }

    /// <summary>The first and the last segment of the column that contains the segment at <paramref name="index"/>. Does not allocate.</summary>
    public static (int Start, int End) ColumnBounds(IReadOnlyList<CoreCandleData> segments, int index)
    {
        int start = index;
        while (start > 0 && !StartsNewColumn(segments, start))
        {
            start--;
        }

        int end = index;
        while (end < segments.Count - 1 && !StartsNewColumn(segments, end + 1))
        {
            end++;
        }

        return (start, end);
    }
}
