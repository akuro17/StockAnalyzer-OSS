using System;
using Avalonia.Media;
using SkiaSharp;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// The one conversion of a fill opacity setting (0-100 %) to the alpha byte of a Skia colour: <c>255 * percent / 100</c> in integer arithmetic (rounded down). The default Robust Regression
/// Trend opacity of 11 % would give 28, the constant alpha the sibling range curve tools have always used; the tools that offer the setting start at <see cref="DefaultPercent"/> (15 %, alpha 38).
/// </summary>
internal static class DrawingFillOpacity
{
    /// <summary>The largest opacity setting.</summary>
    internal const int FullPercent = 100;

    /// <summary>The opacity (percent) a new fill starts with: the GARCH cone, the Robust Regression Trend and the three areas of the Regular Polygon.</summary>
    internal const int DefaultPercent = 15;

    /// <summary>The alpha byte of <paramref name="percent"/> (clamped to 0..100).</summary>
    internal static byte ToAlpha(int percent) => (byte)(byte.MaxValue * Math.Clamp(percent, 0, FullPercent) / FullPercent);

    /// <summary>
    /// The colour a fill colour setting keeps: a value with the alpha 0 becomes the default drawing colour. The alpha of a fill colour is never drawn (the opacity setting decides it), and
    /// <c>DrawingDocumentCodec</c> turns a saved <c>null</c> into <c>default(Color)</c> (transparent black), so an alpha of 0 can only mean "no colour was saved" and must not become a black fill.
    /// </summary>
    internal static Color NormalizeFillColor(Color value) => value.A == 0 ? DrawingThemeContext.DefaultColor : value;

    /// <summary>The colour of a filled area: the colour of the fill setting (its own alpha is ignored) with the alpha of <paramref name="percent"/>.</summary>
    internal static SKColor Resolve(Color fill, int percent) => new SKColor(fill.R, fill.G, fill.B, ToAlpha(percent));
}
