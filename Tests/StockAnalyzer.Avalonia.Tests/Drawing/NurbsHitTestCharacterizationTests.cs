using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Avalonia;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Models;
using StockAnalyzer.TestSupport;
using static StockAnalyzer.TestSupport.CharacterizationSupport;
using Xunit;
using Xunit.Abstractions;
using Point = Avalonia.Point;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

/// <summary>
/// Characterization of <c>HitTest</c> of the four NURBS curve objects (arc, hyperbola, parabola, trend curve).
/// The expected rows were captured before the sampling loop and the segment distance were moved into
/// <see cref="NurbsMath"/> and must never be edited. One row = one object/shape/tolerance, one character per probe
/// point (1 = hit, 0 = miss) in row-major order of the probe grid.
/// </summary>
public class NurbsHitTestCharacterizationTests
{
    private static readonly double[] ProbeXs = { -10, 0, 25, 50, 75, 100, 110 };
    private static readonly double[] ProbeYs = { -10, 0, 25, 50, 75, 100, 110 };
    private static readonly double[] Tolerances = { 0, 3, 8 };

    private readonly ITestOutputHelper _output;

    public NurbsHitTestCharacterizationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static ChartPoint P(double x, double y) => PixelCoordinateTransform.Chart(x, y);

    private static string Row(string label, Func<Point, double, bool> hit, double tolerance)
    {
        var sb = new StringBuilder();
        foreach (double x in ProbeXs)
        {
            foreach (double y in ProbeYs)
            {
                sb.Append(hit(new Point(x, y), tolerance) ? '1' : '0');
            }
        }

        return $"{label}|tol={tolerance.ToString(CultureInfo.InvariantCulture)}|{sb}";
    }

    private static List<string> Capture()
    {
        var lines = new List<string>();
        var transform = new PixelCoordinateTransform();
        ChartPoint a = P(0, 0), b = P(50, 100), c = P(100, 0);

        foreach (double weight in new[] { 0.3, 0.5, 0.9 })
        {
            var arc = new NurbsConicArcObject(a, b, c) { Weight = weight };
            foreach (double tol in Tolerances)
            {
                lines.Add(Row($"arc|w={weight.ToString(CultureInfo.InvariantCulture)}", (pt, t) => arc.HitTest(pt, transform, t), tol));
            }
        }

        foreach (double weight in new[] { 1.5, 2.0, 5.0 })
        {
            var hyperbola = new NurbsHyperbolaObject(a, b, c) { Weight = weight };
            foreach (double tol in Tolerances)
            {
                lines.Add(Row($"hyperbola|w={weight.ToString(CultureInfo.InvariantCulture)}", (pt, t) => hyperbola.HitTest(pt, transform, t), tol));
            }
        }

        var parabola = new NurbsParabolaObject(a, b, c);
        foreach (double tol in Tolerances)
        {
            lines.Add(Row("parabola", (pt, t) => parabola.HitTest(pt, transform, t), tol));
        }

        var trend4 = new NurbsTrendCurveObject(new[] { P(0, 0), P(30, 80), P(70, 80), P(100, 0) });
        var trend2 = new NurbsTrendCurveObject(new[] { P(0, 0), P(100, 100) });
        var many = new List<ChartPoint>();
        for (int i = 0; i < 129; i++)
        {
            many.Add(P(2 * i, (i % 5) * 10));
        }

        var trend129 = new NurbsTrendCurveObject(many);

        foreach (double tol in Tolerances)
        {
            lines.Add(Row("trend4", (pt, t) => trend4.HitTest(pt, transform, t), tol));
            lines.Add(Row("trend2", (pt, t) => trend2.HitTest(pt, transform, t), tol));
            lines.Add(Row("trend129", (pt, t) => trend129.HitTest(pt, transform, t), tol));
        }

        return lines;
    }

    [Fact]
    public void NurbsHitTest_MatchesCapturedRows()
    {
        var actual = Capture();

        AssertMatches(Expected, actual, _output);
    }

    private static readonly string[] Expected =
    {
        "arc|w=0.3|tol=0|0000000010000000000000000000000000001000000000000",
        "arc|w=0.3|tol=3|0000000010000000000000010000000000001000000000000",
        "arc|w=0.3|tol=8|0000000010000000100000010000001000001000000000000",
        "arc|w=0.5|tol=0|0000000010000000000000000000000000001000000000000",
        "arc|w=0.5|tol=3|0000000010000000100000000000001000001000000000000",
        "arc|w=0.5|tol=8|0000000010000000100000010000001000001000000000000",
        "arc|w=0.9|tol=0|0000000010000000000000000000000000001000000000000",
        "arc|w=0.9|tol=3|0000000010000000000000001000000000001000000000000",
        "arc|w=0.9|tol=8|0000000010000000100000001000001000001000000000000",
        "hyperbola|w=1.5|tol=0|0000000010000000000000000000000000001000000000000",
        "hyperbola|w=1.5|tol=3|0000000010000000000000000000000000001000000000000",
        "hyperbola|w=1.5|tol=8|0000000010000000010000000000000100001000000000000",
        "hyperbola|w=2|tol=0|0000000010000000000000000000000000001000000000000",
        "hyperbola|w=2|tol=3|0000000010000000010000000000000100001000000000000",
        "hyperbola|w=2|tol=8|0000000010000000010000000100000100001000000000000",
        "hyperbola|w=5|tol=0|0000000010000000010000000000000100001000000000000",
        "hyperbola|w=5|tol=3|0000000010000000010000000000000100001000000000000",
        "hyperbola|w=5|tol=8|0000000010000000010000000100000100001000000000000",
        "parabola|tol=0|0000000010000000000000001000000000001000000000000",
        "parabola|tol=3|0000000010000000000000001000000000001000000000000",
        "parabola|tol=8|0000000010000000100000001000001000001000000000000",
        "trend4|tol=0|0000000010000000000000000000000000001000000000000",
        "trend2|tol=0|0000000010000000100000001000000010000000100000000",
        "trend129|tol=0|0000000010000000100000000000001000000000000000000",
        "trend4|tol=3|0000000010000000010000000000000100001000000000000",
        "trend2|tol=3|0000000010000000100000001000000010000000100000000",
        "trend129|tol=3|0000000010000000100000010000001000000100000010000",
        "trend4|tol=8|0000000010000000010000000000000100001000000000000",
        "trend2|tol=8|0000000010000000100000001000000010000000100000000",
        "trend129|tol=8|0000000011000000100000110000011000001100000110000",
    };
}
