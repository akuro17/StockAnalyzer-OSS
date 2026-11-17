using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Serialization;
using StockAnalyzer.Core.Drawing;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Drawing;
using StockAnalyzer.Core.ReadOnly;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

/// <summary>
/// The saved drawings as <c>sa_get_chart_drawings</c> reads them: real chart objects are captured and written by the application's own codec, read back by the read-only
/// Core reader, and calculated by the Core catalog from what was saved. The values must equal what the chart object itself shows. This is the end-to-end proof that the saved type
/// names and parameter names the Core side relies on are the ones the application writes.
/// </summary>
public sealed class SavedDrawingValuesRoundTripTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sa_saved_drawings_" + Guid.NewGuid().ToString("N"));

    public SavedDrawingValuesRoundTripTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static ChartPoint P(int dayOffset, decimal price) => new(new DateTime(2025, 12, 1).AddDays(dayOffset), price);

    [Fact]
    public void EveryStoredTypeName_IsTheClassNameOfTheChartObject_TheCodecResolves()
    {
        var points = new[] { P(0, 100m), P(10, 150m), P(7, 130m) };

        foreach (DrawingValueDescriptor descriptor in DrawingValueCatalog.All)
        {
            IChartObject obj = DrawingValueCatalogGuardTests.Factories[descriptor.Name](points.Take(descriptor.PointCount).ToArray());
            try
            {
                Assert.Equal(descriptor.StoredTypeName, obj.GetType().Name);
                Assert.Equal(obj.GetType(), ChartObjectTypeRegistry.Resolve(descriptor.StoredTypeName));
            }
            finally
            {
                (obj as IDisposable)?.Dispose();
            }
        }
    }

    [Fact]
    public void BothPositionTools_ShareOneClass_AndTheSavedTypeParameterTellsThemApart()
    {
        var positions = new IChartObject[]
        {
            new LongShortPositionObject(P(0, 100m), P(1, 95m), P(2, 110m), isLong: true),
            new LongShortPositionObject(P(0, 100m), P(1, 105m), P(2, 90m), isLong: false),
        };
        var manager = new ChartObjectManager();
        foreach (IChartObject position in positions)
        {
            manager.AddObject(position);
        }

        DrawingDocumentState state = DrawingDocumentCodec.Capture(manager, new DrawingDocumentKey("7203", TimeframeType.Daily), 1);

        DrawingObjectRecord[] records = state.Contexts.Single(c => c.ContextName == "Standard").Objects.ToArray();
        Assert.All(records, r => Assert.Equal("LongShortPositionObject", r.TypeName));
        Assert.Equal(new[] { "LongPosition", "ShortPosition" }, records.Select(r => ReadSavedTool(r)).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.False(DrawingValueCatalog.TryGetStored("LongShortPositionObject", null, out _));
        Assert.True(DrawingValueCatalog.TryGetStored("LongShortPositionObject", "ShortPosition", out DrawingValueDescriptor shortTool));
        Assert.Equal("ShortPosition", shortTool.Name);
    }

    [Fact]
    public void TheOptionsTheImageReads_AreSavedUnderTheNamesTheCoreReaderExpects()
    {
        // sa_render_chart_image draws a saved trend line with its extend mode, a channel with its channel mode, and skips an object the user hid.
        var trend = new TrendLineObject(P(0, 100m), P(10, 110m)) { ExtendMode = LineExtendMode.Right, IsVisible = false };
        var channel = new ParallelChannelObject(P(0, 100m), P(10, 110m), P(5, 120m)) { ChannelMode = ChannelMode.FlatOpposite };
        var plain = new TrendLineObject(P(0, 100m), P(10, 110m));
        var manager = new ChartObjectManager();
        manager.AddObject(trend);
        manager.AddObject(channel);
        manager.AddObject(plain);

        DrawingDocumentState state = DrawingDocumentCodec.Capture(manager, new DrawingDocumentKey("7203", TimeframeType.Daily), 1);

        IReadOnlyList<DrawingObjectRecord> records = state.Contexts.Single(c => c.ContextName == DrawingContextNames.Standard).Objects;
        DrawingObjectRecord[] trends = records.Where(r => r.TypeName == nameof(TrendLineObject)).ToArray();
        Assert.Equal(2, trends.Length);
        var hiddenParameters = ParametersAfterTheFile(trends.Single(r => !DrawingStoredOptions.IsVisible(ParametersAfterTheFile(r))));
        var plainParameters = ParametersAfterTheFile(trends.Single(r => DrawingStoredOptions.IsVisible(ParametersAfterTheFile(r))));
        Assert.Equal(LineExtendMode.Right, DrawingStoredOptions.ReadLineExtendMode(hiddenParameters));
        Assert.Equal(LineExtendMode.None, DrawingStoredOptions.ReadLineExtendMode(plainParameters));
        Assert.Equal(ChannelMode.FlatOpposite, DrawingStoredOptions.ReadChannelMode(ParametersAfterTheFile(records.Single(r => r.TypeName == nameof(ParallelChannelObject)))));
        Assert.True(DrawingValueCatalog.TryGetStored(nameof(ParallelChannelObject), null, out DrawingValueDescriptor descriptor));
        Assert.Equal("ParallelChannel", descriptor.Name);

        (trend as IDisposable)?.Dispose();
        (channel as IDisposable)?.Dispose();
        (plain as IDisposable)?.Dispose();
    }

    /// <summary>The parameters of a record as the reader sees them: after a trip through the JSON of the file.</summary>
    private static IReadOnlyDictionary<string, object?> ParametersAfterTheFile(DrawingObjectRecord record)
    {
        string json = System.Text.Json.JsonSerializer.Serialize(record.StyleAndParameters, DrawingDocumentCodec.SerializerOptions);
        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(json);
        return document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
    }

    private static string? ReadSavedTool(DrawingObjectRecord record)
    {
        // The reader works on JSON, so look at the record as it is after a trip through the file.
        string json = System.Text.Json.JsonSerializer.Serialize(record.StyleAndParameters, DrawingDocumentCodec.SerializerOptions);
        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty(DrawingStoredOptions.ToolKey, out System.Text.Json.JsonElement type) ? type.GetString() : null;
    }

    [Fact]
    public async Task SavedChartObjects_AreCalculatedFromTheFile_ExactlyAsTheChartObjectsShowThem()
    {
        var vwapAnchor = P(20, 1m);
        var objects = new List<IChartObject>
        {
            new TrendLineObject(P(0, 100m), P(30, 130m)),
            new FibonacciRetracementObject(P(0, 100m), P(40, 140m)) { ShowExtensions = true },
            new PitchforkObject(P(0, 100m), P(20, 140m), P(10, 110m)) { PitchforkType = PitchforkType.Schiff },
            new AngleObject(P(0, 100m), P(30, 130m)),
            new GannSquareOfNineObject(P(5, 100m)),
            new TargetPriceProjectionObject(P(0, 100m), P(20, 150m), P(10, 130m)) { ShowEqualPercentage = false },
            new LongShortPositionObject(P(10, 100m), P(10, 95m), P(10, 110m), isLong: true),
            new LongShortPositionObject(P(10, 100m), P(10, 105m), P(10, 90m), isLong: false),
            new RegressionTrendObject(P(10, 100m), P(60, 100m)) { StdDevMultiplier = 1.5m, ExtendRight = true },
            new AnchoredVwapObject(vwapAnchor) { ShowBand1 = true, Band1Multiplier = 1.5m, PriceSource = PriceType.Close },
            new AnchoredVwapObject(vwapAnchor) { ShowBand2 = true, Band2Multiplier = 3m, PriceSource = PriceType.Weighted },
            new ClassicPivotPointsObject(P(50, 1m)) { Period = PivotPeriod.Week, ExtendMode = PivotLineExtendMode.ExtendBoth },
            new ClassicPivotPointsObject(P(50, 1m)) { Period = PivotPeriod.Month },
        };
        var manager = new ChartObjectManager();
        foreach (IChartObject obj in objects)
        {
            DrawingValueCatalogGuardTests.Prepare(obj);
            manager.AddObject(obj);
        }

        DrawingDocumentState captured = DrawingDocumentCodec.Capture(manager, new DrawingDocumentKey("7203", TimeframeType.Daily), 1);
        File.WriteAllText(Path.Combine(_root, "7203-T.Daily.json"), DrawingDocumentCodec.SerializeToJson(captured));

        DrawingDocumentReadResult read = await new FileReadOnlyDrawingDocumentSource(_root).ReadAsync("7203.T", TimeFrame.D1);

        Assert.Equal(DrawingDocumentReadStatus.Read, read.Status);
        DrawingObjectRecord[] records = read.Document!.Value.Contexts.Single(c => c.ContextName == "Standard").Objects.ToArray();
        Assert.Equal(objects.Count, records.Length);

        DateTime evaluation = new DateTime(2026, 2, 20);
        const decimal currentPrice = 123.456m;
        for (int i = 0; i < records.Length; i++)
        {
            DrawingObjectRecord record = records[i];
            Assert.True(
                DrawingValueCatalog.TryGetStored(record.TypeName, DrawingStoredOptions.ReadTool(record.StyleAndParameters), out DrawingValueDescriptor descriptor),
                $"{record.TypeName} is not a catalogued tool");
            Assert.Equal(DrawingCoordinateKind.UtcTime, record.CoordinateKind);
            Assert.Equal(descriptor.PointCount, record.Points.Count);

            var request = new DrawingValueRequest(
                record.Points.Select(point => new DrawingAnchor(point.UtcTime!.Value, point.YValue)).ToArray(),
                evaluation,
                currentPrice,
                IndicatorColor.Gray,
                DrawingStoredOptions.Read(record.StyleAndParameters),
                DrawingValueCatalogGuardTests.Candles);

            var fromFile = descriptor.Calculate(request);
            var fromObject = ((IDrawingCalculatedValuesProvider)objects[i]).GetCalculatedValues(evaluation, currentPrice);

            Assert.NotEmpty(fromObject);
            Assert.Equal(
                fromObject.Select(v => (v.Key, v.Label, v.NumericValue, v.FormattedText)).ToArray(),
                fromFile.Select(v => (v.Key, v.Label, v.NumericValue, v.FormattedText)).ToArray());
        }

        foreach (IChartObject obj in objects)
        {
            (obj as IDisposable)?.Dispose();
        }
    }
}
