using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Avalonia;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.TestSupport;
using static StockAnalyzer.TestSupport.CharacterizationSupport;
using Xunit;
using Xunit.Abstractions;
using Point = Avalonia.Point;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

/// <summary>
/// Characterization of <c>HitTest</c> of <see cref="PolylineObject"/> and <see cref="CurveLineTextObject"/> (straight and smooth).
/// The expected rows were captured before the two loops were moved into <see cref="BezierSplineMath"/> and must never be edited.
/// One row = one object/shape/tolerance, one character per probe point (1 = hit, 0 = miss) in row-major order of the probe grid.
/// </summary>
public class PolylineHitTestCharacterizationTests
{
    private static readonly double[] ProbeXs = { -5, 0, 25, 50, 75, 100, 125, 150, 155, 200, 256, 260 };
    private static readonly double[] ProbeYs = { -5, 0, 20, 40, 60, 65 };
    private static readonly double[] Tolerances = { 0, 4, 10 };

    private readonly ITestOutputHelper _output;

    public PolylineHitTestCharacterizationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static List<ChartPoint> Shape(string name)
    {
        var points = new List<ChartPoint>();
        switch (name)
        {
            case "two":
                points.Add(PixelCoordinateTransform.Chart(0, 0));
                points.Add(PixelCoordinateTransform.Chart(100, 0));
                break;
            case "four":
                points.Add(PixelCoordinateTransform.Chart(0, 0));
                points.Add(PixelCoordinateTransform.Chart(50, 60));
                points.Add(PixelCoordinateTransform.Chart(100, 0));
                points.Add(PixelCoordinateTransform.Chart(150, 60));
                break;
            case "many":
                for (int i = 0; i < 129; i++)
                {
                    points.Add(PixelCoordinateTransform.Chart(2 * i, (i % 5) * 10));
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(name));
        }

        return points;
    }

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

        foreach (string shape in new[] { "two", "four", "many" })
        {
            foreach (bool smooth in new[] { false, true })
            {
                var polyline = new PolylineObject(Shape(shape)) { IsSmooth = smooth };
                var curve = new CurveLineTextObject(Shape(shape)) { IsSmooth = smooth };
                foreach (double tol in Tolerances)
                {
                    lines.Add(Row($"polyline|{shape}|smooth={smooth}", (pt, t) => polyline.HitTest(pt, transform, t), tol));
                    lines.Add(Row($"curvetext|{shape}|smooth={smooth}", (pt, t) => curve.HitTest(pt, transform, t), tol));
                }
            }
        }

        return lines;
    }

    [Fact]
    public void PolylineHitTest_MatchesCapturedRows()
    {
        var actual = Capture();

        AssertMatches(Expected, actual, _output);
    }

    private static readonly string[] Expected =
    {
        "polyline|two|smooth=False|tol=0|000000010000010000010000010000010000000000000000000000000000000000000000",
        "curvetext|two|smooth=False|tol=0|000000010000010000010000010000010000000000000000000000000000000000000000",
        "polyline|two|smooth=False|tol=4|000000010000010000010000010000010000000000000000000000000000000000000000",
        "curvetext|two|smooth=False|tol=4|000000010000010000010000010000010000000000000000000000000000000000000000",
        "polyline|two|smooth=False|tol=10|110000110000110000110000110000110000000000000000000000000000000000000000",
        "curvetext|two|smooth=False|tol=10|110000110000110000110000110000110000000000000000000000000000000000000000",
        "polyline|two|smooth=True|tol=0|000000010000010000010000010000010000000000000000000000000000000000000000",
        "curvetext|two|smooth=True|tol=0|000000010000010000010000010000010000000000000000000000000000000000000000",
        "polyline|two|smooth=True|tol=4|000000010000010000010000010000010000000000000000000000000000000000000000",
        "curvetext|two|smooth=True|tol=4|000000010000010000010000010000010000000000000000000000000000000000000000",
        "polyline|two|smooth=True|tol=10|110000110000110000110000110000110000000000000000000000000000000000000000",
        "curvetext|two|smooth=True|tol=10|110000110000110000110000110000110000000000000000000000000000000000000000",
        "polyline|four|smooth=False|tol=0|000000010000000000000010000000010000000000000010000000000000000000000000",
        "curvetext|four|smooth=False|tol=0|000000010000000000000010000000010000000000000010000000000000000000000000",
        "polyline|four|smooth=False|tol=4|000000010000000000000010000000010000000000000010000000000000000000000000",
        "curvetext|four|smooth=False|tol=4|000000010000000000000010000000010000000000000010000000000000000000000000",
        "polyline|four|smooth=False|tol=10|110000110000001100000011001100110000001100000011000011000000000000000000",
        "curvetext|four|smooth=False|tol=10|110000110000001100000011001100110000001100000011000011000000000000000000",
        "polyline|four|smooth=True|tol=0|000000010000000000000010000000010000000000000010000000000000000000000000",
        "curvetext|four|smooth=True|tol=0|000000010000000000000010000000010000000000000010000000000000000000000000",
        "polyline|four|smooth=True|tol=4|000000010000000100000010000000010000001000000010000000000000000000000000",
        "curvetext|four|smooth=True|tol=4|000000010000000100000010000000010000001000000010000000000000000000000000",
        "polyline|four|smooth=True|tol=10|110000110000001100000011001100110000001100000011000011000000000000000000",
        "curvetext|four|smooth=True|tol=10|110000110000001100000011001100110000001100000011000011000000000000000000",
        "polyline|many|smooth=False|tol=0|000000010000000000010000000000010000000000010000000000010000000000000000",
        "curvetext|many|smooth=False|tol=0|000000010000000000010000000000010000000000010000000000010000000000000000",
        "polyline|many|smooth=False|tol=4|000000011000001100011100001100011100001100011100001100011100001000000000",
        "curvetext|many|smooth=False|tol=4|000000011000001100011100001100011100001100011100001100011100001000000000",
        "polyline|many|smooth=False|tol=10|111000111100111100111100111100111100111100111100111100111100111100011000",
        "curvetext|many|smooth=False|tol=10|111000111100111100111100111100111100111100111100111100111100111100011000",
        "polyline|many|smooth=True|tol=0|000000010000000000010000000000010000000000010000000000010000000000000000",
        "curvetext|many|smooth=True|tol=0|000000010000000000010000000000010000000000010000000000010000000000000000",
        "polyline|many|smooth=True|tol=4|000000011000001100011100001100011100001100011100001100011100001000000000",
        "curvetext|many|smooth=True|tol=4|000000011000001100011100001100011100001100011100001100011100001000000000",
        "polyline|many|smooth=True|tol=10|111000111100111100111100111100111100111100111100111100111100111100011000",
        "curvetext|many|smooth=True|tol=10|111000111100111100111100111100111100111100111100111100111100111100011000",
    };
}
