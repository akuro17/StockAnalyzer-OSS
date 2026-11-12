using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Drawing;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

/// <summary>
/// Guards that the Core drawing-value catalog and the GUI drawing objects describe the same 15 tools:
/// every name is a <see cref="ChartObjectType"/>, and the catalog result equals what the GUI object returns.
/// </summary>
public sealed class DrawingValueCatalogGuardTests
{
    private static readonly Color ObjectColor = Color.FromArgb(255, 10, 20, 30);
    private static readonly DateTime T1 = new(2026, 1, 1);

    private static readonly Dictionary<string, Func<ChartPoint[], IChartObject>> Factories = new(StringComparer.Ordinal)
    {
        ["TrendLine"] = p => new TrendLineObject(p[0], p[1]),
        ["Ray"] = p => new RayObject(p[0], p[1]),
        ["HorizontalLine"] = p => new HorizontalLineObject(p[0]),
        ["VerticalLine"] = p => new VerticalLineObject(p[0]),
        ["Arrow"] = p => new ArrowObject(p[0], p[1]),
        ["PriceLabel"] = p => new PriceLabelObject(p[0]),
        ["FibonacciRetracement"] = p => new FibonacciRetracementObject(p[0], p[1]),
        ["FibonacciFan"] = p => new FibonacciFanObject(p[0], p[1]),
        ["FibonacciExpansion"] = p => new FibonacciExpansionObject(p[0], p[1], p[2]),
        ["FibonacciChannel"] = p => new FibonacciChannelObject(p[0], p[1], p[2]),
        ["ParallelChannel"] = p => new ParallelChannelObject(p[0], p[1], p[2]),
        ["GannFan"] = p => new GannFanObject(p[0], p[1]),
        ["GannBox"] = p => new GannBoxObject(p[0], p[1]),
        ["SineLine"] = p => new SineLineObject(p[0], p[1]),
        ["TimeCycles"] = p => new TimeCyclesObject(p[0], p[1]),
    };

    [Fact]
    public void EveryCatalogName_IsAChartObjectType_WhoseGuiObjectProvidesCalculatedValues()
    {
        Assert.Equal(DrawingValueCatalog.All.Count, Factories.Count);
        var points = new[] { new ChartPoint(T1, 100m), new ChartPoint(T1.AddDays(10), 150m), new ChartPoint(T1.AddDays(7), 130m) };

        foreach (DrawingValueDescriptor descriptor in DrawingValueCatalog.All)
        {
            Assert.True(Enum.TryParse(descriptor.Name, ignoreCase: false, out ChartObjectType type), descriptor.Name);
            IChartObject obj = Factories[descriptor.Name](points);
            try
            {
                Assert.Equal(type, obj.Type);
                Assert.IsAssignableFrom<IDrawingCalculatedValuesProvider>(obj);
                // PriceLabelObject also keeps a label-position point; the catalog reads only the first PointCount points.
                Assert.True(obj.Points.Count >= descriptor.PointCount, descriptor.Name);
            }
            finally
            {
                (obj as IDisposable)?.Dispose();
            }
        }
    }

    [Fact]
    public void CatalogResult_EqualsTheGuiObjectResult_ForEveryToolTimestampAndOption()
    {
        var points = new[] { new ChartPoint(T1, 100m), new ChartPoint(T1.AddDays(10), 150m), new ChartPoint(T1.AddDays(7), 130m) };
        var objectColor = new IndicatorColor(ObjectColor.A, ObjectColor.R, ObjectColor.G, ObjectColor.B);

        foreach (DrawingValueDescriptor descriptor in DrawingValueCatalog.All)
        {
            foreach (bool showExtensions in new[] { false, true })
            {
                foreach (decimal? price in new decimal?[] { null, 123.456m })
                {
                    foreach (DateTime timestamp in new[] { T1.AddDays(5.5), T1.AddDays(-7), T1.AddDays(20.75) })
                    {
                        IChartObject obj = Factories[descriptor.Name](points.Take(descriptor.PointCount).ToArray());
                        try
                        {
                            obj.Color = ObjectColor;
                            if (obj is FibonacciRetracementObject retracement)
                            {
                                retracement.ShowExtensions = showExtensions;
                            }

                            var request = new DrawingValueRequest(
                                points.Take(descriptor.PointCount).Select(p => p.ToAnchorForTest()).ToArray(),
                                timestamp, price, objectColor, new DrawingValueOptions(showExtensions));

                            IReadOnlyList<DrawingCalculatedValue> gui = ((IDrawingCalculatedValuesProvider)obj).GetCalculatedValues(timestamp, price);
                            IReadOnlyList<DrawingCalculatedValue> core = descriptor.Calculate(request);

                            Assert.True(gui.SequenceEqual(core), $"{descriptor.Name} ts={timestamp:o} cp={price} ext={showExtensions}");
                        }
                        finally
                        {
                            (obj as IDisposable)?.Dispose();
                        }
                    }
                }
            }
        }
    }
}

internal static class ChartPointTestExtensions
{
    // DrawingAnchorExtensions.ToAnchor is internal to StockAnalyzer.Avalonia; the test builds the anchor itself.
    public static DrawingAnchor ToAnchorForTest(this ChartPoint point) => new(point.Time, point.Price);
}
