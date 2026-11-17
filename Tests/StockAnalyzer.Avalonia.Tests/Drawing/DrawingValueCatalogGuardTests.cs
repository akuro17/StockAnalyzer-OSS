using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Drawing;
using StockAnalyzer.Core.Drawing.Values;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

/// <summary>
/// Guards that the Core drawing-value catalog and the GUI drawing objects describe the same 24 tools:
/// every name is a <see cref="ChartObjectType"/>, and the catalog result equals what the GUI object returns.
/// </summary>
public sealed class DrawingValueCatalogGuardTests
{
    private static readonly Color ObjectColor = Color.FromArgb(255, 10, 20, 30);
    private static readonly DateTime T1 = new(2026, 1, 1);

    internal static readonly Dictionary<string, Func<ChartPoint[], IChartObject>> Factories = new(StringComparer.Ordinal)
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
        ["Pitchfork"] = p => new PitchforkObject(p[0], p[1], p[2]),
        ["AngleTool"] = p => new AngleObject(p[0], p[1]),
        ["GannSquareOfNine"] = p => new GannSquareOfNineObject(p[0]),
        ["TargetPriceProjection"] = p => new TargetPriceProjectionObject(p[0], p[1], p[2]),
        ["LongPosition"] = p => new LongShortPositionObject(p[0], p[1], p[2], isLong: true),
        ["ShortPosition"] = p => new LongShortPositionObject(p[0], p[1], p[2], isLong: false),
        ["RegressionTrend"] = p => new RegressionTrendObject(p[0], p[1]),
        ["AnchoredVWAP"] = p => new AnchoredVwapObject(p[0]),
        ["ClassicPivotPoints"] = p => new ClassicPivotPointsObject(p[0]),
    };

    /// <summary>Daily bars (one per calendar day, from 2025-11-01) with a wavy close, so the regression has a spread, the VWAP a deviation and the pivot periods differ from each other.</summary>
    internal static readonly List<CoreCandleData> Candles = Enumerable.Range(0, 150)
        .Select(day =>
        {
            decimal close = 100m + day * 0.3m + Math.Round((decimal)Math.Sin(day / 4.0) * 5m, 2);
            return new CoreCandleData(new DateTime(2025, 11, 1).AddDays(day), close - 0.2m, close + 0.9m, close - 0.8m, close, 1000 + 7L * day);
        })
        .ToList();

    /// <summary>The state the chart gives a bar-based object before it is asked for values (the chart calls Recalculate with its bars).</summary>
    internal static void Prepare(IChartObject obj)
    {
        switch (obj)
        {
            case RegressionTrendObject regression: regression.Recalculate(Candles); break;
            case AnchoredVwapObject vwap: vwap.Recalculate(Candles); break;
            case ClassicPivotPointsObject pivots: pivots.Recalculate(Candles); break;
        }
    }

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
                foreach (PitchforkKind kind in Enum.GetValues<PitchforkKind>())
                {
                    foreach (decimal? price in new decimal?[] { null, 123.456m })
                    {
                        foreach (DateTime timestamp in new[] { T1.AddDays(5.5), T1.AddDays(-7), T1.AddDays(20.75) })
                        {
                            IChartObject obj = Factories[descriptor.Name](points.Take(descriptor.PointCount).ToArray());
                            try
                            {
                                obj.Color = ObjectColor;
                                if (obj is LongShortPositionObject position)
                                {
                                    // The catalog gives the three items the colour of the tool; the chart object has a colour of its own for the target and the stop.
                                    position.TargetColor = ObjectColor;
                                    position.StopColor = ObjectColor;
                                }

                                Prepare(obj);
                                if (obj is FibonacciRetracementObject retracement)
                                {
                                    retracement.ShowExtensions = showExtensions;
                                }

                                if (obj is PitchforkObject fork)
                                {
                                    fork.PitchforkType = (PitchforkType)(int)kind;
                                }

                                var request = new DrawingValueRequest(
                                    points.Take(descriptor.PointCount).Select(p => p.ToAnchorForTest()).ToArray(),
                                    timestamp, price, objectColor, new DrawingValueOptions(showExtensions, kind), Candles);

                                IReadOnlyList<DrawingCalculatedValue> gui = ((IDrawingCalculatedValuesProvider)obj).GetCalculatedValues(timestamp, price);
                                IReadOnlyList<DrawingCalculatedValue> core = descriptor.Calculate(request);

                                Assert.True(gui.SequenceEqual(core), $"{descriptor.Name} ts={timestamp:o} cp={price} ext={showExtensions} kind={kind}");
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

    public static TheoryData<string, string> CandleToolOptions() => new()
    {
        { "RegressionTrend", "multiplier 1.5, extend right" },
        { "RegressionTrend", "multiplier 0.1, not extended" },
        { "AnchoredVWAP", "both bands" },
        { "AnchoredVWAP", "band 1 only, close" },
        { "AnchoredVWAP", "band 2 only, weighted" },
        { "ClassicPivotPoints", "week, extend both" },
        { "ClassicPivotPoints", "month, extend left" },
    };

    [Theory]
    [MemberData(nameof(CandleToolOptions))]
    public void CandleToolOptions_GiveTheSameValuesAsTheGuiObjectWithTheSameSettings(string toolName, string setting)
    {
        Assert.True(DrawingValueCatalog.TryGet(toolName, out DrawingValueDescriptor descriptor));
        var points = new[] { new ChartPoint(new DateTime(2025, 12, 5), 100m), new ChartPoint(new DateTime(2026, 2, 20), 150m) };
        var objectColor = new IndicatorColor(ObjectColor.A, ObjectColor.R, ObjectColor.G, ObjectColor.B);
        IChartObject obj = Factories[toolName](points.Take(descriptor.PointCount).ToArray());
        try
        {
            obj.Color = ObjectColor;
            DrawingValueOptions options;
            switch (obj)
            {
                case RegressionTrendObject regression when setting.StartsWith("multiplier 1.5"):
                    regression.StdDevMultiplier = 1.5m;
                    regression.ExtendRight = true;
                    options = new DrawingValueOptions(StdDevMultiplier: 1.5m, ExtendRight: true);
                    break;
                case RegressionTrendObject regression:
                    regression.StdDevMultiplier = 0.1m;
                    options = new DrawingValueOptions(StdDevMultiplier: 0.1m);
                    break;
                case AnchoredVwapObject vwap when setting == "both bands":
                    vwap.ShowBand1 = true;
                    vwap.Band1Multiplier = 1.5m;
                    vwap.ShowBand2 = true;
                    vwap.Band2Multiplier = 3m;
                    options = new DrawingValueOptions(ShowBand1: true, Band1Multiplier: 1.5m, ShowBand2: true, Band2Multiplier: 3m);
                    break;
                case AnchoredVwapObject vwap when setting.StartsWith("band 1 only"):
                    vwap.ShowBand1 = true;
                    vwap.PriceSource = PriceType.Close;
                    options = new DrawingValueOptions(ShowBand1: true, PriceSource: PriceType.Close);
                    break;
                case AnchoredVwapObject vwap:
                    vwap.ShowBand2 = true;
                    vwap.PriceSource = PriceType.Weighted;
                    options = new DrawingValueOptions(ShowBand2: true, PriceSource: PriceType.Weighted);
                    break;
                case ClassicPivotPointsObject pivots when setting.StartsWith("week"):
                    pivots.Period = PivotPeriod.Week;
                    pivots.ExtendMode = PivotLineExtendMode.ExtendBoth;
                    options = new DrawingValueOptions(PivotPeriod: PivotPeriod.Week, PivotExtendMode: PivotLineExtendMode.ExtendBoth);
                    break;
                case ClassicPivotPointsObject pivots:
                    pivots.Period = PivotPeriod.Month;
                    pivots.ExtendMode = PivotLineExtendMode.ExtendLeft;
                    options = new DrawingValueOptions(PivotPeriod: PivotPeriod.Month, PivotExtendMode: PivotLineExtendMode.ExtendLeft);
                    break;
                default:
                    throw new InvalidOperationException(setting);
            }

            Prepare(obj);
            foreach (DateTime timestamp in new[] { new DateTime(2025, 12, 20), new DateTime(2026, 1, 15), new DateTime(2026, 2, 20), new DateTime(2026, 3, 30) })
            {
                var request = new DrawingValueRequest(points.Take(descriptor.PointCount).Select(p => p.ToAnchorForTest()).ToArray(), timestamp, 123.456m, objectColor, options, Candles);

                IReadOnlyList<DrawingCalculatedValue> gui = ((IDrawingCalculatedValuesProvider)obj).GetCalculatedValues(timestamp, 123.456m);
                IReadOnlyList<DrawingCalculatedValue> core = descriptor.Calculate(request);

                Assert.NotEmpty(core);
                Assert.True(gui.SequenceEqual(core), $"{toolName} ({setting}) ts={timestamp:o}");
            }
        }
        finally
        {
            (obj as IDisposable)?.Dispose();
        }
    }
}

internal static class ChartPointTestExtensions
{
    // DrawingAnchorExtensions.ToAnchor is internal to StockAnalyzer.Avalonia; the test builds the anchor itself.
    public static DrawingAnchor ToAnchorForTest(this ChartPoint point) => new(point.Time, point.Price);
}
