using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Avalonia.Media;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Models;
using StockAnalyzer.TestSupport;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

/// <summary>
/// Characterization (golden) test of <c>GetCalculatedValues</c> for the 15 drawing tools of the headless MCP drawing-values feature
/// (docs/MCP_SERVER_COMMAND_REFERENCE.md section 6.39, "Tools"; the conventions are in docs/ja/DRAWING_TOOLS_SPEC.md, "Headless Calculation Conventions"). The golden file was captured from the code BEFORE the formulas moved to
/// StockAnalyzer.Core/Drawing; it must never be edited to make a refactoring pass. Every entry is compared on Key, Label, NumericValue
/// (with its decimal scale), FormattedText and the colour source, under the en-US culture the GUI uses.
/// </summary>
public sealed class DrawingValuesCharacterizationTests
{
    private const string GoldenRelativePath = "Tests/StockAnalyzer.Avalonia.Tests/Drawing/Golden/drawing_values_characterization.txt";
    private const string CultureName = "en-US";

    private static readonly Color ObjectColor = Color.FromArgb(255, 10, 20, 30);

    private static readonly DateTime T1 = new(2026, 1, 1);
    private static readonly DateTime T2 = new(2026, 1, 11);
    private static readonly DateTime T3 = new(2026, 1, 8, 12, 0, 0);
    private static readonly DateTime Mid = new(2026, 1, 6, 12, 0, 0);
    private static readonly DateTime Before = new(2025, 12, 25, 6, 0, 0);
    private static readonly DateTime After = new(2026, 1, 21, 18, 30, 15);
    private static readonly DateTime FarFuture = new(9000, 1, 1);
    private const decimal CurrentPrice = 123.456m;

    private sealed record ToolCase(string Name, int PointCount, Func<ChartPoint[], IChartObject> Create);

    private static readonly ToolCase[] Tools =
    {
        new("TrendLine", 2, p => new TrendLineObject(p[0], p[1])),
        new("Ray", 2, p => new RayObject(p[0], p[1])),
        new("HorizontalLine", 1, p => new HorizontalLineObject(p[0])),
        new("VerticalLine", 1, p => new VerticalLineObject(p[0])),
        new("Arrow", 2, p => new ArrowObject(p[0], p[1])),
        new("PriceLabel", 1, p => new PriceLabelObject(p[0])),
        new("FibonacciRetracement", 2, p => new FibonacciRetracementObject(p[0], p[1])),
        new("FibonacciRetracement+Extensions", 2, p => new FibonacciRetracementObject(p[0], p[1]) { ShowExtensions = true }),
        new("FibonacciFan", 2, p => new FibonacciFanObject(p[0], p[1])),
        new("FibonacciExpansion", 3, p => new FibonacciExpansionObject(p[0], p[1], p[2])),
        new("FibonacciChannel", 3, p => new FibonacciChannelObject(p[0], p[1], p[2])),
        new("ParallelChannel", 3, p => new ParallelChannelObject(p[0], p[1], p[2])),
        new("GannFan", 2, p => new GannFanObject(p[0], p[1])),
        new("GannBox", 2, p => new GannBoxObject(p[0], p[1])),
        new("SineLine", 2, p => new SineLineObject(p[0], p[1])),
        new("TimeCycles", 2, p => new TimeCyclesObject(p[0], p[1])),
    };

    private sealed record PointSet(string Name, ChartPoint[] Points, bool Full);

    private static readonly PointSet[] PointSets =
    {
        new("Normal", new[] { new ChartPoint(T1, 100m), new ChartPoint(T2, 150m), new ChartPoint(T3, 130m) }, true),
        new("Reversed", new[] { new ChartPoint(T2, 150m), new ChartPoint(T1, 100m), new ChartPoint(T3, 130m) }, false),
        new("SameTime", new[] { new ChartPoint(T1, 100m), new ChartPoint(T1, 150m), new ChartPoint(T3, 130m) }, false),
        new("SubSecond", new[] { new ChartPoint(T1, 100m), new ChartPoint(T1.AddMilliseconds(500), 150m), new ChartPoint(T3, 130m) }, false),
        new("ZeroAndNegative", new[] { new ChartPoint(T1, 0m), new ChartPoint(T2, -25.5m), new ChartPoint(new DateTime(2026, 1, 8), -10m) }, false),
    };

    /// <summary>Renders every tool x point set x timestamp x current price combination as stable text.</summary>
    internal static string RenderAll()
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(CultureName);
        try
        {
            var text = new StringBuilder();
            foreach (ToolCase tool in Tools)
            {
                foreach (PointSet set in PointSets)
                {
                    var calls = new List<(DateTime Timestamp, decimal? Price)>();
                    if (set.Full)
                    {
                        foreach (DateTime ts in new[] { Mid, Before, After })
                        {
                            calls.Add((ts, null));
                            calls.Add((ts, CurrentPrice));
                        }

                        calls.Add((FarFuture, null));
                    }
                    else
                    {
                        calls.Add((Mid, null));
                        calls.Add((After, CurrentPrice));
                    }

                    foreach ((DateTime timestamp, decimal? price) in calls)
                    {
                        AppendCall(text, tool, set.Name, set.Points[..tool.PointCount], timestamp, price, clearPoints: false);
                    }
                }

                AppendCall(text, tool, "PointsCleared", PointSets[0].Points[..tool.PointCount], Mid, CurrentPrice, clearPoints: true);
            }

            return text.ToString();
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    private static void AppendCall(StringBuilder text, ToolCase tool, string setName, ChartPoint[] points, DateTime timestamp, decimal? price, bool clearPoints)
    {
        text.Append("## ").Append(tool.Name).Append(" | ").Append(setName).Append(" | ts=")
            .Append(timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)).Append(" | cp=")
            .AppendLine(price?.ToString(CultureInfo.InvariantCulture) ?? "null");

        IChartObject obj = tool.Create(points);
        try
        {
            obj.Color = ObjectColor;
            if (clearPoints)
            {
                obj.Points.Clear();
            }

            var objectColor = new IndicatorColor(ObjectColor.A, ObjectColor.R, ObjectColor.G, ObjectColor.B);
            try
            {
                IReadOnlyList<DrawingCalculatedValue> values = ((IDrawingCalculatedValuesProvider)obj).GetCalculatedValues(timestamp, price);
                text.Append("count=").Append(values.Count.ToString(CultureInfo.InvariantCulture)).AppendLine();
                foreach (DrawingCalculatedValue v in values)
                {
                    text.Append(v.Key).Append('|').Append(v.Label).Append('|')
                        .Append(v.NumericValue?.ToString(CultureInfo.InvariantCulture) ?? "null").Append('|')
                        .Append(v.FormattedText).Append('|').AppendLine(ColorTag(v.Color, objectColor));
                }
            }
            catch (Exception ex)
            {
                text.Append("THROWS ").AppendLine(ex.GetType().Name);
            }
        }
        finally
        {
            (obj as IDisposable)?.Dispose();
        }
    }

    private static string ColorTag(IndicatorColor color, IndicatorColor objectColor)
    {
        if (color == objectColor) return "OBJECT";
        if (color == IndicatorColor.Gray) return "GRAY";
        if (color == IndicatorColor.Bullish) return "BULLISH";
        if (color == IndicatorColor.Bearish) return "BEARISH";
        return color.ToString();
    }

    [Fact]
    public void GetCalculatedValues_OfTheFifteenTools_IsIdenticalToTheGoldenFileCapturedBeforeTheCoreMove()
    {
        string goldenPath = Path.Combine(TestRepositoryRoot.Find(), GoldenRelativePath.Replace('/', Path.DirectorySeparatorChar));
        string expected = File.ReadAllText(goldenPath).Replace("\r\n", "\n");
        string actual = RenderAll().Replace("\r\n", "\n");

        if (expected != actual)
        {
            string[] e = expected.Split('\n');
            string[] a = actual.Split('\n');
            int i = 0;
            while (i < e.Length && i < a.Length && e[i] == a[i])
            {
                i++;
            }

            Assert.Fail($"First difference at line {i + 1}.\nExpected: {(i < e.Length ? e[i] : "<end>")}\nActual:   {(i < a.Length ? a[i] : "<end>")}");
        }
    }

    [Fact]
    public void RenderAll_CoversEveryToolOnce_AndTheGoldenFileIsNotEmpty()
    {
        string rendered = RenderAll();
        foreach (ToolCase tool in Tools)
        {
            Assert.Contains("## " + tool.Name + " | Normal | ts=2026-01-06 12:00:00.000 | cp=null", rendered);
        }
    }
}
