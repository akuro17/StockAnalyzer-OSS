using System;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// The single place that turns a <see cref="double"/> price of the numeric coordinate API (<c>NumericToScreen(x, y)</c>) into a screen Y through the
/// decimal price axis. A price that has no screen position (NaN, an infinity, 2^96 or more, or a value whose axis arithmetic leaves the decimal range)
/// becomes NaN instead of an <see cref="OverflowException"/>, because every drawing tool and renderer calls the transform on the render path.
/// </summary>
internal static class NumericPriceAxis
{
    /// <summary>The screen Y of <paramref name="y"/> on <paramref name="transform"/>'s price axis, or NaN when it cannot be positioned. Allocation-free.</summary>
    public static double GetScreenY(ICoordinateTransform transform, double y)
    {
        if (!double.IsFinite(y))
        {
            return double.NaN;
        }

        try
        {
            return transform.GetYFromPrice((decimal)y);
        }
        catch (OverflowException)
        {
            return double.NaN;
        }
    }
}
