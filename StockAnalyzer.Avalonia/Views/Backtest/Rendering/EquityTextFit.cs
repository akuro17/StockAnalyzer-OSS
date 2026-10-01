using System;

namespace StockAnalyzer.Avalonia.Views.Backtest.Rendering;

/// <summary>Pure text fitting for the equity chart's labels and panels (Avalonia-free: the width measure is injected).</summary>
public static class EquityTextFit
{
    /// <summary>The marker that replaces the cut-off end of a text (same "..." as the other HUD labels).</summary>
    public const string Ellipsis = "...";

    /// <summary>
    /// The text itself when <paramref name="measure"/> says it fits in <paramref name="maxWidth"/>; otherwise its longest prefix that still
    /// fits together with <see cref="Ellipsis"/> (binary search on the prefix length); the ellipsis alone when not even one character fits.
    /// </summary>
    public static string Truncate(string text, double maxWidth, Func<string, double> measure)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(measure);
        if (text.Length == 0 || measure(text) <= maxWidth)
        {
            return text;
        }

        int low = 0;
        int high = text.Length - 1;
        while (low < high)
        {
            int middle = low + ((high - low + 1) / 2);
            if (measure(string.Concat(text.AsSpan(0, middle), Ellipsis)) <= maxWidth) low = middle;
            else high = middle - 1;
        }
        return string.Concat(text.AsSpan(0, low), Ellipsis);
    }
}
