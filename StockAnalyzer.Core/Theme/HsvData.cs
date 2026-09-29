using System;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Theme;

/// <summary>
/// A UI-agnostic struct for storing precise HSV (Hue, Saturation, Value, Alpha) color data.
/// Created to prevent round-trip data loss when dealing with pure black colors in UI frameworks.
/// </summary>
public readonly record struct HsvData
{
    public double A { get; init; } // 0.0 to 1.0
    public double H { get; init; } // 0.0 to 360.0
    public double S { get; init; } // 0.0 to 1.0
    public double V { get; init; } // 0.0 to 1.0

    public HsvData(double a, double h, double s, double v)
    {
        A = Math.Clamp(a, 0.0, 1.0);
        H = Math.Clamp(h, 0.0, 360.0);
        S = Math.Clamp(s, 0.0, 1.0);
        V = Math.Clamp(v, 0.0, 1.0);
    }

    /// <summary>
    /// Converts an IndicatorColor to HsvData.
    /// </summary>
    public static HsvData FromColor(IndicatorColor color)
    {
        double a = color.A / 255.0;
        double r = color.R / 255.0;
        double g = color.G / 255.0;
        double b = color.B / 255.0;

        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;

        double h = 0;
        if (delta > 0)
        {
            if (max == r) h = (g - b) / delta % 6;
            else if (max == g) h = (b - r) / delta + 2;
            else h = (r - g) / delta + 4;
            h *= 60;
            if (h < 0) h += 360;
        }

        double s = max == 0 ? 0 : delta / max;
        double v = max;

        return new HsvData(a, h, s, v);
    }

    /// <summary>
    /// Converts an IndicatorColor to HsvData.
    /// </summary>
    public static HsvData FromIndicatorColor(IndicatorColor color) => FromColor(color);

    /// <summary>
    /// Converts HsvData to an IndicatorColor.
    /// </summary>
    public IndicatorColor ToIndicatorColor()
    {
        double c = V * S;
        double x = c * (1 - Math.Abs((H / 60.0) % 2 - 1));
        double m = V - c;

        double r1, g1, b1;
        if (H < 60) { r1 = c; g1 = x; b1 = 0; }
        else if (H < 120) { r1 = x; g1 = c; b1 = 0; }
        else if (H < 180) { r1 = 0; g1 = c; b1 = x; }
        else if (H < 240) { r1 = 0; g1 = x; b1 = c; }
        else if (H < 300) { r1 = x; g1 = 0; b1 = c; }
        else { r1 = c; g1 = 0; b1 = x; }

        byte a = (byte)Math.Clamp(Math.Round(A * 255.0), 0, 255);
        byte r = (byte)Math.Clamp(Math.Round((r1 + m) * 255.0), 0, 255);
        byte g = (byte)Math.Clamp(Math.Round((g1 + m) * 255.0), 0, 255);
        byte b = (byte)Math.Clamp(Math.Round((b1 + m) * 255.0), 0, 255);

        return new IndicatorColor(a, r, g, b);
    }

    [Obsolete("Use FromIndicatorColor instead")]
    public static HsvData FromSkColor(IndicatorColor color) => FromIndicatorColor(color);

    [Obsolete("Use ToIndicatorColor instead")]
    public IndicatorColor ToSkColor() => ToIndicatorColor();

    /// <summary>
    /// Alias for ToIndicatorColor to support existing ViewModel code.
    /// </summary>
    public IndicatorColor ToColor() => ToIndicatorColor();

    /// <summary>
    /// Parses an HTML color string (#AARRGGBB or #RRGGBB) to HsvData.
    /// Defaults to the provided fallback on failure.
    /// </summary>
    public static HsvData FromHtmlSafe(string html, HsvData fallback)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(html)) return fallback;
            
            string clean = html.Trim().TrimStart('#');
            if (clean.Length == 6)
            {
                byte r = Convert.ToByte(clean.Substring(0, 2), 16);
                byte g = Convert.ToByte(clean.Substring(2, 2), 16);
                byte b = Convert.ToByte(clean.Substring(4, 2), 16);
                return FromIndicatorColor(new IndicatorColor(255, r, g, b));
            }
            if (clean.Length == 8)
            {
                byte a = Convert.ToByte(clean.Substring(0, 2), 16);
                byte r = Convert.ToByte(clean.Substring(2, 2), 16);
                byte g = Convert.ToByte(clean.Substring(4, 2), 16);
                byte b = Convert.ToByte(clean.Substring(6, 2), 16);
                return FromIndicatorColor(new IndicatorColor(a, r, g, b));
            }
            return fallback;
        }
        catch
        {
            return fallback;
        }
    }

    /// <summary>
    /// Parses an HTML color string (#AARRGGBB or #RRGGBB) to HsvData.
    /// Safe version that defaults to Gray on failure.
    /// </summary>
    public static HsvData FromHtmlSafe(string html) => FromHtmlSafe(html, FromIndicatorColor(IndicatorColor.Gray));

    /// <summary>
    /// Converts HsvData to an HTML hex string (#AARRGGBB).
    /// </summary>
    public string ToHtml()
    {
        var c = ToIndicatorColor();
        return $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
    }
}
