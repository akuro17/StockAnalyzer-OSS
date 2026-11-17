using System.Globalization;
using StockAnalyzer.Core.Models.Drawing;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>
/// The one rule of the saved-drawing tools for an as-of date (endDate): a saved object is a look-ahead suspect when ANY of its control points is dated after endDate, compared by the calendar
/// date of the point (never by the stamp of the last bar: a weekly or monthly bar is stamped at the start of its period, so the stamp of the last bar would call a point inside the last
/// period "after"). The test is a necessary sign only: the drawing file stores no creation time, so an object whose points are all on or before endDate may still have been drawn later, and
/// an object anchored on the still open period of the last bar is not caught. Without an endDate nothing is checked. An object without time-based control points has no date to compare.
/// </summary>
internal static class DrawingAsOfRule
{
    /// <summary>True when a control point of the object is dated after <paramref name="endDate"/>.</summary>
    public static bool IsAfter(DrawingObjectRecord record, DateOnly endDate)
    {
        foreach (DrawingStoredPoint point in record.Points)
        {
            if (point.UtcTime is { } time && DateOnly.FromDateTime(time) > endDate)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The warning of sa_get_chart_drawings that names how many returned objects are look-ahead suspects.</summary>
    public static string SuspectWarning(int count)
        => $"{count.ToString(CultureInfo.InvariantCulture)} object(s) have a control point dated after endDate (lookAheadSuspect true); their points and calculatedValues use that later point, so they may not have been known at endDate.";

    /// <summary>The warning of sa_render_chart_image that names how many saved drawings were left out for the same reason.</summary>
    public static string ExcludedWarning(int count)
        => $"{count.ToString(CultureInfo.InvariantCulture)} saved drawing(s) have a control point dated after endDate and are not drawn (they may not have been known at endDate); omit endDate to draw them.";
}
