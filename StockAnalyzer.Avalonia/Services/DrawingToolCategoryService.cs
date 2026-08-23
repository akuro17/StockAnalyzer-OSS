namespace StockAnalyzer.Avalonia.Services;

using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Models;
using System.Collections.Generic;

/// <summary>
/// Provides the canonical 9-category definition of all drawing tools.
/// Icons follow the specification: geometric/mathematical symbols for tool essence.
/// </summary>
public static class DrawingToolCategoryService
{
    public static IReadOnlyList<DrawingToolCategoryDefinition> GetCategories()
    {
        return new[]
        {
            // 1. Cursors
            new DrawingToolCategoryDefinition(
                NameKey: "DrawCat_Cursors",
                Icon: "\U0001F446", // 👆
                Tools: new[]
                {
                    new DrawingToolInfo(DrawingTool.Eraser, "\U0001F9F9", "DrawTool_Eraser"),
                    new DrawingToolInfo(DrawingTool.Pointer, "\U0001F446", "DrawTool_Pointer"),
                    new DrawingToolInfo(DrawingTool.Ruler, "\U0001F4CF", "DrawTool_Ruler"),
                }),

            // 2. Lines & Trend
            new DrawingToolCategoryDefinition(
                NameKey: "DrawCat_Lines",
                Icon: "\uFF0F", // ／
                Tools: new[]
                {
                    new DrawingToolInfo(DrawingTool.AngleTool, "\u2220", "DrawTool_AngleTool"),
                    new DrawingToolInfo(DrawingTool.Arrow, "\u2197", "DrawTool_Arrow"),
                    new DrawingToolInfo(DrawingTool.CatenaryCurve, "\u2312", "DrawTool_CatenaryCurve"),
                    new DrawingToolInfo(DrawingTool.CurveTrend, "\u2312", "DrawTool_CurveTrend"),
                    new DrawingToolInfo(DrawingTool.HorizontalLine, "\u2500", "DrawTool_HorizontalLine"),
                    new DrawingToolInfo(DrawingTool.NurbsConicArc, "\u2312", "DrawTool_NurbsConic"),
                    new DrawingToolInfo(DrawingTool.NurbsHyperbola, "\u22D9", "DrawTool_NurbsHyperbola"),
                    new DrawingToolInfo(DrawingTool.NurbsParabola, "\u222A", "DrawTool_NurbsParabola"),
                    new DrawingToolInfo(DrawingTool.NurbsTrendCurve, "\u223F", "DrawTool_NurbsTrendCurve"),
                    new DrawingToolInfo(DrawingTool.Polyline, "\u29E2", "DrawTool_Polyline"),
                    new DrawingToolInfo(DrawingTool.Ray, "\u2198", "DrawTool_Ray"),
                    new DrawingToolInfo(DrawingTool.TrendLine, "\uFF0F", "DrawTool_TrendLine"),
                    new DrawingToolInfo(DrawingTool.VerticalLine, "\u2502", "DrawTool_VerticalLine"),
                }),

            // 3. Shapes
            new DrawingToolCategoryDefinition(
                NameKey: "DrawCat_Shapes",
                Icon: "\u2B1C", // ⬜
                Tools: new[]
                {
                    new DrawingToolInfo(DrawingTool.Ellipse, "\u2B55", "DrawTool_Ellipse"),
                    new DrawingToolInfo(DrawingTool.NurbsConic, "\u25CE", "DrawTool_NurbsCircle"),
                    new DrawingToolInfo(DrawingTool.NurbsEllipse, "\u2B2D", "DrawTool_NurbsEllipse"),
                    new DrawingToolInfo(DrawingTool.Rectangle, "\u2B1C", "DrawTool_Rectangle"),
                    new DrawingToolInfo(DrawingTool.Triangle, "\U0001F53A", "DrawTool_Triangle"),
                }),

            // 4. Text
            new DrawingToolCategoryDefinition(
                NameKey: "DrawCat_Text",
                Icon: "\U0001F4DD", // 📝
                Tools: new[]
                {
                    new DrawingToolInfo(DrawingTool.Callout, "\U0001F4AC", "DrawTool_Callout"),
                    new DrawingToolInfo(DrawingTool.CurveLineText, "\U0001F30A", "DrawTool_CurveLineText"),
                    new DrawingToolInfo(DrawingTool.LineText, "\U0001F524", "DrawTool_LineText"),
                    new DrawingToolInfo(DrawingTool.PriceLabel, "\U0001F3F7️", "DrawTool_PriceLabel"),
                    new DrawingToolInfo(DrawingTool.Text, "\U0001F4DD", "DrawTool_Text"),
                }),

            // 5. Channels & Regression
            new DrawingToolCategoryDefinition(
                NameKey: "DrawCat_Channels",
                Icon: "\u2AFD", // ⫽
                Tools: new[]
                {
                    new DrawingToolInfo(DrawingTool.CurveChannel, "\u224B", "DrawTool_CurveChannel"),
                    new DrawingToolInfo(DrawingTool.ParallelChannel, "\u2AFD", "DrawTool_ParallelChannel"),
                    new DrawingToolInfo(DrawingTool.Pitchfork, "\u22D4", "DrawTool_Pitchfork"),
                    new DrawingToolInfo(DrawingTool.RangeSpline, "\u223D", "DrawTool_RangeSpline"),
                    new DrawingToolInfo(DrawingTool.RegressionTrend, "\u223F", "DrawTool_RegressionTrend"),
                }),

            // 6. Fibonacci Tools
            new DrawingToolCategoryDefinition(
                NameKey: "DrawCat_Fibonacci",
                Icon: "\u2262", // ≢
                Tools: new[]
                {
                    new DrawingToolInfo(DrawingTool.FibonacciArc, "\u25E0", "DrawTool_FibArc"),
                    new DrawingToolInfo(DrawingTool.FibonacciChannel, "\u2226", "DrawTool_FibChannel"),
                    new DrawingToolInfo(DrawingTool.FibonacciCircle, "\u25D6", "DrawTool_FibCircle"),
                    new DrawingToolInfo(DrawingTool.FibonacciEllipse, "\u2B2D", "DrawTool_FibEllipse"),
                    new DrawingToolInfo(DrawingTool.FibonacciExpansion, "\u2913", "DrawTool_FibExpansion"),
                    new DrawingToolInfo(DrawingTool.FibonacciFan, "\u2A5A", "DrawTool_FibFan"),
                    new DrawingToolInfo(DrawingTool.FibonacciRetracement, "\u2262", "DrawTool_FibRetracement"),
                    new DrawingToolInfo(DrawingTool.FibonacciSpiral, "\U0001F300", "DrawTool_FibSpiral"),
                    new DrawingToolInfo(DrawingTool.FibonacciTimeZone, "\U0001F552", "DrawTool_FibTimeZone"),
                }),

            // 7. Gann Tools
            new DrawingToolCategoryDefinition(
                NameKey: "DrawCat_Gann",
                Icon: "\u29A2", // ⧢
                Tools: new[]
                {
                    new DrawingToolInfo(DrawingTool.GannBox, "\U0001F533", "DrawTool_GannBox"),
                    new DrawingToolInfo(DrawingTool.GannFan, "\u29A2", "DrawTool_GannFan"),
                    new DrawingToolInfo(DrawingTool.GannGrid, "\u25A6", "DrawTool_GannGrid"),
                    new DrawingToolInfo(DrawingTool.GannSquare, "\U0001F4A0", "DrawTool_GannSquare"),
                    new DrawingToolInfo(DrawingTool.GannSquare144, "\U0001F522", "DrawTool_GannSquare144"),
                    new DrawingToolInfo(DrawingTool.GannSquareOfNine, "\u2684", "DrawTool_GannSquare9"),
                    new DrawingToolInfo(DrawingTool.GannWheel, "\u2638", "DrawTool_GannWheel"),
                }),

            // 8. Patterns
            new DrawingToolCategoryDefinition(
                NameKey: "DrawCat_Patterns",
                Icon: "\u29D6", // ⧖
                Tools: new[]
                {
                    new DrawingToolInfo(DrawingTool.BarPattern, "\u2016", "DrawTool_BarPattern"),
                    new DrawingToolInfo(DrawingTool.AutoElliottWave, "\u223E", "DrawTool_AutoElliottWave"),
                    new DrawingToolInfo(DrawingTool.HarmonicPattern, "\u29D6", "DrawTool_HarmonicPattern"),
                    new DrawingToolInfo(DrawingTool.GeometricPattern, "\U0001F4D0", "DrawTool_GeometricPattern"),
                    new DrawingToolInfo(DrawingTool.DtwProjection, "\U0001F52E", "DrawTool_DtwProjection"),
                    new DrawingToolInfo(DrawingTool.KalmanFilterProjection, "\U0001F4C8", "DrawTool_KalmanFilterProjection"),
                    new DrawingToolInfo(DrawingTool.TargetPriceProjection, "\u21F2", "DrawTool_TargetPriceProjection"),
                }),

            // 9. Cycles & Volume
            new DrawingToolCategoryDefinition(
                NameKey: "DrawCat_Cycles",
                Icon: "\u21B9", // ↹
                Tools: new[]
                {
                    new DrawingToolInfo(DrawingTool.AnchoredVWAP, "\u2693", "DrawTool_AnchoredVWAP"),
                    new DrawingToolInfo(DrawingTool.CyclicLines, "\u21B9", "DrawTool_CyclicLines"),
                    new DrawingToolInfo(DrawingTool.FixedRangeVolumeProfile, "\U0001F4CA", "DrawTool_FRVP"),
                    new DrawingToolInfo(DrawingTool.LongPosition, "\U0001F7E9", "DrawTool_LongPosition"),
                    new DrawingToolInfo(DrawingTool.ShortPosition, "\U0001F7E5", "DrawTool_ShortPosition"),
                    new DrawingToolInfo(DrawingTool.SineLine, "\u223D", "DrawTool_SineLine"),
                    new DrawingToolInfo(DrawingTool.TimeCycles, "\u23F1\uFE0F", "DrawTool_TimeCycles"),
                }),
        };
    }
}
